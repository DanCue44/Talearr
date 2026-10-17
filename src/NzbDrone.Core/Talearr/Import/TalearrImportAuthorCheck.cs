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
    // TaleArr: the last check before a tracked download is imported into the book it was grabbed
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
                return otherTag.Trim();
            }

            // Audiobook part files are often named after the narrator or the chapter, so only an
            // ebook's own file name is read as "Author - Title".
            var isAudio = item.Path.IsNotNullOrWhiteSpace() &&
                          MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(item.Path));
            var fromFileName = isAudio ? null : TalearrReleaseIdentityGuard.FindOtherAuthor(fileName, expectedAuthor, books);

            return fromFileName ?? TalearrReleaseIdentityGuard.FindOtherAuthor(downloadTitle, expectedAuthor, books);
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
