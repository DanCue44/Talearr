using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: extra bundle and omnibus detection on top of ReleasePackDetector. Called from
    // ReleasePackDetector.Detect when the upstream patterns found nothing. Catches the
    // releases that slipped through on a real library:
    //   "1984 - The Complete Novels - George Orwell"
    //   "Walter Isaacson - Great Innovators (Benjamin Franklin; Einstein; Steve Jobs) (epub)"
    //   "John Grisham - Three Classic Thrillers (The Firm; The Appeal; The Chamber) (v5.0) (epub)"
    //   "R.F. Kuang 5 Books Collection Set"
    public static class TalearrBundleDetector
    {
        private static readonly (Regex Regex, string PackType)[] BundlePatterns =
        {
            (new Regex(@"\b(?:complete|collected|selected|classic)\s+(?:novels|works|stories|short\s+stories|fiction|thrillers|mysteries|books|series|saga)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "complete-works"),
            (new Regex(@"\b(?:[2-9]|1\d|20)[\s-]*(?:books?|novels|volumes?|titles)\b(?!\s*#?\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase), "book-count"),
            (new Regex(@"\b(?:two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)[\s-]+(?:books?|novels|volumes?|classic|great)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "book-count"),
            (new Regex(@"\b\d\s*-?\s*in\s*-?\s*1\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "n-in-1"),
            (new Regex(@"\b(?:duology|bundle|box[\s-]?set|boxset)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "named-pack"),
            (new Regex(@"\bcollection\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "collection")
        };

        private static readonly Regex ListSeparatorRegex = new Regex(@";|\s/\s|\s&\s|\s\+\s", RegexOptions.Compiled);
        private static readonly Regex NonWordRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled);
        private static readonly Regex SubtitleSplitRegex = new Regex(@"[:(\[]", RegexOptions.Compiled);

        private const int CatalogEntryCacheLimit = 50000;
        private static readonly ConcurrentDictionary<string, (bool IsBundle, string Primary)> CatalogEntryCache =
            new ConcurrentDictionary<string, (bool IsBundle, string Primary)>(StringComparer.Ordinal);

        // Titles shorter than this (after normalising) are too generic to count as list entries.
        private const int MinimumListTitleLength = 5;

        public static ReleasePackDetection Detect(string releaseTitle, Book targetBook, IEnumerable<Book> authorCatalog)
        {
            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return null;
            }

            var targetTitle = targetBook?.Title;

            foreach (var (regex, packType) in BundlePatterns)
            {
                var match = regex.Match(releaseTitle);
                if (!match.Success)
                {
                    continue;
                }

                // The wanted book is itself titled this way ("The Collection", a duology...).
                if (!string.IsNullOrWhiteSpace(targetTitle) && regex.IsMatch(targetTitle))
                {
                    continue;
                }

                return MultipleBooks(packType, match.Value);
            }

            return DetectCatalogTitleList(releaseTitle, targetBook, authorCatalog);
        }

        // True when a book's own title reads like a bundle (the user monitors an omnibus).
        public static bool LooksLikeBundleTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var upstream = ReleasePackDetector.Detect(title, null, null);
            if (upstream?.Verdict == ReleasePackDetectionVerdict.MultipleBooks)
            {
                return true;
            }

            return BundlePatterns.Any(pattern => pattern.Regex.IsMatch(title));
        }

        // Talearr: true when the release title names the book ("The Poppy War Box Set (Books 1-3)"
        // names "The Poppy War"). Titles shorter than three characters never count: they appear
        // inside too many unrelated release names.
        public static bool ReleaseMentionsTitle(string releaseTitle, string bookTitle)
        {
            var primary = Normalize(GetPrimaryTitle(bookTitle));
            if (primary.Length < 3 || string.IsNullOrWhiteSpace(releaseTitle))
            {
                return false;
            }

            var normalizedRelease = " " + Normalize(releaseTitle) + " ";
            if (!normalizedRelease.Contains(" " + primary + " ", StringComparison.Ordinal))
            {
                return false;
            }

            // Talearr: "All-in-One Bundle" does not name the book "All In". The title must appear
            // at least once outside a hyphenated compound, unless the release uses hyphens as its
            // only word separator ("Poppy-War-Box-Set").
            if (!HasHyphenCompound(releaseTitle))
            {
                return true;
            }

            var words = primary.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var pattern = @"(?<![\p{L}\p{N}])(?<!\p{L}-)" + string.Join(@"[^\p{L}\p{N}]+", words) + @"(?![\p{L}\p{N}])(?!-\p{L})";
            return Regex.IsMatch(LowerWithoutMarks(releaseTitle), pattern);
        }

        private static readonly Regex HyphenCompoundRegex = new Regex(@"\p{L}-\p{L}", RegexOptions.Compiled);
        private static readonly Regex OtherWordSeparatorRegex = new Regex(@"[\p{L}\p{N}][\s._]+[\p{L}\p{N}]", RegexOptions.Compiled);

        private static bool HasHyphenCompound(string releaseTitle)
        {
            return HyphenCompoundRegex.IsMatch(releaseTitle) && OtherWordSeparatorRegex.IsMatch(releaseTitle);
        }

        private static string LowerWithoutMarks(string value)
        {
            var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c != '\'')
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        // A release that lists the wanted book together with other books by the same author:
        // "Great Innovators (Benjamin Franklin; Einstein; Steve Jobs)".
        private static ReleasePackDetection DetectCatalogTitleList(string releaseTitle, Book targetBook, IEnumerable<Book> authorCatalog)
        {
            // Only an explicit list ("A; B; C", "A / B", "A & B") in a single-book search counts.
            // A series name that equals a book title ("Dungeon Crawler Carl - Carl's Doomsday
            // Scenario") is left to the title matcher.
            var catalog = authorCatalog?.Where(book => book != null).ToList();
            if (targetBook == null || catalog == null || catalog.Count < 2 || !ListSeparatorRegex.IsMatch(releaseTitle))
            {
                return null;
            }

            var normalizedRelease = " " + Normalize(releaseTitle) + " ";
            var targetPrimary = Normalize(GetPrimaryTitle(targetBook.Title));
            if (targetPrimary.Length == 0 || !normalizedRelease.Contains(" " + targetPrimary + " ", StringComparison.Ordinal))
            {
                return null;
            }

            var found = catalog
                .Where(book => book.Id != targetBook.Id || book.Id == 0)
                .Select(book => GetCatalogEntry(book.Title))
                .Where(entry => !entry.IsBundle)
                .Select(entry => entry.Primary)
                .Where(primary => primary.Length >= MinimumListTitleLength)
                .Where(primary => !primary.Contains(targetPrimary, StringComparison.Ordinal) &&
                                  !targetPrimary.Contains(primary, StringComparison.Ordinal))
                .Distinct()
                .Where(primary => normalizedRelease.Contains(" " + primary + " ", StringComparison.Ordinal))
                .ToList();

            // Drop titles that are only part of a longer title that was also found.
            found = found.Where(title => !found.Any(other => other != title && other.Contains(title, StringComparison.Ordinal))).ToList();

            if (found.Count >= 1)
            {
                return MultipleBooks("catalog-title-list", string.Join(" | ", found));
            }

            return null;
        }

        private static (bool IsBundle, string Primary) GetCatalogEntry(string title)
        {
            title ??= string.Empty;

            if (CatalogEntryCache.TryGetValue(title, out var cached))
            {
                return cached;
            }

            if (CatalogEntryCache.Count > CatalogEntryCacheLimit)
            {
                CatalogEntryCache.Clear();
            }

            var entry = (LooksLikeBundleTitle(title), Normalize(GetPrimaryTitle(title)));
            CatalogEntryCache[title] = entry;
            return entry;
        }

        private static ReleasePackDetection MultipleBooks(string packType, string matchedValue)
        {
            return new ReleasePackDetection
            {
                Verdict = ReleasePackDetectionVerdict.MultipleBooks,
                PackType = packType,
                MatchedValue = matchedValue
            };
        }

        private static string GetPrimaryTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var parts = SubtitleSplitRegex.Split(title, 2);
            return parts[0].Trim().Length > 0 ? parts[0] : title;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            var cleaned = NonWordRegex.Replace(builder.ToString().Replace("'", string.Empty), " ").Trim();

            if (cleaned.StartsWith("the ", StringComparison.Ordinal))
            {
                cleaned = cleaned.Substring(4);
            }

            return cleaned;
        }
    }
}
