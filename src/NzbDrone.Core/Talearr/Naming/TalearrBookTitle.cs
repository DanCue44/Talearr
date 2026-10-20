using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Naming
{
    // Talearr: the title that {Book Title} and its variants put into file and folder names.
    //
    // Upstream uses the edition's title, and audiobook edition titles are often a store listing
    // rather than the book's name: "Nineteen Eighty-Four (MP3 Book)", "Animal Farm (Audible Audio)",
    // "The King of Torts [Abridged 5-CD Set]". Talearr uses the book's own title instead (the work,
    // "1984"), falling back to the edition title when the book has none, and strips format noise
    // in brackets from whichever title it uses. Narrator and abridged disambiguation for additional
    // recordings is unchanged (FileNameBuilder.Talearr.cs).
    public static class TalearrBookTitle
    {
        // A bracketed group is noise when it holds at least one of these...
        private static readonly HashSet<string> NoiseWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mp3", "mp3s", "m4b", "m4a", "aac", "flac", "ogg", "wma",
            "cd", "cds", "audiocd", "mp3cd", "compact", "cassette", "cassettes", "tape", "tapes",
            "audio", "audible", "audiobook", "audiobooks", "audiofile", "playaway",
            "unabridged", "abridged", "abridgement", "abridgment"
        };

        // ...and nothing else but these, numbers and punctuation ("(MP3 Book)", "[Abridged 5-CD Set]",
        // "(9 Cassette Tapes)", "(Unabridged Library Edition)"). "(Book 1)", "(French Edition)",
        // "(Special Edition)" and "[Dramatised]" have no noise word and stay.
        private static readonly HashSet<string> FillerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "books", "edition", "ed", "version", "set", "box", "disc", "discs", "disk", "disks",
            "on", "and", "complete", "format", "library", "digital", "download", "retail", "the", "a", "an",
            "in", "of", "minutes", "min", "hours", "hrs", "volume", "vol", "full", "cast", "read", "by"
        };

        private static readonly Regex RoundGroupRegex = new Regex(@"\s*\((?<content>[^()]*)\)", RegexOptions.Compiled);
        private static readonly Regex SquareGroupRegex = new Regex(@"\s*\[(?<content>[^\[\]]*)\]", RegexOptions.Compiled);
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\d]+", RegexOptions.Compiled);

        // Bare noise at the very end: "Animal Farm Unabridged", "Gray Mountain Audiobook",
        // "The Street Lawyer COMPLETE AND UNABRIDGED".
        private static readonly Regex TrailingNoiseRegex = new Regex(
            @"(?:[\s,:;\-]+(?:complete\s+and\s+unabridged|unabridged|abridged|audio\s?book|audio\s+cd|mp3\s+cd|audible\s+audio(?:\s+edition)?))+\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ArticleOnlyRegex = new Regex(@"^\s*(?:(?:the|a|an)\s*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SpaceBeforePunctuationRegex = new Regex(@"\s+(?=[.,;:!?])", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        // The title for names: the book's own title when it has one, else the edition's, without
        // format noise.
        public static string ForNaming(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            Book book = null;
            try
            {
                book = edition.Book;
            }
            catch (Exception)
            {
                // Lazy loaded; a detached edition just uses its own title.
                book = null;
            }

            return ForNaming(book, edition);
        }

        public static string ForNaming(Book book, Edition edition)
        {
            var title = book?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = edition?.Title;
            }

            return Strip(title);
        }

        // Removes format noise from a title. Never returns an empty string for a non-empty title:
        // when everything would go, the title comes back as it was.
        public static string Strip(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var result = title;

            // Inner groups first, so "[AudioCD(2012)]" is judged on its own content.
            result = SquareGroupRegex.Replace(RoundGroupRegex.Replace(result, RemoveIfNoise), RemoveIfNoise);
            result = RoundGroupRegex.Replace(result, RemoveIfNoise);

            // "The Audiobook" is a title, not "The" plus noise.
            var withoutTrailing = TrailingNoiseRegex.Replace(result, string.Empty);
            if (!ArticleOnlyRegex.IsMatch(withoutTrailing))
            {
                result = withoutTrailing;
            }

            result = SpaceBeforePunctuationRegex.Replace(result, string.Empty);
            result = MultiSpaceRegex.Replace(result, " ");
            result = result.Trim().TrimEnd(',', ';', ':', '-').Trim();

            return result.Length == 0 ? title.Trim() : result;
        }

        public static bool IsNoiseGroup(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            var words = SplitWords(content);
            if (words.Count == 0 || !words.Any(w => NoiseWords.Contains(w)))
            {
                return false;
            }

            return words.All(w => NoiseWords.Contains(w) || FillerWords.Contains(w) || w.All(char.IsDigit));
        }

        private static string RemoveIfNoise(Match match)
        {
            if (!IsNoiseGroup(match.Groups["content"].Value))
            {
                return match.Value;
            }

            // Keep a separating space when the group sat between two words.
            return match.Value.Length > 0 && char.IsWhiteSpace(match.Value[0]) ? " " : string.Empty;
        }

        private static List<string> SplitWords(string content)
        {
            var words = new List<string>();
            foreach (Match match in WordRegex.Matches(content))
            {
                // "AudioCD" and "MP3CD" are one noise word each; split CamelCase only when the
                // whole word is not already known ("AudioBook" is "audiobook").
                var word = match.Value;
                if (NoiseWords.Contains(word) || FillerWords.Contains(word))
                {
                    words.Add(word);
                    continue;
                }

                var parts = Regex.Split(word, @"(?<=\p{Ll})(?=\p{Lu})");
                words.AddRange(parts.Where(p => p.Length > 0));
            }

            return words;
        }
    }
}
