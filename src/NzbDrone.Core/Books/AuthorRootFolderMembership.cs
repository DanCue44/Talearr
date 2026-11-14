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

        public static List<Author> InRootFolder(IEnumerable<Author> authors, string rootFolderPath, BookMediaType mediaType)
        {
            return (authors ?? Enumerable.Empty<Author>())
                .Where(author => IsInRootFolder(author, rootFolderPath, mediaType))
                .ToList();
        }
    }
}
