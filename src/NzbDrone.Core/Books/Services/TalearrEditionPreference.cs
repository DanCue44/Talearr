using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books.Services
{
    /// <summary>
    /// TaleArr: language-aware edition ranking.
    ///
    /// Upstream picks the most rated native-format edition, so a popular translation (German
    /// "Die Akte" with 861 votes) beats the English original ("The Pelican Brief" audiobook with
    /// 377 votes) whenever the metadata profile allows every language. TaleArr ranks editions by
    /// language first and only then by popularity:
    ///
    ///   0  preferred language, title matches the work title
    ///   1  preferred language
    ///   2  unknown language ("und", empty), title matches the work title
    ///   3  other language, title matches the work title (an English title mislabelled)
    ///   4  unknown language
    ///   5  other language
    ///
    /// The preferred language is the UI language from the existing Config table (UILanguage,
    /// default English). Metadata profile AllowedLanguages still filters the candidate set upstream,
    /// so a profile that only allows German never offers an English edition here.
    /// The work title comes from the book (Title, OriginalTitle) when it is loaded, plus the titles
    /// of candidate editions already labelled with the preferred language. No setting is stored.
    /// </summary>
    public static class TalearrEditionPreference
    {
        public const string DefaultLanguage = "eng";

        // Ranks 0..2 count as "in the preferred language" (2 = unlabelled but titled like the work).
        public const int PreferredRankLimit = 2;

        private static readonly HashSet<string> UnknownLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "und", "mul", "zxx", "mis", "unknown", "null"
        };

        public static string ResolvePreferredLanguage(int? uiLanguageId)
        {
            if (uiLanguageId.HasValue && uiLanguageId.Value > 0)
            {
                try
                {
                    var language = Language.FindById(uiLanguageId.Value);
                    var code = language == null ? null : IsoLanguages.Get(language)?.ThreeLetterCode;
                    var canonical = NormalizeLanguage(code);
                    if (canonical != null)
                    {
                        return canonical;
                    }
                }
                catch (Exception)
                {
                    // Unknown id in the Config table: fall back to English.
                }
            }

            return DefaultLanguage;
        }

        public static string NormalizeLanguage(string language)
        {
            if (language.IsNullOrWhiteSpace() || UnknownLanguageCodes.Contains(language.Trim()))
            {
                return null;
            }

            var canonical = language.CanonicalizeLanguage()?.Trim();
            return canonical.IsNullOrWhiteSpace() || UnknownLanguageCodes.Contains(canonical) ? null : canonical;
        }

        public static HashSet<string> BuildReferenceTitles(IEnumerable<Edition> editions, string preferredLanguage, IEnumerable<string> extraTitles = null)
        {
            var titles = new HashSet<string>(StringComparer.Ordinal);
            var preferred = NormalizeLanguage(preferredLanguage) ?? DefaultLanguage;
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null).ToList();

            void Add(string title)
            {
                var normalized = TalearrTitleNormalizer.NormalizeMain(title);
                if (normalized.Length > 0)
                {
                    titles.Add(normalized);
                }
            }

            foreach (var title in extraTitles ?? Enumerable.Empty<string>())
            {
                Add(title);
            }

            foreach (var edition in list)
            {
                // Only read the parent book when it is already in memory; never trigger a lazy query.
                if (edition.LazyBook?.IsLoaded == true && edition.LazyBook.Value != null)
                {
                    Add(edition.LazyBook.Value.Title);
                    Add(edition.LazyBook.Value.OriginalTitle);
                }
            }

            foreach (var edition in list.Where(e => string.Equals(NormalizeLanguage(e.Language), preferred, StringComparison.OrdinalIgnoreCase)))
            {
                Add(edition.Title);
            }

            return titles;
        }

        public static int GetLanguageRank(Edition edition, string preferredLanguage, IReadOnlySet<string> referenceTitles)
        {
            if (edition == null)
            {
                return int.MaxValue;
            }

            var preferred = NormalizeLanguage(preferredLanguage) ?? DefaultLanguage;
            var language = NormalizeLanguage(edition.Language);
            var titleMatches = TitleMatchesReference(edition, referenceTitles);

            if (language != null && string.Equals(language, preferred, StringComparison.OrdinalIgnoreCase))
            {
                return titleMatches ? 0 : 1;
            }

            if (language == null)
            {
                return titleMatches ? 2 : 4;
            }

            return titleMatches ? 3 : 5;
        }

        public static bool IsPreferredLanguage(Edition edition, string preferredLanguage)
        {
            var preferred = NormalizeLanguage(preferredLanguage) ?? DefaultLanguage;
            return string.Equals(NormalizeLanguage(edition?.Language), preferred, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsKnownForeignLanguage(Edition edition, string preferredLanguage)
        {
            var language = NormalizeLanguage(edition?.Language);
            var preferred = NormalizeLanguage(preferredLanguage) ?? DefaultLanguage;
            return language != null && !string.Equals(language, preferred, StringComparison.OrdinalIgnoreCase);
        }

        public static bool TitleMatchesReference(Edition edition, IReadOnlySet<string> referenceTitles)
        {
            if (edition == null || referenceTitles == null || referenceTitles.Count == 0)
            {
                return false;
            }

            var main = TalearrTitleNormalizer.NormalizeMain(edition.Title);
            return main.Length > 0 && referenceTitles.Contains(main);
        }
    }
}
