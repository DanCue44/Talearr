using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Import;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: reads the ASIN of an audiobook picked from an Audible result of the add search
    // (TalearrAudibleBookSearch). Such a book carries only az:{ASIN} ids.
    public static class TalearrAudibleBookRequests
    {
        private static readonly Regex AsinIdRegex = new Regex(@"^az:(?<asin>[A-Z0-9]{10})(?:-[a-z]+)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "az:B0D4FKJX6M" (or "az:B0D4FKJX6M-audiobook") gives B0D4FKJX6M; anything else null.
        public static string Asin(string providerId)
        {
            var match = AsinIdRegex.Match((providerId ?? string.Empty).Trim());
            return match.Success ? match.Groups["asin"].Value.ToUpperInvariant() : null;
        }

        // The ASIN of a requested book that has no Hardcover, Goodreads or Open Library work id.
        public static string RequestedAsin(Book book)
        {
            if (book == null || BookEditionIdentity.GetCanonicalWorkProviderIds(book).Any())
            {
                return null;
            }

            var ids = new List<string> { book.ForeignEditionId, book.BaseBookId, book.TitleSlug };
            ids.AddRange((book.Editions ?? new List<Edition>()).Select(e => e?.ForeignEditionId));
            ids.AddRange(book.RemoteProviderIds ?? Enumerable.Empty<string>());

            return ids.Select(Asin).FirstOrDefault(asin => asin != null);
        }

        // The author's book for an ASIN added from Audible. The book is named after its work's
        // lead listing (az:{ASIN}); another listing of the same work is one of its editions.
        // Talearr #13: when the listing is Hardcover's book (a Publisher's Pack Hardcover lists
        // too), the Audible merge leaves that book's ids alone and the ASIN is on one of its
        // editions, so a book with no az: id of its own counts when it is the only one with an
        // edition of that ASIN.
        public static Book FindBook(IEnumerable<Book> authorBooks, string asin, BookMediaType mediaType, Func<int, List<Edition>> editionsOf)
        {
            var sameMedia = (authorBooks ?? Enumerable.Empty<Book>())
                .Where(book => book != null && book.MediaType == mediaType)
                .GroupBy(book => book.Id)
                .Select(group => group.First())
                .OrderBy(book => book.Id)
                .ToList();

            var books = sameMedia
                .Select(book => (Book: book, Asins: TalearrAudibleSourceSettings.KeptAsins(book).ToList()))
                .Where(entry => entry.Asins.Count > 0)
                .ToList();

            var found = books.FirstOrDefault(entry => entry.Asins.Contains(asin, StringComparer.OrdinalIgnoreCase)).Book ??
                        books.FirstOrDefault(entry => HasEdition(editionsOf?.Invoke(entry.Book.Id), asin)).Book;
            if (found != null)
            {
                return found;
            }

            var kept = books.Select(entry => entry.Book.Id).ToHashSet();
            var carriers = sameMedia
                .Where(book => !kept.Contains(book.Id) &&
                               (string.Equals(book.AudibleASIN, asin, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(book.ASIN, asin, StringComparison.OrdinalIgnoreCase) ||
                                HasEdition(editionsOf?.Invoke(book.Id), asin)))
                .ToList();

            if (carriers.Count == 1)
            {
                return carriers[0];
            }

            // Hardcover can list a pack's recording on book 1 of the series as well (#14); the
            // pack entry is the one that holds it.
            var packs = carriers.Where(book => TalearrPackFiles.BookPack(book) != null).ToList();
            return packs.Count == 1 ? packs[0] : null;
        }

        // Editions per book for FindBook, read once for the whole author.
        public static Func<int, List<Edition>> EditionsOfAuthor(int authorId, IEditionService editionService)
        {
            if (editionService == null)
            {
                return _ => new List<Edition>();
            }

            Dictionary<int, List<Edition>> byBook = null;
            return bookId =>
            {
                if (byBook == null)
                {
                    byBook = (editionService.GetEditionsByAuthor(authorId) ?? new List<Edition>())
                        .Where(edition => edition != null)
                        .GroupBy(edition => edition.BookId)
                        .ToDictionary(group => group.Key, group => group.ToList());
                }

                // An author-wide read that came back empty falls back to the book's own editions.
                return byBook.Count > 0
                    ? byBook.GetValueOrDefault(bookId) ?? new List<Edition>()
                    : editionService.GetEditionsByBook(bookId) ?? new List<Edition>();
            };
        }

        private static bool HasEdition(IEnumerable<Edition> editions, string asin)
        {
            return (editions ?? Enumerable.Empty<Edition>())
                .Any(edition => string.Equals(edition?.Asin, asin, StringComparison.OrdinalIgnoreCase) ||
                                Asin(edition?.ForeignEditionId) == asin);
        }
    }
}
