using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.Import
{
    // Talearr: the last check before a tracked download is imported into the book it was grabbed
    // for. A file that explicitly credits a different author ("Craig Rice - 45 Murderers- A
    // Collection of True Crime Stories" for Ryan Becker's "True Crime Stories: 48 Terrifying True
    // Crime Murder Cases") is not that book, however well the title words lined up.
    public static class TalearrImportAuthorCheck
    {
        public const string RejectionPrefix = "Completed download is by ";

        // Ebook author metadata (dc:creator) is reliable. Audiobook ARTIST / ALBUMARTIST tags are
        // often the narrator, so they are not used to reject.
        private static readonly string[] AuthorTagKeys = { "AUTHOR", "AUTHORS", "ALL_AUTHORS" };

        public static string FindOtherAuthor(LocalBook item, Author expectedAuthor, Book expectedBook, string downloadTitle)
        {
            if (item == null || expectedAuthor?.Name.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            var books = expectedBook == null ? Array.Empty<Book>() : new[] { expectedBook };
            var fileName = item.Path.IsNullOrWhiteSpace() ? null : Path.GetFileNameWithoutExtension(item.Path);
            var authorTags = GetAuthorTagValues(item).ToList();

            // Any mention of the expected author (or an alias or pen name) settles it.
            if (ReleaseIdentityEvidence.TextNamesAuthor(expectedAuthor, downloadTitle) ||
                ReleaseIdentityEvidence.TextNamesAuthor(expectedAuthor, fileName) ||
                authorTags.Any(value => ReleaseIdentityEvidence.NameMatchesAuthor(expectedAuthor, value) ||
                                        ReleaseIdentityEvidence.TextNamesAuthor(expectedAuthor, value)))
            {
                return null;
            }

            var otherTag = authorTags.FirstOrDefault(value => TalearrReleaseIdentityGuard.IsOtherAuthorTag(value, expectedAuthor, books));
            if (otherTag != null)
            {
                return IsMisspelling(otherTag, expectedAuthor.Name) ? null : otherTag.Trim();
            }

            // Audiobook part files are often named after the narrator or the chapter, so only an
            // ebook's own file name is read as "Author - Title".
            var isAudio = item.Path.IsNotNullOrWhiteSpace() &&
                          MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(item.Path));
            var fromFileName = isAudio ? null : TalearrReleaseIdentityGuard.FindOtherAuthor(fileName, expectedAuthor, books);
            var other = fromFileName ?? TalearrReleaseIdentityGuard.FindOtherAuthor(downloadTitle, expectedAuthor, books);

            return other != null && IsMisspelling(other, expectedAuthor.Name) ? null : other;
        }

        // A name that differs from the expected author's by one slip in one word of seven letters or more ("William R.
        // Fortschen" for William R. Forstchen: two letters swapped) is the same author misspelled,
        // not someone else. Every other word must match, and the slipped word keeps its first letter.
        internal static bool IsMisspelling(string found, string expected)
        {
            var foundWords = NameWords(found);
            var expectedWords = NameWords(expected);
            if (foundWords.Count == 0 || foundWords.Count != expectedWords.Count)
            {
                return false;
            }

            var differing = foundWords.Zip(expectedWords, (a, b) => (a, b)).Where(pair => pair.a != pair.b).ToList();
            if (differing.Count != 1)
            {
                return false;
            }

            var (word, wanted) = differing[0];
            return wanted.Length >= 7 && word.Length >= 7 && word[0] == wanted[0] && OneSlipApart(word, wanted);
        }

        private static List<string> NameWords(string name)
        {
            return (name ?? string.Empty)
                .ToLowerInvariant()
                .Split(new[] { ' ', '.', ',', '-', '\'', '\u2019' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        // One letter changed, added, dropped, or two neighbouring letters swapped.
        private static bool OneSlipApart(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 1)
            {
                return false;
            }

            var start = 0;
            while (start < a.Length && start < b.Length && a[start] == b[start])
            {
                start++;
            }

            var restA = a.Substring(start);
            var restB = b.Substring(start);

            if (restA.Length == restB.Length)
            {
                return restA.Substring(Math.Min(1, restA.Length)) == restB.Substring(Math.Min(1, restB.Length)) ||
                       (restA.Length >= 2 && restA[0] == restB[1] && restA[1] == restB[0] && restA.Substring(2) == restB.Substring(2));
            }

            return restA.Length > restB.Length ? restA.Substring(1) == restB : restB.Substring(1) == restA;
        }

        public static string BuildRejection(string otherAuthor, Author expectedAuthor, string bookLabel)
        {
            return $"{RejectionPrefix}{otherAuthor}, not {expectedAuthor?.Name} (grabbed for {bookLabel}).";
        }

        private static IEnumerable<string> GetAuthorTagValues(LocalBook item)
        {
            var tags = item.RawTags?.AllTags;
            if (tags == null || tags.Count == 0)
            {
                yield break;
            }

            foreach (var entry in tags)
            {
                if (!AuthorTagKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase) || entry.Value == null)
                {
                    continue;
                }

                foreach (var value in entry.Value
                             .Where(value => value.IsNotNullOrWhiteSpace())
                             .SelectMany(value => value.Split(';'))
                             .Select(value => value.Trim())
                             .Where(value => value.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    yield return value;
                }
            }
        }
    }
}
