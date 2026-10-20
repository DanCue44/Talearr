using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace NzbDrone.Core.Talearr.MamFiller
{
    // Talearr: the household's taste for the freeleech seeding filler. Built once per run from the
    // MAM indexer settings and the authors in the library.
    public class TalearrMamFillerTasteProfile
    {
        public List<string> PreferredCategories { get; set; } = new List<string>();
        public List<string> ExcludedCategories { get; set; } = new List<string>();

        // Author key (TalearrMamFillerTaste.AuthorKey) to the name shown in the match reason.
        public Dictionary<string, string> ListedAuthors { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public HashSet<string> LibraryAuthors { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public bool OnlyPreferred { get; set; }

        public bool HasPreferences => PreferredCategories.Count > 0 || ListedAuthors.Count > 0 || LibraryAuthors.Count > 0;
    }

    // Talearr: pure taste rules (category and author matching, MAM category ids). Category names
    // are matched by whole words, case-insensitively, so "True Crime" matches both
    // "Audiobooks - True Crime" and "Ebooks - True Crime" whatever MAM's ids are.
    public static class TalearrMamFillerTaste
    {
        public const string DefaultPreferredCategories = "True Crime";
        public const string DefaultExcludedCategories = "Religion, Spirituality, Christian, Pol/Soc/Relig";

        // MAM's site category ids (the same list Jackett and Prowlarr ship), used only to narrow
        // the extra preferred search. Ids seen in this run's results are added to these, and the
        // final decision is always made on the category name.
        public static readonly IReadOnlyDictionary<int, string> KnownCategories = new Dictionary<int, string>
        {
            [39] = "Audiobooks - Action/Adventure", [49] = "Audiobooks - Art", [50] = "Audiobooks - Biographical",
            [83] = "Audiobooks - Business", [51] = "Audiobooks - Computer/Internet", [97] = "Audiobooks - Crafts",
            [40] = "Audiobooks - Crime/Thriller", [41] = "Audiobooks - Fantasy", [106] = "Audiobooks - Food",
            [42] = "Audiobooks - General Fiction", [52] = "Audiobooks - General Non-Fic", [98] = "Audiobooks - Historical Fiction",
            [54] = "Audiobooks - History", [55] = "Audiobooks - Home/Garden", [43] = "Audiobooks - Horror",
            [99] = "Audiobooks - Humor", [84] = "Audiobooks - Instructional", [44] = "Audiobooks - Juvenile",
            [56] = "Audiobooks - Language", [45] = "Audiobooks - Literary Classics", [57] = "Audiobooks - Math/Science/Tech",
            [85] = "Audiobooks - Medical", [87] = "Audiobooks - Mystery", [119] = "Audiobooks - Nature",
            [88] = "Audiobooks - Philosophy", [58] = "Audiobooks - Pol/Soc/Relig", [59] = "Audiobooks - Recreation",
            [46] = "Audiobooks - Romance", [47] = "Audiobooks - Science Fiction", [53] = "Audiobooks - Self-Help",
            [89] = "Audiobooks - Travel/Adventure", [100] = "Audiobooks - True Crime", [108] = "Audiobooks - Urban Fantasy",
            [48] = "Audiobooks - Western", [111] = "Audiobooks - Young Adult",
            [60] = "Ebooks - Action/Adventure", [71] = "Ebooks - Art", [72] = "Ebooks - Biographical",
            [90] = "Ebooks - Business", [61] = "Ebooks - Comics/Graphic novels", [73] = "Ebooks - Computer/Internet",
            [101] = "Ebooks - Crafts", [62] = "Ebooks - Crime/Thriller", [63] = "Ebooks - Fantasy",
            [107] = "Ebooks - Food", [64] = "Ebooks - General Fiction", [74] = "Ebooks - General Non-Fiction",
            [102] = "Ebooks - Historical Fiction", [76] = "Ebooks - History", [77] = "Ebooks - Home/Garden",
            [65] = "Ebooks - Horror", [103] = "Ebooks - Humor", [115] = "Ebooks - Illusion/Magic",
            [91] = "Ebooks - Instructional", [66] = "Ebooks - Juvenile", [78] = "Ebooks - Language",
            [67] = "Ebooks - Literary Classics", [79] = "Ebooks - Magazines/Newspapers", [80] = "Ebooks - Math/Science/Tech",
            [92] = "Ebooks - Medical", [118] = "Ebooks - Mixed Collections", [94] = "Ebooks - Mystery",
            [120] = "Ebooks - Nature", [95] = "Ebooks - Philosophy", [81] = "Ebooks - Pol/Soc/Relig",
            [82] = "Ebooks - Recreation", [68] = "Ebooks - Romance", [69] = "Ebooks - Science Fiction",
            [75] = "Ebooks - Self-Help", [96] = "Ebooks - Travel/Adventure", [104] = "Ebooks - True Crime",
            [109] = "Ebooks - Urban Fantasy", [70] = "Ebooks - Western", [112] = "Ebooks - Young Adult"
        };

        public static TalearrMamFillerTasteProfile Build(MyAnonaMouseSettings settings, IEnumerable<string> libraryAuthors)
        {
            var profile = new TalearrMamFillerTasteProfile
            {
                PreferredCategories = ParseList(settings.FillerPreferredCategories),
                ExcludedCategories = ParseList(settings.FillerExcludedCategories),
                OnlyPreferred = settings.FillerOnlyPreferred
            };

            foreach (var name in ParseList(settings.FillerPreferredAuthors))
            {
                var key = AuthorKey(name);
                if (key.Length > 0 && !profile.ListedAuthors.ContainsKey(key))
                {
                    profile.ListedAuthors[key] = name;
                }
            }

            if (settings.FillerPreferLibraryAuthors && libraryAuthors != null)
            {
                foreach (var name in libraryAuthors)
                {
                    var key = AuthorKey(name);
                    if (key.Length > 0)
                    {
                        profile.LibraryAuthors.Add(key);
                    }
                }
            }

            return profile;
        }

        // Comma, semicolon or newline separated; blanks and repeats dropped.
        public static List<string> ParseList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            return text.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => NormalizeWords(t).Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Lower case, accents removed, anything but letters and digits turned into single spaces.
        public static string NormalizeWords(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var space = false;
            foreach (var c in value.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    if (space && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                    space = false;
                }
                else
                {
                    space = true;
                }
            }

            return builder.ToString();
        }

        // "J.K. Rowling", "J K Rowling", "JK Rowling" and "Rowling, J.K." are one author.
        public static string AuthorKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var text = name.Trim();
            var comma = text.IndexOf(',');
            if (comma > 0 && comma == text.LastIndexOf(','))
            {
                var last = text.Substring(0, comma);
                var first = text.Substring(comma + 1);
                if (NormalizeWords(first).Length > 0 && !IsSuffix(first))
                {
                    text = first + " " + last;
                }
            }

            return NormalizeWords(text).Replace(" ", string.Empty);
        }

        // Whole-word match: "True Crime" matches "Audiobooks - True Crime" but "Art" does not
        // match "Martial Arts".
        public static bool CategoryMatches(string categoryName, string entry)
        {
            var name = NormalizeWords(categoryName);
            var wanted = NormalizeWords(entry);
            if (name.Length == 0 || wanted.Length == 0)
            {
                return false;
            }

            return (" " + name + " ").Contains(" " + wanted + " ", StringComparison.Ordinal);
        }

        public static string MatchingCategory(string categoryName, IEnumerable<string> entries)
        {
            return entries?.FirstOrDefault(e => CategoryMatches(categoryName, e));
        }

        // The category name to match on: MAM's catname, else the known name for the id.
        public static string CategoryNameOf(TalearrMamFillerCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate?.CategoryName))
            {
                return candidate.CategoryName;
            }

            return candidate?.CategoryId != null && KnownCategories.TryGetValue(candidate.CategoryId.Value, out var known) ? known : null;
        }

        // Null when the candidate may be grabbed, otherwise why not.
        public static string Reject(TalearrMamFillerCandidate candidate, TalearrMamFillerTasteProfile profile)
        {
            if (profile == null)
            {
                return null;
            }

            if (MatchingCategory(CategoryNameOf(candidate), profile.ExcludedCategories) != null)
            {
                return "excluded category";
            }

            if (profile.OnlyPreferred && PreferredReason(candidate, profile) == null)
            {
                return "not preferred";
            }

            return null;
        }

        // Why the candidate is a preferred pick (shown on Activity > Seeding), or null.
        public static string PreferredReason(TalearrMamFillerCandidate candidate, TalearrMamFillerTasteProfile profile)
        {
            if (candidate == null || profile == null)
            {
                return null;
            }

            var categoryName = CategoryNameOf(candidate);
            var category = MatchingCategory(categoryName, profile.PreferredCategories);
            if (category != null)
            {
                return $"Category: {categoryName}";
            }

            foreach (var author in candidate.Authors ?? new List<string>())
            {
                var key = AuthorKey(author);
                if (key.Length == 0)
                {
                    continue;
                }

                if (profile.LibraryAuthors.Contains(key))
                {
                    return $"Author in your library: {author}";
                }

                if (profile.ListedAuthors.ContainsKey(key))
                {
                    return $"Preferred author: {author}";
                }
            }

            return null;
        }

        // MAM category ids for the preferred categories: the known list plus ids seen in this
        // run's results (category id to name). Empty when nothing is preferred.
        public static List<int> PreferredCategoryIds(IEnumerable<string> preferred, IReadOnlyDictionary<int, string> seen = null)
        {
            var entries = (preferred ?? Enumerable.Empty<string>()).ToList();
            if (entries.Count == 0)
            {
                return new List<int>();
            }

            var names = new Dictionary<int, string>(KnownCategories.ToDictionary(k => k.Key, k => k.Value));
            foreach (var pair in seen ?? new Dictionary<int, string>())
            {
                if (pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    names[pair.Key] = pair.Value;
                }
            }

            return names.Where(n => MatchingCategory(n.Value, entries) != null)
                .Select(n => n.Key)
                .OrderBy(id => id)
                .ToList();
        }

        private static bool IsSuffix(string text)
        {
            var simple = NormalizeWords(text);
            return simple is "jr" or "sr" or "ii" or "iii" or "iv" or "phd" or "md";
        }
    }
}
