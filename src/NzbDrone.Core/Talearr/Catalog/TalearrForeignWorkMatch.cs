using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Catalog
{
    // Talearr: with the Hardcover source, the library's books carry Hardcover ids, but a request
    // can name a work by another provider's id (Allseerr adds from a lookup that returns Goodreads
    // ids, "gr:4987669" for One Second After). Hardcover's own Goodreads links are per edition and
    // often miss that id, so the request is matched to the catalog book by its title instead,
    // within the requested author.
    public static class TalearrForeignWorkMatch
    {
        // A work id from a provider other than the Hardcover catalog. Audible ids (az:) have their
        // own handling (TalearrAudibleBookRequests).
        public static bool IsForeignWorkId(string providerId)
        {
            if (!ProviderIdHelper.TryNormalize(providerId, defaultPrefix: null, out var normalized) ||
                string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return normalized.StartsWith("gr:", StringComparison.OrdinalIgnoreCase) ||
                   normalized.StartsWith("ol:", StringComparison.OrdinalIgnoreCase);
        }

        // The one book of this media type whose title is the requested work's title. When several
        // share the title, the one the author's library already holds (in either format) wins, then
        // an exact title; otherwise there is no safe pick and null is returned.
        public static Book PickSameTitle(IEnumerable<Book> books, string title, BookMediaType mediaType, IEnumerable<Book> libraryBooks = null)
        {
            if (books == null || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var candidates = books
                .Where(book => book != null && book.MediaType == mediaType && TalearrTitleNormalizer.IsSameWorkTitle(book.Title, title))
                .ToList();

            if (candidates.Count <= 1)
            {
                return candidates.SingleOrDefault();
            }

            var libraryWorks = new HashSet<string>(
                (libraryBooks ?? Enumerable.Empty<Book>())
                    .Where(book => book != null && TalearrTitleNormalizer.IsSameWorkTitle(book.Title, title))
                    .Select(book => HardcoverWork(book))
                    .Where(id => id != null),
                StringComparer.OrdinalIgnoreCase);
            var inLibrary = candidates.Where(book => HardcoverWork(book) is string id && libraryWorks.Contains(id)).ToList();
            if (inLibrary.Count == 1)
            {
                return inLibrary[0];
            }

            var normalizedTitle = TalearrTitleNormalizer.NormalizeFull(title);
            var exact = (inLibrary.Count > 1 ? inLibrary : candidates)
                .Where(book => string.Equals(TalearrTitleNormalizer.NormalizeFull(book.Title), normalizedTitle, StringComparison.Ordinal))
                .ToList();

            return exact.Count == 1 ? exact[0] : null;
        }

        // Carries a requested work into Hardcover's catalog: Hardcover's book of the same title
        // gets the requested id (a foreign one) and the server copy's work ids, so the add and
        // the refresh treat it as requested and its provider aliases find it by that id. When
        // Hardcover has no book of that title, the server's copy (if any) is added instead.
        // Returns the book that now stands for the request, or null when none is safe to pick.
        public static Book Bridge(List<Book> hardcoverBooks, Book serverBook, string foreignProviderId, string title, BookMediaType mediaType, IEnumerable<Book> libraryBooks)
        {
            if (hardcoverBooks == null)
            {
                return null;
            }

            var same = PickSameTitle(hardcoverBooks, title, mediaType, libraryBooks);
            if (same != null)
            {
                same.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (ProviderIdHelper.TryNormalize(foreignProviderId, defaultPrefix: null, out var requested))
                {
                    same.RemoteProviderIds.Add(requested);
                }

                if (serverBook != null)
                {
                    same.RemoteProviderIds.UnionWith(BookIdentity.GetStableWorkProviderIdentityTokens(serverBook));
                }

                return same;
            }

            if (serverBook != null &&
                !hardcoverBooks.Any(b => b != null && b.MediaType == mediaType && TalearrTitleNormalizer.IsSameWorkTitle(b.Title, serverBook.Title)))
            {
                hardcoverBooks.Add(serverBook);
                return serverBook;
            }

            return null;
        }

        private static string HardcoverWork(Book book)
        {
            if (string.IsNullOrWhiteSpace(book?.HardcoverBookId))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(book.HardcoverBookId.Trim(), "hc");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
