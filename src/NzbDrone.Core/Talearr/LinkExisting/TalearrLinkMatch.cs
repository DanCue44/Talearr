using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Talearr.Audiobookshelf;

namespace NzbDrone.Core.Talearr.LinkExisting
{
    public sealed class TalearrLinkMatchResult
    {
        public double Score { get; set; }
        public bool Strong { get; set; }
        public string MatchedBy { get; set; }
    }

    // Talearr: how well an existing audiobook (an Audiobookshelf item or a folder name) fits the
    // book being added. Strong (preselected) when the ASIN is the same, or when the author fits and
    // the whole title does: the same words, or one title is the other plus a series tail
    // ("The Constable: The Complete Series" for "The Constable: The Complete Series: The
    // Constable, Books 1-2"). Weaker title overlaps are offered but not preselected.
    public static class TalearrLinkMatch
    {
        public const double CandidateJaccard = 0.4;
        public const double StrongJaccard = 0.75;

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "the", "a", "an", "of", "and", "in", "to", "for", "on", "at", "by", "with",
            "book", "books", "unabridged", "audiobook", "edition", "novel"
        };

        public static TalearrLinkMatchResult Score(TalearrLinkCandidateRequest request, string title, string subtitle, string authors, string asin, string isbn)
        {
            if (request == null || request.Title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var authorFits = AuthorFits(request.AuthorName, authors);

            if (asin.IsNotNullOrWhiteSpace() &&
                (request.Asins ?? new List<string>()).Any(a => string.Equals(a?.Trim(), asin.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return new TalearrLinkMatchResult { Score = 1.0, Strong = true, MatchedBy = "asin" };
            }

            var isbnMatch = isbn.IsNotNullOrWhiteSpace() &&
                            (request.Isbns ?? new List<string>()).Any(i => DigitsOnly(i).Length >= 10 && DigitsOnly(i) == DigitsOnly(isbn));

            if (!authorFits && !isbnMatch)
            {
                return null;
            }

            var titles = new List<string> { title };
            if (subtitle.IsNotNullOrWhiteSpace() && (title ?? string.Empty).IndexOf(subtitle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                titles.Add($"{title}: {subtitle}");
            }

            var best = titles
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Select(t => TitleFit(request.Title, t))
                .OrderByDescending(f => f.Strong)
                .ThenByDescending(f => f.Jaccard)
                .FirstOrDefault();

            if (isbnMatch)
            {
                return new TalearrLinkMatchResult { Score = 0.95, Strong = authorFits, MatchedBy = "isbn" };
            }

            if (best == null || (!best.Strong && best.Jaccard < CandidateJaccard))
            {
                return null;
            }

            return new TalearrLinkMatchResult
            {
                Score = best.Strong ? Math.Max(best.Jaccard, 0.9) : best.Jaccard,
                Strong = best.Strong,
                MatchedBy = "title"
            };
        }

        public static bool AuthorFits(string wanted, string authors)
        {
            var wantedKey = TalearrAbsMatcher.NormalizeName(wanted);
            if (wantedKey.IsNullOrWhiteSpace())
            {
                return false;
            }

            var wantedTokens = Tokens(wanted, keepStopWords: true);

            foreach (var author in TalearrAbsMatcher.SplitAuthors(authors))
            {
                if (TalearrAbsMatcher.NormalizeName(author) == wantedKey)
                {
                    return true;
                }

                // The same words in another order ("Sanderson Brandon").
                var tokens = Tokens(author, keepStopWords: true);
                if (tokens.Count > 0 && tokens.SetEquals(wantedTokens))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class Fit
        {
            public double Jaccard { get; set; }
            public bool Strong { get; set; }
        }

        private static Fit TitleFit(string wanted, string candidate)
        {
            var wantedTokens = Tokens(wanted, keepStopWords: false);
            var candidateTokens = Tokens(candidate, keepStopWords: false);

            if (wantedTokens.Count == 0 || candidateTokens.Count == 0)
            {
                return new Fit();
            }

            var shared = wantedTokens.Intersect(candidateTokens).Count();
            var jaccard = (double)shared / wantedTokens.Union(candidateTokens).Count();

            var wantedKey = TalearrAbsMatcher.NormalizeName(wanted);
            var candidateKey = TalearrAbsMatcher.NormalizeName(candidate);
            var shorter = wantedKey.Length <= candidateKey.Length ? wantedKey : candidateKey;
            var longer = ReferenceEquals(shorter, wantedKey) ? candidateKey : wantedKey;
            var shorterTokens = ReferenceEquals(shorter, wantedKey) ? wantedTokens : candidateTokens;

            // The same title, or one title plus a series or subtitle tail. A one-word start is too
            // weak to preselect ("The Constable" for the two-book set).
            var sameTitle = wantedKey == candidateKey;
            var longerTokens = ReferenceEquals(shorter, wantedKey) ? candidateTokens : wantedTokens;
            var prefixed = shorter.Length > 0 &&
                           longer.StartsWith(shorter, StringComparison.Ordinal) &&
                           shorterTokens.Count >= 2 &&
                           shorterTokens.IsSubsetOf(longerTokens);

            return new Fit
            {
                Jaccard = jaccard,
                Strong = sameTitle || prefixed || jaccard >= StrongJaccard
            };
        }

        private static HashSet<string> Tokens(string text, bool keepStopWords)
        {
            if (text.IsNullOrWhiteSpace())
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }

            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\'' || c == '’')
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return builder.ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => keepStopWords || !StopWords.Contains(t))
                .ToHashSet(StringComparer.Ordinal);
        }

        private static string DigitsOnly(string value)
        {
            return new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        }
    }
}
