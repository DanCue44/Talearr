using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Catalog
{
    // Talearr: the remote catalog of a Tracked author (TalearrAuthorCatalogStore), cut down to the
    // books the user keeps. Runs after the metadata profile and the other normalization steps, on
    // add (AuthorLibraryService) and on refresh (RefreshAuthorService), per media type:
    //   - a remote book that matches a kept local book of the same media type stays, so refresh
    //     keeps updating it (same work-first identity rules as refresh matching, BookIdentity);
    //   - a remote book the current add explicitly requested stays, so it is inserted;
    //   - everything else is left out and never inserted.
    // A local book that is not kept no longer has a remote counterpart, so the normal refresh
    // pruning (RefreshBookService.ShouldDelete) removes it unless it has files or is pinned.
    public static class TalearrTrackedCatalogFilter
    {
        // The same reasons RefreshBookService.ShouldDelete keeps a local row: manual add,
        // monitored, strict or manually picked edition, or files.
        public static bool IsKeptLocalBook(Book book, bool hasFiles)
        {
            if (book == null)
            {
                return false;
            }

            return hasFiles ||
                   book.Monitored ||
                   book.AddOptions?.AddType == BookAddType.Manual ||
                   !book.AnyEditionOk ||
                   book.Editions?.Any(edition => edition?.ManualAdd == true) == true;
        }

        public static List<Book> Apply(
            List<Book> remoteBooks,
            IEnumerable<Book> keptLocalBooks,
            Func<Book, bool> isRequested,
            Logger logger,
            Author local,
            string logContext)
        {
            var remote = remoteBooks ?? new List<Book>();
            if (remote.Count == 0)
            {
                return remote;
            }

            var keptByMediaType = (keptLocalBooks ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .GroupBy(book => book.MediaType)
                .ToDictionary(group => group.Key, group => group.ToList());

            var result = new List<Book>();
            var matchedLocal = 0;
            var requested = 0;

            foreach (var book in remote.Where(book => book != null))
            {
                if (keptByMediaType.TryGetValue(book.MediaType, out var candidates) &&
                    BookIdentity.FindWorkFirstMatchResult(candidates, book).Matches.Any())
                {
                    matchedLocal++;
                    result.Add(book);
                    continue;
                }

                if (isRequested?.Invoke(book) == true)
                {
                    requested++;
                    result.Add(book);
                }
            }

            logger?.Info("{0}: Author '{1}' tracks only its own books; returning {2} of {3} books ({4} already in the library, {5} requested, {6} left out of the catalog)",
                logContext,
                local?.Name ?? "Unknown",
                result.Count,
                remote.Count,
                matchedLocal,
                requested,
                remote.Count - result.Count);

            return result;
        }
    }
}
