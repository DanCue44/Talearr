using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Download
{
    public static class RemoteBookMediaTypeExtensions
    {
        public static BookMediaType GetPreferredMediaType(this RemoteBook remoteBook)
        {
            if (remoteBook?.Books == null || remoteBook.Books.Count == 0)
            {
                return BookMediaType.Audiobook;
            }

            var hasAudiobooks = remoteBook.Books.Any(b => b.MediaType == BookMediaType.Audiobook);
            var hasEbooks = remoteBook.Books.Any(b => b.MediaType == BookMediaType.Ebook);

            if (hasAudiobooks && !hasEbooks)
            {
                return BookMediaType.Audiobook;
            }

            if (hasEbooks && !hasAudiobooks)
            {
                return BookMediaType.Ebook;
            }

            // Ambiguous (mixed media types). Prefer audiobook to match historical behavior.
            return BookMediaType.Audiobook;
        }

        // Talearr: the download client category for a release. Comics use the comics category, or
        // the ebook category when none is set (setups from before comics keep working).
        public static string PickCategory(this RemoteBook remoteBook, string ebookCategory, string audiobookCategory, string comicsCategory)
        {
            if (remoteBook?.IsComic == true)
            {
                return string.IsNullOrWhiteSpace(comicsCategory) ? ebookCategory : comicsCategory;
            }

            return remoteBook.GetPreferredMediaType() == BookMediaType.Ebook ? ebookCategory : audiobookCategory;
        }

        public static List<Book> GetBooksMatchingReleaseMediaType(this RemoteBook remoteBook)
        {
            if (remoteBook?.Books == null || remoteBook.Books.Count == 0)
            {
                return new List<Book>();
            }

            var books = remoteBook.Books.Where(b => b != null).ToList();
            var mediaType = QualityMediaTypeHelper.DetectMediaType(remoteBook.ParsedBookInfo?.Quality?.Quality, remoteBook.Release);

            if (!mediaType.HasValue)
            {
                return books;
            }

            var matchingBooks = books.Where(b => b.MediaType == mediaType.Value).ToList();
            return matchingBooks.Any() ? matchingBooks : books;
        }
    }
}
