using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public interface ITalearrSourceGapFill
    {
        // Fills what Hardcover lacks in a payload from Talearr's source. Returns the number of
        // books given a release year.
        int FillMissingDates(V5AuthorResponse response);
    }

    // Talearr: Hardcover is thin for small and self-published authors: many books have no
    // release date on the book or any edition, and metadata profiles with "Skip missing release
    // date" drop them. Open Library often has a first publish year for the same title. One author
    // lookup (skipped when Hardcover links the Open Library author) and one works list per author,
    // only when at least one book has no date at all; results are cached by the Open Library
    // client. Matching is by normalized title (full, then the part before a subtitle), and only a
    // single unambiguous year is used.
    public class TalearrSourceGapFill : ITalearrSourceGapFill
    {
        public const int MaxWorks = 200;

        private readonly IOpenLibraryClient _openLibrary;
        private readonly Logger _logger;

        public TalearrSourceGapFill(IOpenLibraryClient openLibrary, Logger logger)
        {
            _openLibrary = openLibrary;
            _logger = logger;
        }

        public int FillMissingDates(V5AuthorResponse response)
        {
            var missing = (response?.Books ?? new List<V5Book>())
                .Where(b => b != null && !HasAnyDate(b))
                .ToList();

            if (missing.Count == 0 || response.Author == null)
            {
                return 0;
            }

            try
            {
                // The mapper stores Hardcover's link as "ol:OL123A"; the client wants the bare key.
                var linked = response.Author.OpenLibraryAuthorId;
                var colon = linked?.IndexOf(':') ?? -1;
                var authorKey = (colon >= 0 ? linked.Substring(colon + 1) : linked) ?? FindAuthorKey(response.Author.Name);
                if (authorKey == null)
                {
                    _logger.Debug("[TALEARR-GAPFILL] No Open Library author for {0}; {1} books stay without a date", response.Author.Name, missing.Count);
                    return 0;
                }

                var works = _openLibrary.GetAuthorWorks(authorKey, MaxWorks) ?? new List<OpenLibraryWorkDoc>();
                var filled = FillFromWorks(missing, works);

                _logger.Debug("[TALEARR-GAPFILL] {0}: {1} of {2} undated books dated from Open Library ({3} works)",
                    response.Author.Name, filled, missing.Count, works.Count);

                return filled;
            }
            catch (OpenLibraryException ex)
            {
                _logger.Debug(ex, "[TALEARR-GAPFILL] Open Library unavailable for {0}", response.Author.Name);
                return 0;
            }
        }

        internal static bool HasAnyDate(V5Book book)
        {
            return book.PublicationDate.HasValue ||
                   book.PublicationYear.HasValue ||
                   (book.Editions ?? new List<V5Edition>()).Any(e => e?.PublicationDate != null);
        }

        internal static int FillFromWorks(IEnumerable<V5Book> books, IEnumerable<OpenLibraryWorkDoc> works)
        {
            var byTitle = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var work in works ?? Enumerable.Empty<OpenLibraryWorkDoc>())
            {
                if (work?.FirstPublishYear == null || work.FirstPublishYear.Value < 1000)
                {
                    continue;
                }

                foreach (var key in TitleKeys(work.Title, work.Subtitle))
                {
                    if (!byTitle.TryGetValue(key, out var years))
                    {
                        years = new HashSet<int>();
                        byTitle[key] = years;
                    }

                    years.Add(work.FirstPublishYear.Value);
                }
            }

            var filled = 0;
            foreach (var book in books ?? Enumerable.Empty<V5Book>())
            {
                foreach (var key in TitleKeys(book.Title, book.Subtitle))
                {
                    if (byTitle.TryGetValue(key, out var years) && years.Count == 1)
                    {
                        book.PublicationYear = years.First();
                        filled++;
                        break;
                    }
                }
            }

            return filled;
        }

        // Full title (with subtitle when it is separate) first, then the main title alone.
        internal static IEnumerable<string> TitleKeys(string title, string subtitle)
        {
            var keys = new List<string>();
            void Add(string value)
            {
                var key = Normalize(value);
                if (key.Length >= 3 && !keys.Contains(key))
                {
                    keys.Add(key);
                }
            }

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                Add($"{title} {subtitle}");
            }

            Add(title);

            var main = title ?? string.Empty;
            var cut = main.IndexOfAny(new[] { ':', '(', '[' });
            if (cut > 0)
            {
                Add(main.Substring(0, cut));
            }

            return keys;
        }

        internal static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var space = false;
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    space = false;
                }
                else if (!space && builder.Length > 0 && c != '\'' && c != '’')
                {
                    builder.Append(' ');
                    space = true;
                }
            }

            var result = builder.ToString().Trim();
            foreach (var article in new[] { "the ", "a ", "an " })
            {
                if (result.StartsWith(article, StringComparison.Ordinal))
                {
                    result = result.Substring(article.Length);
                    break;
                }
            }

            return result;
        }

        private string FindAuthorKey(string name)
        {
            var wanted = Normalize(name);
            if (wanted.Length == 0)
            {
                return null;
            }

            return (_openLibrary.SearchAuthors(name, 5) ?? new List<OpenLibraryAuthorDoc>())
                .Where(a => a != null && Normalize(a.Name) == wanted)
                .OrderByDescending(a => a.WorkCount)
                .Select(a => a.Key)
                .FirstOrDefault();
        }
    }
}
