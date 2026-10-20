using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.ContentFilter
{
    // Talearr: genre and content type rules a metadata profile adds on top of Chaptarr's own
    // (minimum pages, omnibus, ignored terms, languages). Stored per profile as a talearr.* Config
    // row, so the MetadataProfiles table is untouched.
    public class TalearrContentFilter
    {
        // When not empty, a book must carry at least one of these genres. Books the metadata
        // has no genres for are kept, since about half the catalog has none.
        public List<string> AllowedGenres { get; set; } = new List<string>();

        // A book carrying any of these genres is skipped.
        public List<string> BlockedGenres { get; set; } = new List<string>();

        public bool SkipShortStories { get; set; }
        public bool SkipNovellas { get; set; }
        public bool SkipAnthologies { get; set; }
        public bool SkipGraphicNovels { get; set; }

        [JsonIgnore]
        public bool IsActive =>
            Clean(AllowedGenres).Any() || Clean(BlockedGenres).Any() ||
            SkipShortStories || SkipNovellas || SkipAnthologies || SkipGraphicNovels;

        public TalearrContentFilter Normalized()
        {
            return new TalearrContentFilter
            {
                AllowedGenres = Clean(AllowedGenres),
                BlockedGenres = Clean(BlockedGenres),
                SkipShortStories = SkipShortStories,
                SkipNovellas = SkipNovellas,
                SkipAnthologies = SkipAnthologies,
                SkipGraphicNovels = SkipGraphicNovels
            };
        }

        public bool SameRulesAs(TalearrContentFilter other)
        {
            var a = Normalized();
            var b = (other ?? new TalearrContentFilter()).Normalized();

            return a.SkipShortStories == b.SkipShortStories &&
                   a.SkipNovellas == b.SkipNovellas &&
                   a.SkipAnthologies == b.SkipAnthologies &&
                   a.SkipGraphicNovels == b.SkipGraphicNovels &&
                   a.AllowedGenres.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.AllowedGenres) &&
                   a.BlockedGenres.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.BlockedGenres);
        }

        private static List<string> Clean(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static class TalearrContentFilterRules
    {
        // Genre names as the metadata server returns them (Goodreads and Hardcover style shelves),
        // checked against a live library: "Short stories", "Novella", "Anthologies", "Literary
        // Collections", "Comics", "Graphic novels", "Comics & Graphic Novels".
        public static readonly string[] ShortStoryGenres = { "short stories", "short story", "short story collection" };
        public static readonly string[] NovellaGenres = { "novella", "novellas", "novelette" };
        public static readonly string[] AnthologyGenres = { "anthology", "anthologies", "literary collections", "collections and anthologies" };
        public static readonly string[] GraphicNovelGenres = { "graphic novel", "graphic novels", "comics", "comic book", "comic books", "manga", "manhwa", "bande dessinee" };

        // Null when the book passes, otherwise why it was skipped.
        public static string Rejection(Book book, TalearrContentFilter filter)
        {
            if (book == null || filter == null || !filter.IsActive)
            {
                return null;
            }

            var genres = (book.Genres ?? new List<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(Normalize)
                .ToList();

            var rules = filter.Normalized();

            var blocked = rules.BlockedGenres.FirstOrDefault(term => AnyGenreMatches(genres, term));
            if (blocked != null)
            {
                return $"genre '{blocked}' is blocked";
            }

            if (rules.AllowedGenres.Any() && genres.Any() && !rules.AllowedGenres.Any(term => AnyGenreMatches(genres, term)))
            {
                return "no allowed genre";
            }

            if (rules.SkipShortStories && ShortStoryGenres.Any(term => AnyGenreMatches(genres, term)))
            {
                return "short stories";
            }

            if (rules.SkipNovellas && NovellaGenres.Any(term => AnyGenreMatches(genres, term)))
            {
                return "novella";
            }

            if (rules.SkipAnthologies &&
                (AnthologyGenres.Any(term => AnyGenreMatches(genres, term)) || Contains(Normalize(book.Title), "anthology")))
            {
                return "anthology";
            }

            if (rules.SkipGraphicNovels && GraphicNovelGenres.Any(term => AnyGenreMatches(genres, term)))
            {
                return "graphic novel or comic";
            }

            return null;
        }

        // Whole word match after folding case, accents and punctuation: "comics" matches
        // "Comics & Graphic Novels" but "comic" does not match "Comics".
        public static bool GenreMatches(string genre, string term)
        {
            return Contains(Normalize(genre), Normalize(term));
        }

        private static bool AnyGenreMatches(List<string> normalizedGenres, string term)
        {
            var normalizedTerm = Normalize(term);
            return normalizedGenres.Any(g => Contains(g, normalizedTerm));
        }

        private static bool Contains(string normalizedValue, string normalizedTerm)
        {
            if (normalizedTerm.Length == 0 || normalizedValue.Length == 0)
            {
                return false;
            }

            return $" {normalizedValue} ".Contains($" {normalizedTerm} ", StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var decomposed = value.Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var lastWasSpace = true;

            foreach (var c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            return builder.ToString().Trim();
        }
    }
}
