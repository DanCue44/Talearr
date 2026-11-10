using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

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
        public static Book FindBook(IEnumerable<Book> authorBooks, string asin, BookMediaType mediaType, Func<int, List<Edition>> editionsOf)
        {
            var books = (authorBooks ?? Enumerable.Empty<Book>())
                .Where(book => book != null && book.MediaType == mediaType)
                .Select(book => (Book: book, Asins: TalearrAudibleSourceSettings.KeptAsins(book).ToList()))
                .Where(entry => entry.Asins.Count > 0)
                .OrderBy(entry => entry.Book.Id)
                .ToList();

            return books.FirstOrDefault(entry => entry.Asins.Contains(asin, StringComparer.OrdinalIgnoreCase)).Book ??
                   books.FirstOrDefault(entry => (editionsOf?.Invoke(entry.Book.Id) ?? new List<Edition>())
                       .Any(edition => string.Equals(edition?.Asin, asin, StringComparison.OrdinalIgnoreCase) ||
                                       Asin(edition?.ForeignEditionId) == asin)).Book;
        }
    }
}
