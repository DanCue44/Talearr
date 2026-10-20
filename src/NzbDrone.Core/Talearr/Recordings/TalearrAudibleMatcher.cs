using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Recordings
{
    public sealed class AudibleMatch
    {
        public AudibleProduct Product { get; set; }
        public double Confidence { get; set; }
        public string Method { get; set; }
    }

    // Talearr: decides whether an Audible product is the recording a Chaptarr edition describes.
    // Pure functions, so the rules are easy to test and to tune.
    public static class TalearrAudibleMatcher
    {
        public const double SearchConfidenceThreshold = 0.75;
        public const double RuntimeTolerance = 0.05;

        private static readonly Regex Parenthetical = new Regex(@"\s*[\(\[][^\)\]]*[\)\]]\s*", RegexOptions.Compiled);
        private static readonly Regex NonAlnum = new Regex(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

        public static List<string> EditionNarrators(Edition edition)
        {
            var names = new List<string>();
            if (edition?.NarratorNames != null)
            {
                names.AddRange(edition.NarratorNames);
            }

            if (names.Count == 0 && !string.IsNullOrWhiteSpace(edition?.Narrator))
            {
                names.AddRange(edition.Narrator.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries));
            }

            return names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // All ASIN-shaped ids an edition carries, AudibleASIN first.
        public static List<string> CandidateAsins(Edition edition)
        {
            var values = new List<string>();
            if (edition == null)
            {
                return values;
            }

            values.Add(edition.AudibleASIN);
            values.Add(edition.Asin);
            values.AddRange(edition.Asins ?? new List<string>());

            return values
                .Select(TalearrRecordingsStore.NormalizeAsin)
                .Where(TalearrAudibleClient.LooksLikeAsin)

                // ISBN-10s are ASIN-shaped but never Audible products.
                .Where(v => !v.All(char.IsDigit) && !(v.Take(9).All(char.IsDigit) && v[9] == 'X'))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Replace("ß", "ss").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return NonAlnum.Replace(builder.ToString().ToLowerInvariant(), string.Empty);
        }

        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var text = Parenthetical.Replace(title, " ");
            var colon = text.IndexOf(':');
            if (colon > 0)
            {
                text = text.Substring(0, colon);
            }

            text = Regex.Replace(text, @"^\s*(the|a|an)\s+", string.Empty, RegexOptions.IgnoreCase);
            return NormalizeName(text);
        }

        // true / false when both sides name narrators, null when either side is unknown.
        public static bool? NarratorsMatch(IEnumerable<string> editionNarrators, IEnumerable<string> productNarrators)
        {
            var left = (editionNarrators ?? Enumerable.Empty<string>()).Select(NormalizeName).Where(n => n.Length > 0).ToHashSet();
            var right = (productNarrators ?? Enumerable.Empty<string>()).Select(NormalizeName).Where(n => n.Length > 0).ToHashSet();

            if (left.Count == 0 || right.Count == 0)
            {
                return null;
            }

            if (left.Overlaps(right))
            {
                return true;
            }

            // "Sergio Zamora" against "Sergio Zamora Sola": one name contained in the other.
            return left.Any(l => right.Any(r => (l.Length >= 6 && r.Contains(l)) || (r.Length >= 6 && l.Contains(r))));
        }

        public static bool? RuntimeMatches(int? editionSeconds, int? productMinutes, double tolerance = RuntimeTolerance)
        {
            if (!editionSeconds.HasValue || editionSeconds.Value <= 0 || !productMinutes.HasValue || productMinutes.Value <= 0)
            {
                return null;
            }

            var editionMinutes = editionSeconds.Value / 60.0;
            return Math.Abs(editionMinutes - productMinutes.Value) <= Math.Max(editionMinutes, productMinutes.Value) * tolerance;
        }

        public static bool? LanguageMatches(string editionLanguage, string productLanguage)
        {
            var edition = ToEnglishName(editionLanguage);
            var product = ToEnglishName(productLanguage);
            if (edition == null || product == null)
            {
                return null;
            }

            return string.Equals(edition, product, StringComparison.OrdinalIgnoreCase);
        }

        private static string ToEnglishName(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var value = language.Trim();
            if (value.Equals("und", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (value.Length == 2 || value.Length == 3 || value.Contains('-'))
            {
                var iso = IsoLanguages.Find(value.ToLowerInvariant() == "ger" ? "deu" : value);
                if (iso != null)
                {
                    return iso.EnglishName;
                }
            }

            return IsoLanguages.FindByName(value)?.EnglishName ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
        }

        // A product found through the edition's own ASINs. Accepted unless it contradicts the
        // edition (other narrator, other language, very different runtime).
        public static AudibleMatch MatchByAsin(Edition edition, IEnumerable<AudibleProduct> products)
        {
            var narrators = EditionNarrators(edition);
            var storedAsin = TalearrRecordingsStore.NormalizeAsin(edition?.AudibleASIN);
            var order = CandidateAsins(edition);

            AudibleMatch best = null;
            foreach (var product in (products ?? Enumerable.Empty<AudibleProduct>()).Where(p => p != null && p.Found))
            {
                if (NarratorsMatch(narrators, product.Narrators) == false ||
                    LanguageMatches(edition?.Language, product.Language) == false ||
                    RuntimeMatches(edition?.DurationSeconds, product.RuntimeMinutes, 0.15) == false)
                {
                    continue;
                }

                var isStored = string.Equals(product.Asin, storedAsin, StringComparison.Ordinal);
                var confidence = isStored ? 1.0 : 0.9;
                if (NarratorsMatch(narrators, product.Narrators) == null)
                {
                    confidence -= 0.1;
                }

                var candidate = new AudibleMatch
                {
                    Product = product,
                    Confidence = confidence,
                    Method = isStored ? EditionAudibleLink.MethodStoredAsin : EditionAudibleLink.MethodAsinsList
                };

                if (best == null || candidate.Confidence > best.Confidence ||
                    (candidate.Confidence.Equals(best.Confidence) && order.IndexOf(product.Asin) < order.IndexOf(best.Product.Asin)))
                {
                    best = candidate;
                }
            }

            return best;
        }

        // Search fallback: title must match, narrators must match when the edition names any,
        // runtime within 5% when both are known. Release year and publisher break ties. Returns
        // null below the threshold or when the top two disagree on abridged status.
        public static AudibleMatch MatchBySearch(Edition edition, string bookTitle, IEnumerable<AudibleProduct> products)
        {
            var narrators = EditionNarrators(edition);
            var wantedTitles = new[] { NormalizeTitle(bookTitle), NormalizeTitle(edition?.Title) }
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();

            if (wantedTitles.Count == 0)
            {
                return null;
            }

            var scored = new List<AudibleMatch>();
            foreach (var product in (products ?? Enumerable.Empty<AudibleProduct>()).Where(p => p != null && p.Found))
            {
                var productTitle = NormalizeTitle(product.Title);
                if (!wantedTitles.Any(t => t == productTitle))
                {
                    continue;
                }

                if (LanguageMatches(edition?.Language, product.Language) == false)
                {
                    continue;
                }

                var narratorMatch = NarratorsMatch(narrators, product.Narrators);
                var runtimeMatch = RuntimeMatches(edition?.DurationSeconds, product.RuntimeMinutes);
                if (narratorMatch == false || runtimeMatch == false)
                {
                    continue;
                }

                var score = 0.45;
                score += narratorMatch == true ? 0.35 : 0;
                score += runtimeMatch == true ? 0.15 : 0;

                if (!string.IsNullOrWhiteSpace(edition?.Publisher) && !string.IsNullOrWhiteSpace(product.Publisher) &&
                    NormalizeName(product.Publisher).StartsWith(NormalizeName(edition.Publisher).Substring(0, Math.Min(6, NormalizeName(edition.Publisher).Length)), StringComparison.Ordinal))
                {
                    score += 0.05;
                }

                var editionYear = edition?.ReleaseDate?.Year;
                if (editionYear.HasValue && DateTime.TryParse(product.ReleaseDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var released))
                {
                    var gap = Math.Abs(released.Year - editionYear.Value);
                    score += gap <= 1 ? 0.05 : gap > 5 ? -0.1 : 0;
                }

                scored.Add(new AudibleMatch { Product = product, Confidence = Math.Min(1.0, score), Method = EditionAudibleLink.MethodSearch });
            }

            var ranked = scored.OrderByDescending(m => m.Confidence).ThenBy(m => m.Product.Asin, StringComparer.Ordinal).ToList();
            var top = ranked.FirstOrDefault();
            if (top == null || top.Confidence < SearchConfidenceThreshold)
            {
                return null;
            }

            var runnerUp = ranked.Skip(1).FirstOrDefault();
            if (runnerUp != null && runnerUp.Confidence >= top.Confidence - 0.01 && runnerUp.Product.Abridged != top.Product.Abridged)
            {
                return null;
            }

            return top;
        }
    }
}
