using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Naming;

namespace NzbDrone.Core.Talearr.Metadata
{
    // Talearr: text that goes into the sidecar files Talearr writes next to a book (Audiobookshelf's
    // metadata.json) and into the tags of a merged M4B.
    //
    // Upstream wrote the matched edition's title, which for audiobooks is often a store listing:
    // the 1984 import on 2026-10-16 got "Nineteen Eighty-Four (MP3 Book)" in metadata.json, and the
    // narrator came through as "Samuel  West" (two spaces), so Audiobookshelf showed both. The title
    // is now the same one the folder is named after (TalearrBookTitle), the edition subtitle is kept
    // only when it is a real subtitle, and names have their whitespace collapsed.
    public static class TalearrSidecarText
    {
        // Any run of whitespace, including no-break and other Unicode spaces, plus zero-width space.
        private static readonly Regex WhitespaceRegex = new Regex(@"[\s​﻿]+", RegexOptions.Compiled);

        // Subtitles that only describe the kind of book, not this book: "A Novel", "A Thriller".
        private static readonly Regex GenericSubtitleRegex = new Regex(
            @"^(?:(?:a|an)\s+)?(?:novel|novella|thriller|mystery|memoir|romance|novel\s+in\s+stories|audio\s?book|audio\s+drama)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // The title for metadata.json: the book's own title without format noise, like the folder.
        public static string Title(Book book, Edition edition)
        {
            return Clean(TalearrBookTitle.ForNaming(book, edition));
        }

        // The subtitle for metadata.json, or null. The edition's subtitle is preferred (it belongs to
        // the matched recording), then the book's. A subtitle is dropped when it is only format noise
        // ("Unabridged", "MP3 Book"), only a genre ("A Novel"), or repeats the title.
        public static string Subtitle(Book book, Edition edition, string title)
        {
            foreach (var candidate in new[] { edition?.Subtitle, book?.Subtitle })
            {
                var subtitle = CleanSubtitle(candidate, title);
                if (subtitle != null)
                {
                    return subtitle;
                }
            }

            return null;
        }

        public static string CleanSubtitle(string subtitle, string title)
        {
            var cleaned = Clean(subtitle);
            if (string.IsNullOrEmpty(cleaned))
            {
                return null;
            }

            cleaned = cleaned.Trim('(', ')', '[', ']', ' ', ':', '-', ',');
            if (cleaned.Length == 0 || TalearrBookTitle.IsNoiseGroup(cleaned))
            {
                return null;
            }

            var stripped = Clean(TalearrBookTitle.Strip(cleaned));
            if (string.IsNullOrEmpty(stripped) || TalearrBookTitle.IsNoiseGroup(stripped) || GenericSubtitleRegex.IsMatch(stripped))
            {
                return null;
            }

            var cleanTitle = Clean(title);
            if (!string.IsNullOrEmpty(cleanTitle) &&
                (string.Equals(stripped, cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                 cleanTitle.EndsWith(": " + stripped, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            return stripped;
        }

        // Collapses every run of whitespace to one space and trims. Null stays null.
        public static string Clean(string value)
        {
            if (value == null)
            {
                return null;
            }

            return WhitespaceRegex.Replace(value, " ").Trim();
        }

        // A person's name for metadata ("Samuel  West" -> "Samuel West").
        public static string Name(string name)
        {
            var cleaned = Clean(name);
            return string.IsNullOrEmpty(cleaned) ? null : cleaned;
        }

        // Names cleaned, empties dropped, duplicates (after cleaning, ignoring case) removed, first
        // spelling kept.
        public static List<string> Names(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Select(Name)
                .Where(n => n != null)
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }
    }
}
