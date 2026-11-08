using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Catalog
{
    /// <summary>
    /// Talearr: reattaches books whose provider ids the metadata server no longer returns.
    ///
    /// A library carried over from Chaptarr can hold rows with an old Goodreads id that the server
    /// has since replaced (the same book now comes back under another gr: or hc: id). The refresh
    /// matches by id only, so such a row is never updated ("was not found, it may have been
    /// removed from Metadata sources") and the server's copy is added next to it as a second row:
    /// one row keeps the files, the other the series number.
    ///
    /// This runs on the remote snapshot after the merge redirects, before the normal matching.
    /// A local row whose work ids no remote book has is attached to the one remote book of the
    /// same media type with the same full title that no local row matches either (a shared
    /// edition id picks between several). The remote book
    /// is given the local row's id, so the normal refresh updates the row in place and the drift
    /// check then moves it to the server's ids (files, monitoring and history stay). Several stale
    /// rows of one title are attached together only when they share an id (recording copies of
    /// one work); anything ambiguous is left to the normal refresh rules.
    /// Nothing is written to the database here.
    /// </summary>
    public static class TalearrStaleIdentityReconciler
    {
        public static int Attach(IReadOnlyCollection<Book> localBooks, List<Book> remoteBooks, Logger logger = null, string authorName = null)
        {
            if (localBooks == null || localBooks.Count == 0 || remoteBooks == null || remoteBooks.Count == 0)
            {
                return 0;
            }

            var locals = localBooks.Where(b => b != null && b.Id > 0).GroupBy(b => b.Id).Select(g => g.First()).ToList();
            var remotes = remoteBooks.Where(r => r != null).ToList();

            // The refresh matches a work by its work ids first: an old row that shares only edition
            // ids (ASINs) with the server's copy still counts as another work, so compare work ids.
            var localWork = locals.ToDictionary(b => b.Id, BookIdentity.GetStableWorkProviderIdentityTokens);
            var remoteWork = remotes.ToDictionary(r => r, BookIdentity.GetStableWorkProviderIdentityTokens, BookReferenceComparer.Instance);

            bool SameWork(Book local, Book remote)
            {
                return local.MediaType == remote.MediaType && localWork[local.Id].Overlaps(remoteWork[remote]);
            }

            var stale = locals
                .Where(local => localWork[local.Id].Count > 0 && !remotes.Any(remote => SameWork(local, remote)))
                .ToList();

            if (stale.Count == 0)
            {
                return 0;
            }

            var unmatchedRemotes = remotes
                .Where(remote => remoteWork[remote].Count > 0 && !locals.Any(local => SameWork(local, remote)))
                .ToList();

            var attached = 0;
            foreach (var group in stale.GroupBy(local => (local.MediaType, Title: TalearrTitleNormalizer.NormalizeFull(local.Title))))
            {
                if (string.IsNullOrWhiteSpace(group.Key.Title))
                {
                    continue;
                }

                var candidates = unmatchedRemotes
                    .Where(remote => remote.MediaType == group.Key.MediaType &&
                                     string.Equals(TalearrTitleNormalizer.NormalizeFull(remote.Title), group.Key.Title, StringComparison.Ordinal))
                    .ToList();

                // Two server books with this title (a reissue, a different work): only a shared
                // edition id (the same ASIN) tells them apart.
                if (candidates.Count > 1)
                {
                    var editions = new HashSet<string>(group.SelectMany(BookIdentity.GetEditionProviderIdentityTokens), StringComparer.OrdinalIgnoreCase);
                    candidates = candidates.Where(c => BookIdentity.GetEditionProviderIdentityTokens(c).Overlaps(editions)).ToList();
                }

                if (candidates.Count != 1)
                {
                    continue;
                }

                var token = SharedToken(group.ToList());
                if (token == null)
                {
                    logger?.Debug("[TALEARR-STALE-ID] {0} stale rows of '{1}' for author '{2}' have different ids; left to the normal refresh rules",
                        group.Count(),
                        group.First().Title,
                        authorName ?? "Unknown");
                    continue;
                }

                var remote = candidates[0];
                remote.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                remote.RemoteProviderIds.Add(token);
                unmatchedRemotes.Remove(remote);
                attached++;

                logger?.Info("[TALEARR-STALE-ID] Book {0} '{1}' ({2}) for author '{3}' is no longer listed under that id; updating it from '{4}' ({5})",
                    string.Join(",", group.Select(b => b.Id)),
                    group.First().Title,
                    token,
                    authorName ?? "Unknown",
                    remote.Title,
                    string.Join(",", remoteWork[remote].Where(t => !string.Equals(t, token, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t, StringComparer.Ordinal)));
            }

            return attached;
        }

        // The id every row of the group has: a work id first, else an edition id.
        private static string SharedToken(List<Book> rows)
        {
            string Pick(Func<Book, HashSet<string>> tokens)
            {
                var shared = tokens(rows[0]);
                foreach (var row in rows.Skip(1))
                {
                    shared.IntersectWith(tokens(row));
                }

                return shared.OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault();
            }

            return Pick(BookIdentity.GetStableWorkProviderIdentityTokens) ?? Pick(BookIdentity.GetEditionProviderIdentityTokens);
        }
    }
}
