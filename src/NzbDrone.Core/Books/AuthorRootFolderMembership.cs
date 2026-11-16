using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Books
{
    // Talearr: which authors a root folder holds for one format. An author belongs to a root
    // folder for a format when that format's root folder path on the author is the root folder.
    public static class AuthorRootFolderMembership
    {
        public static bool IsInRootFolder(Author author, string rootFolderPath, BookMediaType mediaType)
        {
            if (author == null || rootFolderPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var authorRootFolderPath = mediaType == BookMediaType.Audiobook
                ? author.AudiobookRootFolderPath
                : author.EbookRootFolderPath;

            return authorRootFolderPath.IsNotNullOrWhiteSpace() && authorRootFolderPath.PathEquals(rootFolderPath);
        }

        // Talearr (issue #8): authors in the folder for a format whose Monitor New Books differs
        // from the folder's value. An author with no value of its own counts as not matching.
        public static int CountNotMatching(IEnumerable<Author> authors, string rootFolderPath, BookMediaType mediaType, NewItemMonitorTypes? folderValue)
        {
            if (!folderValue.HasValue)
            {
                return 0;
            }

            return InRootFolder(authors, rootFolderPath, mediaType)
                .Count(author => (mediaType == BookMediaType.Audiobook
                    ? author.AudiobookMonitorNewItems
                    : author.EbookMonitorNewItems) != folderValue.Value);
        }

        public static List<Author> InRootFolder(IEnumerable<Author> authors, string rootFolderPath, BookMediaType mediaType)
        {
            return (authors ?? Enumerable.Empty<Author>())
                .Where(author => IsInRootFolder(author, rootFolderPath, mediaType))
                .ToList();
        }
    }
}
