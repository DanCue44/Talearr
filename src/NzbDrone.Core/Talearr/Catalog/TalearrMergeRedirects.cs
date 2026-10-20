using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Catalog
{
    /// <summary>
    /// Talearr: keeps merged duplicates merged across author refreshes.
    ///
    /// When the merge tool folds book row B into row A and the two rows had different provider
    /// ids, the metadata server can keep returning B's id. Without help the next refresh finds no
    /// local row for it and adds B again. This runs on the remote snapshot before the normal
    /// matching (like TalearrFallbackReconciler): a remote book that matches no local row by id
    /// but whose work id (or, for a book with no work id, an edition id) was merged into a local
    /// row of the same media type is
    ///   - dropped, when another remote book already matches that kept row (the server still
    ///     lists both works; the kept row is refreshed from its own one), or
    ///   - given the kept row's work id, so the normal refresh updates the kept row in place.
    /// Nothing is written to the database here.
    /// </summary>
    public static class TalearrMergeRedirects
    {
        public static int Apply(IReadOnlyCollection<Book> localBooks, List<Book> remoteBooks, IReadOnlyCollection<TalearrMergeRedirect> redirects, Logger logger = null, string authorName = null)
        {
            if (localBooks == null || localBooks.Count == 0 || remoteBooks == null || remoteBooks.Count == 0 || redirects == null || redirects.Count == 0)
            {
                return 0;
            }

            var localById = localBooks.Where(b => b != null && b.Id > 0).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            var localTokens = localById.Values.ToDictionary(b => b.Id, BookIdentity.GetProviderIdentityTokens);

            var lookup = new Dictionary<(string, int, string), int>();
            foreach (var redirect in redirects.Where(r => r != null && !string.IsNullOrWhiteSpace(r.ProviderId)))
            {
                lookup[(redirect.EntityType, redirect.MediaType, redirect.ProviderId.Trim().ToLowerInvariant())] = redirect.TargetId;
            }

            List<int> DirectMatches(Book remote)
            {
                var tokens = BookIdentity.GetProviderIdentityTokens(remote);
                return localById.Values
                    .Where(local => local.MediaType == remote.MediaType && localTokens[local.Id].Overlaps(tokens))
                    .Select(local => local.Id)
                    .ToList();
            }

            var directlyMatched = new HashSet<int>();
            var unmatched = new List<Book>();
            foreach (var remote in remoteBooks.Where(r => r != null))
            {
                var matches = DirectMatches(remote);
                if (matches.Count == 0)
                {
                    unmatched.Add(remote);
                }
                else
                {
                    directlyMatched.UnionWith(matches);
                }
            }

            var changed = 0;
            var claimed = new HashSet<int>();
            foreach (var remote in unmatched)
            {
                var target = FindTarget(remote, lookup, localById);
                if (target == null)
                {
                    continue;
                }

                if (directlyMatched.Contains(target.Id) || claimed.Contains(target.Id))
                {
                    remoteBooks.Remove(remote);
                    changed++;
                    logger?.Info("[TALEARR-MERGE] Skipped '{0}' ({1}) for author '{2}': it was merged into book {3} '{4}'",
                        remote.Title,
                        Describe(remote),
                        authorName ?? "Unknown",
                        target.Id,
                        target.Title);
                    continue;
                }

                var targetToken = BookIdentity.GetStableWorkProviderIdentityTokens(target).OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault()
                                  ?? BookIdentity.GetEditionProviderIdentityTokens(target).OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault();
                if (targetToken == null)
                {
                    continue;
                }

                remote.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                remote.RemoteProviderIds.Add(targetToken);
                claimed.Add(target.Id);
                changed++;
                logger?.Info("[TALEARR-MERGE] '{0}' ({1}) for author '{2}' updates book {3} '{4}' it was merged into",
                    remote.Title,
                    Describe(remote),
                    authorName ?? "Unknown",
                    target.Id,
                    target.Title);
            }

            return changed;
        }

        private static Book FindTarget(Book remote, Dictionary<(string, int, string), int> lookup, Dictionary<int, Book> localById)
        {
            var mediaType = (int)remote.MediaType;
            var workTokens = BookIdentity.GetStableWorkProviderIdentityTokens(remote);
            var entityType = workTokens.Count > 0 ? TalearrMergeRedirect.Book : TalearrMergeRedirect.Edition;
            var tokens = workTokens.Count > 0 ? workTokens : BookIdentity.GetEditionProviderIdentityTokens(remote);

            var targets = tokens
                .Select(token => lookup.TryGetValue((entityType, mediaType, token.ToLowerInvariant()), out var id) ? id : 0)
                .Where(id => id > 0 && localById.TryGetValue(id, out var local) && local.MediaType == remote.MediaType)
                .Distinct()
                .ToList();

            // Two different kept rows: ambiguous, leave it to the normal refresh.
            return targets.Count == 1 ? localById[targets[0]] : null;
        }

        private static string Describe(Book book)
        {
            return string.Join(",", BookIdentity.GetProviderIdentityTokens(book).OrderBy(t => t, StringComparer.Ordinal));
        }
    }
}
