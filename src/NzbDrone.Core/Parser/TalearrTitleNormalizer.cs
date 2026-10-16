using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// TaleArr: shared title comparison used for edition selection and catalog dedupe.
    /// It tokenizes with <see cref="ReleaseTitleMatchScorer.Tokenize"/> so case, diacritics,
    /// punctuation, "&amp;" and possessives behave exactly like release matching.
    /// </summary>
    public static class TalearrTitleNormalizer
    {
        private static readonly Regex BracketSegmentRegex = new Regex(@"\s*[\(\[\{](?<label>[^\)\]\}]*)[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex SubtitleBoundaryRegex = new Regex(@"\s*(?::|;|\s-\s|\s\u2013\s|\s\u2014\s)\s*", RegexOptions.Compiled);

        private static readonly HashSet<string> LeadingArticles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an"
        };

        // Bracketed labels that describe the format or production of an edition, not the work.
        private static readonly HashSet<string> FormatQualifierLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audiobook", "audible audio", "audio", "audio cd", "audio cassette", "mp3 cd", "cd", "cassette", "playaway",
            "ebook", "e book", "kindle edition", "kindle", "epub", "nook", "nook book",
            "paperback", "hardcover", "hardback", "mass market paperback", "trade paperback", "library binding", "board book",
            "unabridged", "abridged", "retail", "english edition", "english", "large print", "large type",
            "a novel", "novel", "a memoir", "illustrated", "annotated"
        };

        private static readonly string[][] TrailingNoisePhrases =
        {
            new[] { "a", "novel" },
            new[] { "a", "memoir" },
            new[] { "a", "thriller" },
            new[] { "unabridged" },
            new[] { "abridged" }
        };

        /// <summary>
        /// Full title for identity comparison: format-only bracket labels, a leading article and
        /// trailing "a novel" style noise are removed. Other brackets are kept, because
        /// "Animal Farm (Veritas Book 1)" is not the same product as "Animal Farm".
        /// </summary>
        public static string NormalizeFull(string title)
        {
            return Join(CleanTokens(StripFormatBrackets(title)));
        }

        /// <summary>
        /// The main title before any subtitle boundary (": ", " - ", "; ", dashes), with every
        /// bracket removed. Used where only the core title matters (edition language ranking).
        /// </summary>
        public static string NormalizeMain(string title)
        {
            return Join(CleanTokens(GetMainTitle(StripAllBrackets(title))));
        }

        /// <summary>
        /// Same work title: equal full titles, or one side has no subtitle and the main titles agree
        /// ("Steve Jobs" vs "Steve Jobs: The Exclusive Biography"). Two different subtitles never
        /// match ("Animal Farm: Adapted for Children" vs "Animal Farm: A Fairy Story").
        /// </summary>
        public static bool IsSameWorkTitle(string left, string right)
        {
            var leftFull = NormalizeFull(left);
            var rightFull = NormalizeFull(right);
            if (leftFull.Length == 0 || rightFull.Length == 0)
            {
                return false;
            }

            if (string.Equals(leftFull, rightFull, StringComparison.Ordinal))
            {
                return true;
            }

            var leftHasSubtitle = HasSubtitle(StripFormatBrackets(left));
            var rightHasSubtitle = HasSubtitle(StripFormatBrackets(right));
            if (leftHasSubtitle && rightHasSubtitle)
            {
                return false;
            }

            var leftMain = Join(CleanTokens(GetMainTitle(StripFormatBrackets(left))));
            var rightMain = Join(CleanTokens(GetMainTitle(StripFormatBrackets(right))));
            return leftMain.Length > 0 && string.Equals(leftMain, rightMain, StringComparison.Ordinal);
        }

        public static bool HasSubtitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            var match = SubtitleBoundaryRegex.Match(title.Trim());
            return match.Success && match.Index > 0 && match.Index + match.Length < title.Trim().Length;
        }

        public static string GetMainTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var trimmed = title.Trim();
            var match = SubtitleBoundaryRegex.Match(trimmed);
            return match.Success && match.Index > 0 ? trimmed.Substring(0, match.Index).Trim() : trimmed;
        }

        public static string GetSubtitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var trimmed = title.Trim();
            var match = SubtitleBoundaryRegex.Match(trimmed);
            return match.Success && match.Index > 0 ? trimmed.Substring(match.Index + match.Length).Trim() : string.Empty;
        }

        public static string StripAllBrackets(string title)
        {
            return string.IsNullOrWhiteSpace(title) ? string.Empty : BracketSegmentRegex.Replace(title, " ").Trim();
        }

        public static string StripFormatBrackets(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            return BracketSegmentRegex.Replace(title, match => IsFormatQualifier(match.Groups["label"].Value) ? " " : match.Value).Trim();
        }

        public static bool IsFormatQualifier(string label)
        {
            var normalized = Join(ReleaseTitleMatchScorer.Tokenize(label));
            return normalized.Length > 0 && FormatQualifierLabels.Contains(normalized);
        }

        private static List<string> CleanTokens(string title)
        {
            var tokens = ReleaseTitleMatchScorer.Tokenize(title);

            if (tokens.Count > 1 && LeadingArticles.Contains(tokens[0]))
            {
                tokens.RemoveAt(0);
            }

            var changed = true;
            while (changed && tokens.Count > 1)
            {
                changed = false;
                foreach (var phrase in TrailingNoisePhrases)
                {
                    if (tokens.Count > phrase.Length && EndsWith(tokens, phrase))
                    {
                        tokens.RemoveRange(tokens.Count - phrase.Length, phrase.Length);
                        changed = true;
                        break;
                    }
                }
            }

            return tokens;
        }

        private static bool EndsWith(IReadOnlyList<string> tokens, IReadOnlyList<string> phrase)
        {
            var offset = tokens.Count - phrase.Count;
            return !phrase.Where((token, index) => !string.Equals(tokens[offset + index], token, StringComparison.OrdinalIgnoreCase)).Any();
        }

        private static string Join(IEnumerable<string> tokens)
        {
            return string.Join(" ", tokens ?? Enumerable.Empty<string>());
        }
    }
}
