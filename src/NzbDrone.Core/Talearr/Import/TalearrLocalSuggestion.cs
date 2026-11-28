using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.Talearr.Import
{
    // Talearr (issue #13): Manual Import suggests a book from the metadata server when its own
    // matcher finds nothing, and the row reads "Select to Add". When that book is already in the
    // library (a Publisher's Pack added a moment ago) the row should be that book: importing it
    // then puts the file on it, instead of trying to add a book that exists and refusing the file.
    public static class TalearrLocalSuggestion
    {
        // The library's book for a suggestion, or null. Only a book of the same author and media
        // type that has no file yet, and only when the file does not name another pack.
        public static Book FindBook(DiscoveredFileWithMetadata file,
                                    BookMediaType mediaType,
                                    string authorProviderId,
                                    string bookProviderId,
                                    int? restrictToAuthorId,
                                    IAuthorService authorService,
                                    IBookService bookService,
                                    IEditionService editionService,
                                    IMediaFileService mediaFileService)
        {
            if (!ProviderIdHelper.TryNormalize(bookProviderId, defaultPrefix: null, out var bookId) || bookId.IndexOf(':') <= 0)
            {
                return null;
            }

            var author = FindAuthor(authorProviderId, restrictToAuthorId, authorService);
            if (author == null || author.Id <= 0)
            {
                return null;
            }

            Book book;
            var asin = TalearrAudibleBookRequests.Asin(bookId);
            if (asin != null)
            {
                book = TalearrAudibleBookRequests.FindBook(
                    bookService.GetBooksByAuthor(author.Id),
                    asin,
                    mediaType,
                    TalearrAudibleBookRequests.EditionsOfAuthor(author.Id, editionService));
            }
            else
            {
                var separator = bookId.IndexOf(':');
                var books = (bookService.FindAllByWorkProviderId(bookId.Substring(0, separator), ProviderIdHelper.StripPrefix(bookId), mediaType) ?? new List<Book>())
                    .Where(b => b != null && b.Id > 0 && b.AuthorId == author.Id && b.MediaType == mediaType)
                    .GroupBy(b => b.Id)
                    .Select(g => g.First())
                    .ToList();
                book = books.Count == 1 ? books[0] : null;
            }

            if (book == null)
            {
                return null;
            }

            if ((mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>()).Any())
            {
                return null;
            }

            // A pack file goes only on a pack entry that agrees with it (Books 1-2 never on Books 11-12).
            var pack = TalearrPackFiles.FromFile(file);
            if (pack != null && !TalearrPackFiles.Fits(pack, book))
            {
                return null;
            }

            return book;
        }

        private static Author FindAuthor(string authorProviderId, int? restrictToAuthorId, IAuthorService authorService)
        {
            if (ProviderIdHelper.TryNormalize(authorProviderId, defaultPrefix: null, out var normalized) && normalized.IndexOf(':') > 0)
            {
                var separator = normalized.IndexOf(':');
                var byProvider = authorService.FindByProviderId(normalized.Substring(0, separator), ProviderIdHelper.StripPrefix(normalized));
                if (byProvider != null)
                {
                    return restrictToAuthorId is > 0 && byProvider.Id != restrictToAuthorId.Value ? null : byProvider;
                }
            }

            return restrictToAuthorId is > 0 ? authorService.GetAuthor(restrictToAuthorId.Value) : null;
        }
    }
}
