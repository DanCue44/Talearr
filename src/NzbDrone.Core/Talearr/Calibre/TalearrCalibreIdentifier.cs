using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.Talearr.Calibre
{
    public enum TalearrCalibreMatchMethod
    {
        // The file sits in the Calibre library at <books.path>/<data.name>.<format>.
        LibraryPath = 0,

        // The file's own ISBN (tags or file name) names exactly one Calibre book with a usable format.
        Isbn = 1
    }

    // What Calibre says about one file. Either Book is set (identified) or AmbiguousCount > 1
    // (several Calibre books fit, so nothing is used and the file is left for review).
    public sealed class TalearrCalibreIdentification
    {
        public string FilePath { get; set; }
        public TalearrCalibreBook Book { get; set; }
        public TalearrCalibreMatchMethod Method { get; set; }
        public string Isbn { get; set; }
        public int AmbiguousCount { get; set; }

        public bool IsMatch => Book != null;

        public string AuthorText => Book?.Authors?.Count > 0 ? string.Join(", ", Book.Authors) : "an unknown author";

        public string PreferredIsbn => Isbn ?? Book?.Isbns?.FirstOrDefault();

        // e.g. "Identified from Calibre (library path): Dune by Frank Herbert, ISBN 9780441013593, language eng"
        public string Describe()
        {
            if (!IsMatch)
            {
                return $"Calibre has {AmbiguousCount} books with ISBN {Isbn}, so Calibre was not used. Choose the book yourself.";
            }

            var how = Method == TalearrCalibreMatchMethod.LibraryPath ? "library path" : "ISBN";
            var text = $"Identified from Calibre ({how}): {Book.Title} by {AuthorText}";
            if (PreferredIsbn != null)
            {
                text += $", ISBN {PreferredIsbn}";
            }

            if (Book.Languages?.Count > 0)
            {
                text += $", language {string.Join(", ", Book.Languages)}";
            }

            return text;
        }
    }

    public interface ITalearrCalibreIdentifier
    {
        bool IsEnabled { get; }

        // Per file path (ebooks only): what Calibre knows. Files Calibre does not know are left
        // out. Never throws; a missing or unreadable library returns an empty result.
        Dictionary<string, TalearrCalibreIdentification> Identify(IEnumerable<DiscoveredFileWithMetadata> files);
    }

    // Talearr: optional, read-only Calibre lookup for ebooks in Retry Match and Manual Import
    // (issue #4). Calibre only supplies better identification evidence (author, title, ISBN,
    // language); the configured metadata provider still finds the edition.
    public class TalearrCalibreIdentifier : ITalearrCalibreIdentifier
    {
        private readonly ITalearrCalibreSettingsService _settingsService;
        private readonly Logger _logger;

        public TalearrCalibreIdentifier(ITalearrCalibreSettingsService settingsService, Logger logger)
        {
            _settingsService = settingsService;
            _logger = logger;
        }

        public bool IsEnabled => LibraryPath() != null;

        public Dictionary<string, TalearrCalibreIdentification> Identify(IEnumerable<DiscoveredFileWithMetadata> files)
        {
            var result = new Dictionary<string, TalearrCalibreIdentification>(PathEqualityComparer.Instance);
            var library = LibraryPath();
            if (library == null)
            {
                return result;
            }

            var ebooks = (files ?? Enumerable.Empty<DiscoveredFileWithMetadata>())
                .Where(file => !string.IsNullOrWhiteSpace(file?.Path) && MediaFileExtensions.TextExtensions.Contains(Path.GetExtension(file.Path)))
                .GroupBy(file => file.Path, PathEqualityComparer.Instance)
                .Select(group => group.First())
                .ToList();

            if (ebooks.Count == 0)
            {
                return result;
            }

            var metadataPath = TalearrCalibreSettingsService.MetadataPath(library);
            if (!File.Exists(metadataPath))
            {
                _logger.Warn("[CALIBRE] No metadata.db at '{0}'; the Calibre lookup is skipped", metadataPath);
                return result;
            }

            try
            {
                using var reader = TalearrCalibreLibraryReader.Open(metadataPath);
                foreach (var file in ebooks)
                {
                    var identification = IdentifyOne(reader, library, file);
                    if (identification != null)
                    {
                        result[file.Path] = identification;
                        _logger.Debug("[CALIBRE] {0}: {1}", file.Path, identification.Describe());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[CALIBRE] Could not read the Calibre library at '{0}'; matching goes on without it", library);
            }

            return result;
        }

        internal static TalearrCalibreIdentification IdentifyOne(TalearrCalibreLibraryReader reader, string library, DiscoveredFileWithMetadata file)
        {
            var format = Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();

            // 1. A file inside the Calibre library maps by its relative path and format.
            var relative = RelativeToLibrary(library, file.Path);
            if (relative != null)
            {
                var folder = relative.Contains('/') ? relative.Substring(0, relative.LastIndexOf('/')) : string.Empty;
                var name = Path.GetFileNameWithoutExtension(relative);
                var ids = reader.FindByFile(folder, name, format);
                if (ids.Count == 1)
                {
                    var book = reader.GetBook(ids[0]);
                    if (book != null)
                    {
                        return new TalearrCalibreIdentification
                        {
                            FilePath = file.Path,
                            Book = book,
                            Method = TalearrCalibreMatchMethod.LibraryPath
                        };
                    }
                }
            }

            // 2. Elsewhere (a download), the file's own ISBN must name exactly one Calibre book
            // that holds an ebook format; a book in the same format wins over other formats.
            foreach (var isbn in FileIsbns(file))
            {
                var candidates = reader.FindByIsbn(isbn)
                    .Select(reader.GetBook)
                    .Where(book => book != null && book.Formats.Any(f => IsEbookFormat(f.Format)))
                    .ToList();

                if (candidates.Count == 0)
                {
                    continue;
                }

                var sameFormat = candidates.Where(book => book.Formats.Any(f => string.Equals(f.Format, format, StringComparison.OrdinalIgnoreCase))).ToList();
                var pool = sameFormat.Count > 0 ? sameFormat : candidates;

                if (pool.Count == 1)
                {
                    return new TalearrCalibreIdentification
                    {
                        FilePath = file.Path,
                        Book = pool[0],
                        Method = TalearrCalibreMatchMethod.Isbn,
                        Isbn = isbn
                    };
                }

                return new TalearrCalibreIdentification
                {
                    FilePath = file.Path,
                    Isbn = isbn,
                    AmbiguousCount = pool.Count
                };
            }

            return null;
        }

        // The file's ISBNs as ISBN-13: from isbn-like tags, then from the file name.
        internal static List<string> FileIsbns(DiscoveredFileWithMetadata file)
        {
            var hints = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(file.ISBN))
            {
                values.Add(file.ISBN);
            }

            foreach (var pair in file.AllTags ?? new Dictionary<string, List<string>>())
            {
                if (pair.Key != null && pair.Key.IndexOf("isbn", StringComparison.OrdinalIgnoreCase) >= 0 && pair.Value != null)
                {
                    values.AddRange(pair.Value);
                }
            }

            values.Add(Path.GetFileNameWithoutExtension(file.Path));
            hints["isbn"] = values;

            TalearrSourceMatcher.ExtractIdentifiers(hints, out var isbns, out _);
            return isbns
                .Select(TalearrAbsItemReader.NormalizeIsbn)
                .Where(isbn => isbn != null)
                .Distinct()
                .ToList();
        }

        // The path below the library root with forward slashes, or null when the file is not inside it.
        internal static string RelativeToLibrary(string library, string filePath)
        {
            try
            {
                var root = Path.GetFullPath(library);
                var full = Path.GetFullPath(filePath);
                var relative = Path.GetRelativePath(root, full);
                if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                {
                    return null;
                }

                return relative.Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsEbookFormat(string format)
        {
            return !string.IsNullOrWhiteSpace(format) && MediaFileExtensions.TextExtensions.Contains("." + format);
        }

        private string LibraryPath()
        {
            try
            {
                var path = _settingsService?.Get()?.LibraryPath;
                return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
