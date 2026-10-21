using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// Talearr: shared title comparison used for edition selection and catalog dedupe.
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
            "a novel", "novel", "a memoir", "illustrated", "annotated",

            // Talearr: labels Hardcover's audiobook listings carry ("Bound (Unabridged)",
            // "The Housemaid (Unabridged Audiobook)", "Skyshade (Audible Australia)").
            "unabridged audiobook", "abridged audiobook", "audio book", "mp3 book", "mp3",
            "audible australia", "audible uk", "audible studios", "audible original",
            "original recording", "unabridged edition", "abridged edition",
            "penguin classics", "penguin modern classics", "vintage classics", "oxford world s classics"
        };

        // Talearr: "(Dueling Crowns Edition)", "(Kickstarter edition)", "(05:29 hh:mm)".
        private static readonly Regex EditionLabelRegex = new Regex(@"^(?:[\p{L}\p{N}' ]+ )?(?:edition|editie|edicion|edizione|ausgabe)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DurationLabelRegex = new Regex(@"^\d{1,3}\s*:\s*\d{2}(?:\s*hh\s*:?\s*mm)?$|^\d+\s*(?:hr|hrs|hours?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[][] TrailingNoisePhrases =
        {
            new[] { "a", "novel" },
            new[] { "a", "memoir" },
            new[] { "a", "thriller" },
            new[] { "a", "novella" },
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

        /// <summary>
        /// Directional form of <see cref="IsSameWorkTitle"/>: <paramref name="candidate"/> is the work
        /// title, or the work title without its subtitle ("Steve Jobs" for "Steve Jobs: The Exclusive
        /// Biography"). A candidate that adds its own subtitle ("Animal Farm: Adapted for Children"
        /// for "Animal Farm") is a different product and does not match.
        /// </summary>
        public static bool IsSameOrShortenedTitle(string workTitle, string candidate)
        {
            var workFull = NormalizeFull(workTitle);
            var candidateFull = NormalizeFull(candidate);
            if (workFull.Length == 0 || candidateFull.Length == 0)
            {
                return false;
            }

            if (string.Equals(workFull, candidateFull, StringComparison.Ordinal))
            {
                return true;
            }

            if (HasSubtitle(StripFormatBrackets(candidate)))
            {
                return false;
            }

            var workMain = Join(CleanTokens(GetMainTitle(StripFormatBrackets(workTitle))));
            return workMain.Length > 0 && string.Equals(workMain, candidateFull, StringComparison.Ordinal);
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
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            if (DurationLabelRegex.IsMatch(label.Trim()))
            {
                return true;
            }

            var normalized = Join(ReleaseTitleMatchScorer.Tokenize(label.Replace('_', ' ')));
            return normalized.Length > 0 && (FormatQualifierLabels.Contains(normalized) || EditionLabelRegex.IsMatch(normalized));
        }

        /// <summary>
        /// Talearr: the title to show for a book, without format-only brackets
        /// ("Nineteen Eighty-Four (MP3 Book)" becomes "Nineteen Eighty-Four"). Falls back to the
        /// original when nothing would be left.
        /// </summary>
        public static string CleanDisplayTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var cleaned = Regex.Replace(StripFormatBrackets(title), @"\s{2,}", " ").Trim();
            return cleaned.Length == 0 ? title.Trim() : cleaned;
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
