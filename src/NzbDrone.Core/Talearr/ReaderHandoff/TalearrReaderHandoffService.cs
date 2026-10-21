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
        // A file already handed off unchanged is skipped unless force is set.
        List<string> HandOff(Author author, IEnumerable<BookFile> files, TalearrReaderHandoffSettings settings, bool force = false);

        TalearrReaderHandoffTestResult Test(TalearrReaderHandoffSettings settings);
    }

    // Talearr: reader hand-off. After an ebook from a download is imported, a copy goes to a
    // watch folder (for example Calibre-Web-Automated's ingest folder), optionally converted
    // to KEPUB. Always a copy: the library file is never moved or changed. Audiobooks are
    // skipped. Runs async so a slow disk or kepubify never holds up the import. Each copy is
    // logged in talearr.db (Chaptarr #130): a replayed import does not send the same file twice,
    // a name already in the folder is never overwritten by a different book, and failures stay
    // visible (System > Status) until they are retried.
    public class TalearrReaderHandoffService : ITalearrReaderHandoffService, IHandleAsync<BookImportedEvent>
    {
        public const string PartialSuffix = ".part";

        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrKepubify _kepubify;
        private readonly Logger _logger;
        private readonly ITalearrReaderHandoffStore _store;
        private readonly IEventAggregator _eventAggregator;

        public TalearrReaderHandoffService(ITalearrReaderHandoffSettingsService settingsService,
                                           ITalearrKepubify kepubify,
                                           Logger logger,
                                           ITalearrReaderHandoffStore store = null,
                                           IEventAggregator eventAggregator = null)
        {
            _settingsService = settingsService;
            _kepubify = kepubify;
            _logger = logger;
            _store = store;
            _eventAggregator = eventAggregator;
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
                _logger.Warn(ex, "Talearr: reader hand-off failed for '{0}'", message?.Book?.Title);
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

        public List<string> HandOff(Author author, IEnumerable<BookFile> files, TalearrReaderHandoffSettings settings, bool force = false)
        {
            var written = new List<string>();

            if (settings == null || !settings.IsActive)
            {
                return written;
            }

            var ebookFiles = (files ?? Enumerable.Empty<BookFile>())
                .Where(IsEbook)
                .GroupBy(f => f.Path, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (ebookFiles.Count == 0)
            {
                return written;
            }

            if (!Directory.Exists(settings.Path.Trim()))
            {
                var error = $"Reader hand-off folder '{settings.Path}' does not exist";
                ebookFiles.ForEach(f => RecordFailure(f, error));
                AnnounceChanges();
                throw new DirectoryNotFoundException(error);
            }

            var folder = TargetFolder(settings, author);
            Directory.CreateDirectory(folder);

            string kepubify = null;
            if (settings.ConvertToKepub && ebookFiles.Any(f => IsConvertibleEpub(f.Path)))
            {
                kepubify = _kepubify.FindExecutable();
                if (kepubify == null)
                {
                    _logger.Warn("Talearr: Convert to KEPUB is on but kepubify was not found on PATH; copying the EPUB as it is");
                }
            }

            foreach (var file in ebookFiles)
            {
                var source = file.Path;

                if (!File.Exists(source))
                {
                    _logger.Warn("Talearr: reader hand-off skipped '{0}': the imported file is missing", source);
                    RecordFailure(file, "The library file is missing");
                    continue;
                }

                var size = new FileInfo(source).Length;
                var previous = file.Id > 0 ? _store?.Get(file.Id) : null;

                if (!force && previous != null && previous.IsDeliveredCopyOf(source, size))
                {
                    _logger.Debug("Talearr: '{0}' was already handed to the reader folder; not sending it again", Path.GetFileName(source));
                    continue;
                }

                try
                {
                    var destination = CopyOne(source, folder, kepubify);
                    if (destination != null)
                    {
                        written.Add(destination);
                        RecordDelivery(file, size, destination);
                        _logger.Info("Talearr: handed '{0}' to the reader folder as '{1}'", Path.GetFileName(source), destination);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Talearr: reader hand-off of '{0}' failed", Path.GetFileName(source));
                    RecordFailure(file, ex.Message, previous);
                }
            }

            AnnounceChanges();

            return written;
        }

        // Two files are treated as the same book when size and SHA-256 match.
        public static bool SameContent(string first, string second)
        {
            var a = new FileInfo(first);
            var b = new FileInfo(second);

            if (!a.Exists || !b.Exists || a.Length != b.Length)
            {
                return false;
            }

            using var sha = System.Security.Cryptography.SHA256.Create();
            using var streamA = a.OpenRead();
            var hashA = sha.ComputeHash(streamA);
            using var streamB = b.OpenRead();
            var hashB = sha.ComputeHash(streamB);

            return hashA.AsSpan().SequenceEqual(hashB);
        }

        // "Book.epub", then "Book (2).epub" and so on: a name already in the folder is never reused.
        public static string FreeName(string destination)
        {
            if (!File.Exists(destination) && !File.Exists(PartialPathFor(destination)))
            {
                return destination;
            }

            var folder = Path.GetDirectoryName(destination) ?? string.Empty;
            var name = Path.GetFileName(destination);
            var extension = name.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase) ? ".kepub.epub" : Path.GetExtension(name);
            var stem = name.Substring(0, name.Length - extension.Length);

            for (var n = 2; n < 1000; n++)
            {
                var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
                if (!File.Exists(candidate) && !File.Exists(PartialPathFor(candidate)))
                {
                    return candidate;
                }
            }

            return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
        }

        private void RecordDelivery(BookFile file, long size, string destination)
        {
            if (_store == null || file.Id <= 0)
            {
                return;
            }

            _store.Save(new TalearrReaderHandoffDelivery
            {
                BookFileId = file.Id,
                BookId = BookIdOf(file),
                SourcePath = file.Path,
                SourceSize = size,
                DeliveredPath = destination,
                State = TalearrReaderHandoffStates.Delivered,
                Attempts = 0,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private void RecordFailure(BookFile file, string error, TalearrReaderHandoffDelivery previous = null)
        {
            if (_store == null || file == null || file.Id <= 0)
            {
                return;
            }

            previous ??= _store.Get(file.Id);

            _store.Save(new TalearrReaderHandoffDelivery
            {
                BookFileId = file.Id,
                BookId = BookIdOf(file) is var bookId && bookId > 0 ? bookId : previous?.BookId ?? 0,
                SourcePath = file.Path,
                SourceSize = previous?.SourceSize ?? 0,
                DeliveredPath = null,
                State = TalearrReaderHandoffStates.Failed,
                Attempts = (previous?.State == TalearrReaderHandoffStates.Failed ? previous.Attempts : 0) + 1,
                LastError = error,
                UpdatedAt = DateTime.UtcNow
            });
        }

        private static int BookIdOf(BookFile file)
        {
            try
            {
                return file.Edition?.BookId ?? 0;
            }
            catch (Exception)
            {
                // The edition is lazy loaded; a book id is only a convenience for the status list.
                return 0;
            }
        }

        private void AnnounceChanges()
        {
            _eventAggregator?.PublishEvent(new TalearrReaderHandoffDeliveriesChangedEvent());
        }

        private string CopyOne(string source, string folder, string kepubify)
        {
            if (kepubify != null && IsConvertibleEpub(source))
            {
                var kepubDestination = FreeName(Path.Combine(folder, KepubFileName(source)));
                var partial = PartialPath(kepubDestination);
                var error = _kepubify.Convert(kepubify, source, partial);

                if (error == null && File.Exists(partial))
                {
                    File.Move(partial, kepubDestination, true);
                    return kepubDestination;
                }

                TryDelete(partial);
                _logger.Warn("Talearr: KEPUB conversion of '{0}' failed ({1}); copying the EPUB as it is", Path.GetFileName(source), error ?? "no output");
            }

            var destination = Path.Combine(folder, Path.GetFileName(source));

            if (File.Exists(destination) && SameContent(source, destination))
            {
                // Still in the folder from an earlier hand-off (the reader has not picked it up yet).
                return destination;
            }

            destination = FreeName(destination);
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
            return PartialPathFor(destination);
        }

        private static string PartialPathFor(string destination)
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
                _logger.Debug(ex, "Talearr: could not remove '{0}'", path);
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
                result.Errors.Add($"Folder '{path}' does not exist or Talearr cannot see it. Check the container's volume mappings.");
            }
            else
            {
                var probe = Path.Combine(path, ".talearr-write-test-" + Guid.NewGuid().ToString("N") + PartialSuffix);

                try
                {
                    File.WriteAllText(probe, "Talearr write test");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    TryDelete(probe);
                    result.Errors.Add($"Talearr cannot write to '{path}': {ex.Message}");
                }
            }

            if (settings?.ConvertToKepub == true && _kepubify.FindExecutable() == null)
            {
                result.Warnings.Add("Convert to KEPUB is on but kepubify was not found on PATH. EPUBs will be copied unconverted until it is installed.");
            }

            result.Success = result.Errors.Count == 0;
            result.Message = result.Success
                ? $"Talearr can write to '{path}'."
                : string.Join(" ", result.Errors);

            return result;
        }
    }
}
