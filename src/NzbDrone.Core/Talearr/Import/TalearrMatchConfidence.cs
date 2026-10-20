using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Import
{
    public class TalearrMatchConfidenceResult
    {
        public string Level { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
    }

    // Talearr: a visible confidence for each manual import match (Chaptarr #105). The matcher
    // only says which book won, so a wrong match looked the same as a right one. This reads the
    // match's own evidence (provider ids, supporting and conflicting signals) and adds one cheap
    // independent check: does the matched title show up in the file's tags, file name or folder?
    // Display only; it never changes the match or blocks an import.
    public static class TalearrMatchConfidence
    {
        public const string High = "high";
        public const string Medium = "medium";
        public const string Low = "low";

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "or", "in", "on", "to", "for", "with", "at", "by", "from",
            "book", "novel", "edition", "unabridged", "abridged", "audiobook", "ebook", "vol", "volume", "part"
        };

        private static readonly HashSet<string> WeakRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "escape_hatch", "pinned_first_crack", "undistinguished_audiobook_edition"
        };

        public static TalearrMatchConfidenceResult Evaluate(string bookTitle, MatchProvenance provenance, string filePath, IDictionary<string, List<string>> tags)
        {
            if (string.IsNullOrWhiteSpace(bookTitle))
            {
                return null;
            }

            var result = new TalearrMatchConfidenceResult();
            var supporting = provenance?.SupportingSignals ?? new List<MatchSignal>();
            var conflicting = provenance?.ConflictingSignals ?? new List<MatchSignal>();
            var matchedVia = provenance?.MatchedVia ?? string.Empty;

            if (IsUserSelection(provenance))
            {
                result.Level = High;
                result.Notes.Add("Chosen by you");
                return result;
            }

            var byIdentifier = matchedVia.Equals("provider_identifier", StringComparison.OrdinalIgnoreCase) ||
                               supporting.Any(signal => Is(signal, "provider_identifier"));
            var titleInEvidence = TitleAppears(bookTitle, filePath, tags);
            var supportsAuthor = supporting.Any(signal => Is(signal, "author"));
            var supportsTitle = supporting.Any(signal => Is(signal, "title"));

            foreach (var signal in conflicting.Take(3))
            {
                result.Notes.Add(DescribeConflict(signal));
            }

            if (!titleInEvidence)
            {
                result.Notes.Add($"'{bookTitle}' does not appear in the file's tags, file name or folder");
            }

            if (byIdentifier)
            {
                result.Notes.Insert(0, "Matched by an identifier in the file (ISBN or ASIN)");
                result.Level = conflicting.Any() ? Medium : High;
                return result;
            }

            if (WeakRoutes.Contains(matchedVia))
            {
                result.Notes.Add("Fallback match: no distinguishing evidence picked this edition");
            }

            if (conflicting.Any() || !titleInEvidence)
            {
                result.Level = Low;
            }
            else if (supportsAuthor && supportsTitle && !WeakRoutes.Contains(matchedVia))
            {
                result.Level = High;
            }
            else
            {
                result.Level = Medium;
            }

            return result;
        }

        private static bool IsUserSelection(MatchProvenance provenance)
        {
            var source = provenance?.SelectionSource ?? string.Empty;

            return source.Equals("user_local", StringComparison.OrdinalIgnoreCase) ||
                   source.Equals("user_metadata", StringComparison.OrdinalIgnoreCase);
        }

        private static bool Is(MatchSignal signal, string type)
        {
            return signal != null && type.Equals(signal.Type, StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribeConflict(MatchSignal signal)
        {
            var what = (signal?.Type ?? "evidence").Replace('_', ' ');
            if (!string.IsNullOrWhiteSpace(signal?.Observed) && !string.IsNullOrWhiteSpace(signal?.Expected))
            {
                return $"Conflicting {what}: file says '{signal.Observed}', match has '{signal.Expected}'";
            }

            return string.IsNullOrWhiteSpace(signal?.Detail) ? $"Conflicting {what}" : signal.Detail;
        }

        // True when most of the title's significant words appear in the tags, the file name or one
        // of the two parent folders. Titles with no significant words are not judged.
        public static bool TitleAppears(string bookTitle, string filePath, IDictionary<string, List<string>> tags)
        {
            var mainTitle = bookTitle.Split(':')[0];
            var titleWords = Words(mainTitle).Where(word => !StopWords.Contains(word)).Distinct().ToList();
            if (titleWords.Count == 0)
            {
                return true;
            }

            var haystack = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                haystack.Append(' ').Append(Path.GetFileNameWithoutExtension(filePath));
                var folder = Path.GetDirectoryName(filePath);
                for (var i = 0; i < 2 && !string.IsNullOrWhiteSpace(folder); i++)
                {
                    haystack.Append(' ').Append(Path.GetFileName(folder));
                    folder = Path.GetDirectoryName(folder);
                }
            }

            if (tags != null)
            {
                foreach (var key in new[] { "album", "title", "series", "grouping", "subtitle", "dc:title" })
                {
                    foreach (var pair in tags.Where(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && pair.Value != null))
                    {
                        haystack.Append(' ').Append(string.Join(" ", pair.Value));
                    }
                }
            }

            var available = new HashSet<string>(Words(haystack.ToString()));
            var found = titleWords.Count(available.Contains);

            return found * 2 >= titleWords.Count;
        }

        private static IEnumerable<string> Words(string value)
        {
            var current = new StringBuilder();
            foreach (var ch in (value ?? string.Empty).Normalize(NormalizationForm.FormKD))
            {
                if (char.IsLetterOrDigit(ch))
                {
                    current.Append(char.ToLowerInvariant(ch));
                }
                else if (ch == '\'' || ch == '’' || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    // Apostrophes and accents stay inside the word ("titan's" -> "titans").
                }
                else if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }
    }
}
