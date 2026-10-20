using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Catalog
{
    // Talearr: the author page groups (docs/TALEARR_AUTHOR_PAGE.md). Every book row of an author
    // is either one of the author's main works or an "other listing" with one reason. The groups
    // only steer the author page "All" view: nothing is stored, hidden or removed.
    public static class TalearrAuthorPageGroup
    {
        public const string Main = "main";
        public const string Omnibus = "omnibus";
        public const string Guide = "guide";
        public const string Adaptation = "adaptation";
        public const string Duplicate = "duplicate";
        public const string OtherLanguage = "otherLanguage";
    }

    // One book row as the classifier sees it. Title is the title the page shows (the monitored
    // edition's title), Language a canonical language bucket or null when unknown.
    public sealed class TalearrAuthorPageEntry
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public BookMediaType MediaType { get; set; }

        // Monitored, has files, added by hand, or pinned to an edition: always main.
        public bool IsKept { get; set; }
        public bool IsOmnibus { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public string Language { get; set; }
        public int Votes { get; set; }
        public DateTime? ReleaseDate { get; set; }

        // Provider work ids (hc:, gr:, ol:). Rows of the same media type that share one are
        // listings of the same book even when their titles differ.
        public List<string> WorkIds { get; set; } = new List<string>();
    }

    // The rules, in order (the first that applies wins):
    //   1. Kept rows (monitored, with files, manual adds, pinned editions) are always main.
    //   2. Title rules, per row: guide (study guides, summaries, graded readers), then omnibus
    //      (Book.IsOmnibus, box sets, collections, "Complete Works", "N Books", "Volumes 1-3",
    //      two titles joined by " / "), then adaptation ("Graphic Novel", manga, Classics
    //      Illustrated).
    //   3. Same-book rows: the remaining rows of one media type are grouped by normalized title
    //      (author name prefix, brackets, edition words and articles removed) and shared work ids.
    //      In a group:
    //        - a graphic-novel-genre row next to a prose row of the same book is an adaptation;
    //        - a row in a known language outside the preferred languages is otherLanguage when
    //          the group has a row in a preferred (or unknown) language (such a row also joins
    //          a group on its title before any bracket, colon or dash);
    //        - one row is the representative (kept first, then most rating votes, then the
    //          earliest release, then the lowest id) and stays main; the others are duplicates.
    //          Kept rows are main even when they are not the representative.
    public static class TalearrAuthorPageClassifier
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        private static readonly Regex[] GuidePatterns =
        {
            new Regex(@"\byork\s+notes\b", Options),
            new Regex(@"\bspark\s*notes\b", Options),
            new Regex(@"\bcliff'?s?\s*notes\b", Options),
            new Regex(@"\bcoles\s+notes\b", Options),
            new Regex(@"\bmax\s*notes\b", Options),
            new Regex(@"\bsupersummary\b", Options),
            new Regex(@"\bgradesaver\b", Options),
            new Regex(@"\bbookrags\b", Options),
            new Regex(@"\bstudy\s+(guide|notes|companion)\b", Options),
            new Regex(@"\b(literature|reader'?s|teacher'?s|teaching|discussion)\s+guide\b", Options),
            new Regex(@"\bsummary\b", Options),
            new Regex(@"\banalysis\s+of\b", Options),
            // "Brodie's Notes on 1984", but not a title that starts with it ("Notes on a Scandal").
            new Regex(@"\S.*\bnotes\s+on\b", Options),
            new Regex(@"\bworkbook\b", Options),

            // Graded readers: "Level 4: 1984", "Penguin Readers Level 3", "Oxford Bookworms".
            new Regex(@"^\s*level\s+\d+\b", Options),
            new Regex(@"\b(penguin|pearson|macmillan|cambridge)\s+(english\s+)?readers?\b", Options),
            new Regex(@"\boxford\s+bookworms\b", Options),
            new Regex(@"\bgraded\s+readers?\b", Options),
        };

        private static readonly Regex[] OmnibusPatterns =
        {
            new Regex(@"\bcollection\b", Options),
            new Regex(@"\bomnibus\b", Options),
            new Regex(@"\bbox(ed)?[\s-]*set\b", Options),
            new Regex(@"\bbooks?\s+set\b", Options),
            new Regex(@"\b(complete|collected)\s+(works|novels|stories|short\s+stories|fiction|series)\b", Options),
            new Regex(@"\b(\d+|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)\s+(books|novels|novellas)\b", Options),
            new Regex(@"\b(volumes?|vols?\.?|books?)\s+\d+\s*(-|–|to|through|and|&)\s*\d+\b", Options),

            // Two titles joined by a slash: "1984 / Animal Farm".
            new Regex(@"\S\s+/\s+\S", Options),
        };

        private static readonly Regex[] AdaptationPatterns =
        {
            new Regex(@"\bgraphic\s+(novel|adaptation|edition)s?\b", Options),
            new Regex(@"\bmanga\b", Options),
            new Regex(@"\bclassics\s+illustrated\b", Options),
            new Regex(@"\bcomic(\s+book)?\s+(adaptation|version|edition)\b", Options),
        };

        private static readonly Regex Brackets = new Regex(@"\([^)]*\)|\[[^\]]*\]|\{[^}]*\}", Options);

        // Trailing subtitles that do not make a different book: "1984: A Novel".
        private static readonly Regex NoiseSubtitle = new Regex(
            @"\s*[:\-–]\s*(a\s+)?(novel|novella|story|classic|unabridged|abridged|annotated|illustrated|(\w+\s+)?(anniversary\s+)?edition|(\w+\s+)?classics?)\s*$",
            Options);

        private static readonly Regex NoiseWords = new Regex(
            @"\b(unabridged|abridged|annotated|illustrated|deluxe|anniversary|edition|reprint|centennial)\b",
            Options);

        // Everything from the first bracket, colon or spaced dash on.
        private static readonly Regex LeadTitle = new Regex(@"(\s*[(\[:]|\s+[\-\u2013]\s+).*$", Options);

        private static readonly Regex LeadingArticle = new Regex(@"^(the|a|an)\s+", Options);
        private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{Nd}]+", Options);

        public static bool IsKept(Book book, bool hasFiles)
        {
            return TalearrTrackedCatalogFilter.IsKeptLocalBook(book, hasFiles);
        }

        // The title-only rules (guide, omnibus, adaptation). Null when none applies.
        public static string ClassifyByTitle(string title, bool isOmnibus)
        {
            var text = title ?? string.Empty;

            if (GuidePatterns.Any(pattern => pattern.IsMatch(text)))
            {
                return TalearrAuthorPageGroup.Guide;
            }

            if (isOmnibus || OmnibusPatterns.Any(pattern => pattern.IsMatch(text)))
            {
                return TalearrAuthorPageGroup.Omnibus;
            }

            if (AdaptationPatterns.Any(pattern => pattern.IsMatch(text)))
            {
                return TalearrAuthorPageGroup.Adaptation;
            }

            return null;
        }

        // The same-book key: "George Orwell's 1984 (Penguin Modern Classics)" and "1984: A Novel"
        // both become "1984".
        public static string NormalizeTitle(string title, string authorName)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var text = RemoveDiacritics(title).Trim();
            text = StripAuthorPrefix(text, authorName);
            text = Brackets.Replace(text, " ");
            text = NoiseSubtitle.Replace(text, string.Empty);
            text = NoiseWords.Replace(text, " ");
            text = NonWord.Replace(text, " ").Trim().ToLowerInvariant();
            text = LeadingArticle.Replace(text, string.Empty);

            return text.Trim();
        }

        // Preferred languages: the metadata profile's allowed languages, else the languages of
        // the kept rows, else the most common language of the author's rows. Empty disables the
        // language rule.
        public static HashSet<string> ResolvePreferredLanguages(IEnumerable<string> profileLanguages, IEnumerable<TalearrAuthorPageEntry> entries)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var language in profileLanguages ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(language))
                {
                    result.Add(language.Trim());
                }
            }

            if (result.Count > 0)
            {
                return result;
            }

            var list = (entries ?? Enumerable.Empty<TalearrAuthorPageEntry>()).Where(entry => entry != null).ToList();

            foreach (var entry in list.Where(entry => entry.IsKept && !string.IsNullOrWhiteSpace(entry.Language)))
            {
                result.Add(entry.Language.Trim());
            }

            if (result.Count > 0)
            {
                return result;
            }

            var mostCommon = list
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Language))
                .GroupBy(entry => entry.Language.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (mostCommon != null)
            {
                result.Add(mostCommon.Key);
            }

            return result;
        }

        public static Dictionary<int, string> Classify(
            IEnumerable<TalearrAuthorPageEntry> entries,
            string authorName,
            IEnumerable<string> preferredLanguages)
        {
            var list = (entries ?? Enumerable.Empty<TalearrAuthorPageEntry>()).Where(entry => entry != null).ToList();
            var preferred = new HashSet<string>(
                (preferredLanguages ?? Enumerable.Empty<string>()).Where(language => !string.IsNullOrWhiteSpace(language)).Select(language => language.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var groups = new Dictionary<int, string>();
            var candidates = new List<TalearrAuthorPageEntry>();

            foreach (var entry in list)
            {
                if (entry.IsKept)
                {
                    groups[entry.Id] = TalearrAuthorPageGroup.Main;
                    candidates.Add(entry);
                    continue;
                }

                var byTitle = ClassifyByTitle(entry.Title, entry.IsOmnibus);
                if (byTitle != null)
                {
                    groups[entry.Id] = byTitle;
                    continue;
                }

                candidates.Add(entry);
            }

            foreach (var mediaTypeGroup in candidates.GroupBy(entry => entry.MediaType))
            {
                foreach (var sameBook in GroupSameBook(mediaTypeGroup.ToList(), authorName, preferred))
                {
                    ClassifySameBook(sameBook, preferred, groups);
                }
            }

            return groups;
        }

        private static void ClassifySameBook(List<TalearrAuthorPageEntry> sameBook, HashSet<string> preferred, Dictionary<int, string> groups)
        {
            var rest = sameBook.ToList();

            // A graphic novel version of a prose book.
            if (rest.Any(entry => !IsGraphicGenre(entry)))
            {
                foreach (var entry in rest.Where(entry => !entry.IsKept && IsGraphicGenre(entry)).ToList())
                {
                    groups[entry.Id] = TalearrAuthorPageGroup.Adaptation;
                    rest.Remove(entry);
                }
            }

            if (preferred.Count > 0 && rest.Any(entry => IsPreferredOrUnknown(entry, preferred)))
            {
                foreach (var entry in rest.Where(entry => !entry.IsKept && !IsPreferredOrUnknown(entry, preferred)).ToList())
                {
                    groups[entry.Id] = TalearrAuthorPageGroup.OtherLanguage;
                    rest.Remove(entry);
                }
            }

            var representative = rest
                .OrderByDescending(entry => entry.IsKept)
                .ThenByDescending(entry => entry.Votes)
                .ThenBy(entry => entry.ReleaseDate ?? DateTime.MaxValue)
                .ThenBy(entry => entry.Id)
                .FirstOrDefault();

            foreach (var entry in rest)
            {
                groups[entry.Id] = entry.IsKept || ReferenceEquals(entry, representative)
                    ? TalearrAuthorPageGroup.Main
                    : TalearrAuthorPageGroup.Duplicate;
            }
        }

        // Union of rows that share a normalized title or a work id.
        private static List<List<TalearrAuthorPageEntry>> GroupSameBook(List<TalearrAuthorPageEntry> entries, string authorName, HashSet<string> preferred)
        {
            var parent = Enumerable.Range(0, entries.Count).ToArray();

            int Find(int index)
            {
                while (parent[index] != index)
                {
                    parent[index] = parent[parent[index]];
                    index = parent[index];
                }

                return index;
            }

            void Union(int left, int right)
            {
                var a = Find(left);
                var b = Find(right);
                if (a != b)
                {
                    parent[Math.Max(a, b)] = Math.Min(a, b);
                }
            }

            var firstByKey = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < entries.Count; i++)
            {
                var keys = new List<string>();
                var title = NormalizeTitle(entries[i].Title, authorName);
                if (title.Length > 0)
                {
                    keys.Add("t:" + title);
                }

                // A translation often adds its own subtitle ("1984 (Resimli Yeni Ceviri) - Klasik"),
                // so a row in another language also joins on the title before it.
                if (preferred.Count > 0 && !IsPreferredOrUnknown(entries[i], preferred))
                {
                    var lead = NormalizeTitle(LeadTitle.Replace(entries[i].Title ?? string.Empty, string.Empty), authorName);
                    if (lead.Length > 0 && lead != title)
                    {
                        keys.Add("t:" + lead);
                    }
                }

                keys.AddRange((entries[i].WorkIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => "w:" + id.Trim().ToLowerInvariant()));

                foreach (var key in keys)
                {
                    if (firstByKey.TryGetValue(key, out var other))
                    {
                        Union(i, other);
                    }
                    else
                    {
                        firstByKey[key] = i;
                    }
                }
            }

            return Enumerable.Range(0, entries.Count)
                .GroupBy(Find)
                .Select(group => group.Select(index => entries[index]).ToList())
                .ToList();
        }

        private static bool IsGraphicGenre(TalearrAuthorPageEntry entry)
        {
            return entry.Genres?.Any(genre =>
                genre != null &&
                (genre.IndexOf("graphic novel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 genre.IndexOf("comic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 genre.IndexOf("manga", StringComparison.OrdinalIgnoreCase) >= 0)) == true;
        }

        private static bool IsPreferredOrUnknown(TalearrAuthorPageEntry entry, HashSet<string> preferred)
        {
            return string.IsNullOrWhiteSpace(entry.Language) || preferred.Contains(entry.Language.Trim());
        }

        private static string StripAuthorPrefix(string title, string authorName)
        {
            if (string.IsNullOrWhiteSpace(authorName))
            {
                return title;
            }

            var name = Regex.Escape(RemoveDiacritics(authorName).Trim());
            var stripped = Regex.Replace(title, @"^" + name + @"('s|’s)?\s*[:\-–,]?\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return string.IsNullOrWhiteSpace(stripped) ? title : stripped;
        }

        private static string RemoveDiacritics(string text)
        {
            var normalized = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
