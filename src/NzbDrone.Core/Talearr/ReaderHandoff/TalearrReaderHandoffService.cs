using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.Talearr.ReaderHandoff
{
    public sealed class TalearrReaderHandoffTestResult
    {
        public bool Success { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public string Message { get; set; }
    }

    public interface ITalearrReaderHandoffService
    {
        // Copies the ebook files of one import to the watch folder. Returns the written paths.
        List<string> HandOff(Author author, IEnumerable<BookFile> files, TalearrReaderHandoffSettings settings);

        TalearrReaderHandoffTestResult Test(TalearrReaderHandoffSettings settings);
    }

    // TaleArr: reader hand-off. After an ebook from a download is imported, a copy goes to a
    // watch folder (for example Calibre-Web-Automated's ingest folder), optionally converted
    // to KEPUB. Always a copy: the library file is never moved or changed. Audiobooks are
    // skipped. Runs async so a slow disk or kepubify never holds up the import.
    public class TalearrReaderHandoffService : ITalearrReaderHandoffService, IHandleAsync<BookImportedEvent>
    {
        public const string PartialSuffix = ".part";

        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrKepubify _kepubify;
        private readonly Logger _logger;

        public TalearrReaderHandoffService(ITalearrReaderHandoffSettingsService settingsService,
                                           ITalearrKepubify kepubify,
                                           Logger logger)
        {
            _settingsService = settingsService;
            _kepubify = kepubify;
            _logger = logger;
        }

        public void HandleAsync(BookImportedEvent message)
        {
            try
            {
                var settings = _settingsService.Get();

                // Only imports from a download, like the connections' On Release Import. A disk
                // scan of an existing library must not flood the reader with old books.
                if (message?.Book == null || !settings.IsActive || !message.NewDownload)
                {
                    return;
                }

                HandOff(message.Author ?? message.Book.Author, message.ImportedBooks, settings);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: reader hand-off failed for '{0}'", message?.Book?.Title);
            }
        }

        public static bool IsEbook(BookFile file)
        {
            var path = file?.Path;
            return !string.IsNullOrWhiteSpace(path) &&
                   MediaFileExtensions.TextExtensions.Contains(Path.GetExtension(path));
        }

        public static bool IsConvertibleEpub(string path)
        {
            return path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase);
        }

        public static string TargetFolder(TalearrReaderHandoffSettings settings, Author author)
        {
            var folder = settings.Path.Trim();

            if (settings.AuthorFolder)
            {
                var name = FileNameBuilder.CleanFolderName(FileNameBuilder.CleanFileName(author?.Name ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    folder = Path.Combine(folder, name);
                }
            }

            return folder;
        }

        public static string KepubFileName(string epubPath)
        {
            return Path.GetFileNameWithoutExtension(epubPath) + ".kepub.epub";
        }

        public List<string> HandOff(Author author, IEnumerable<BookFile> files, TalearrReaderHandoffSettings settings)
        {
            var written = new List<string>();

            if (settings == null || !settings.IsActive)
            {
                return written;
            }

            var ebooks = (files ?? Enumerable.Empty<BookFile>())
                .Where(IsEbook)
                .Select(f => f.Path)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ebooks.Count == 0)
            {
                return written;
            }

            if (!Directory.Exists(settings.Path.Trim()))
            {
                throw new DirectoryNotFoundException($"Reader hand-off folder '{settings.Path}' does not exist");
            }

            var folder = TargetFolder(settings, author);
            Directory.CreateDirectory(folder);

            string kepubify = null;
            if (settings.ConvertToKepub && ebooks.Any(IsConvertibleEpub))
            {
                kepubify = _kepubify.FindExecutable();
                if (kepubify == null)
                {
                    _logger.Warn("TaleArr: Convert to KEPUB is on but kepubify was not found on PATH; copying the EPUB as it is");
                }
            }

            foreach (var source in ebooks)
            {
                if (!File.Exists(source))
                {
                    _logger.Warn("TaleArr: reader hand-off skipped '{0}': the imported file is missing", source);
                    continue;
                }

                var destination = CopyOne(source, folder, kepubify);
                if (destination != null)
                {
                    written.Add(destination);
                    _logger.Info("TaleArr: handed '{0}' to the reader folder as '{1}'", Path.GetFileName(source), destination);
                }
            }

            return written;
        }

        private string CopyOne(string source, string folder, string kepubify)
        {
            if (kepubify != null && IsConvertibleEpub(source))
            {
                var kepubDestination = Path.Combine(folder, KepubFileName(source));
                var partial = PartialPath(kepubDestination);
                var error = _kepubify.Convert(kepubify, source, partial);

                if (error == null && File.Exists(partial))
                {
                    File.Move(partial, kepubDestination, true);
                    return kepubDestination;
                }

                TryDelete(partial);
                _logger.Warn("TaleArr: KEPUB conversion of '{0}' failed ({1}); copying the EPUB as it is", Path.GetFileName(source), error ?? "no output");
            }

            var destination = Path.Combine(folder, Path.GetFileName(source));
            var temp = PartialPath(destination);

            try
            {
                // Copy under a hidden .part name first, so a watcher never ingests half a file.
                File.Copy(source, temp, true);
                File.Move(temp, destination, true);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }

            return destination;
        }

        private static string PartialPath(string destination)
        {
            return Path.Combine(Path.GetDirectoryName(destination) ?? string.Empty, "." + Path.GetFileName(destination) + PartialSuffix);
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not remove '{0}'", path);
            }
        }

        public TalearrReaderHandoffTestResult Test(TalearrReaderHandoffSettings settings)
        {
            var result = new TalearrReaderHandoffTestResult();
            var path = settings?.Path?.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                result.Errors.Add("Set the watch folder first.");
            }
            else if (!Path.IsPathRooted(path))
            {
                result.Errors.Add($"'{path}' is not an absolute path.");
            }
            else if (!Directory.Exists(path))
            {
                result.Errors.Add($"Folder '{path}' does not exist or TaleArr cannot see it. Check the container's volume mappings.");
            }
            else
            {
                var probe = Path.Combine(path, ".talearr-write-test-" + Guid.NewGuid().ToString("N") + PartialSuffix);

                try
                {
                    File.WriteAllText(probe, "TaleArr write test");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    TryDelete(probe);
                    result.Errors.Add($"TaleArr cannot write to '{path}': {ex.Message}");
                }
            }

            if (settings?.ConvertToKepub == true && _kepubify.FindExecutable() == null)
            {
                result.Warnings.Add("Convert to KEPUB is on but kepubify was not found on PATH. EPUBs will be copied unconverted until it is installed.");
            }

            result.Success = result.Errors.Count == 0;
            result.Message = result.Success
                ? $"TaleArr can write to '{path}'."
                : string.Join(" ", result.Errors);

            return result;
        }
    }
}
