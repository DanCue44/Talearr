using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// rows of one title are attached together when they share an id (recording copies of one
    /// work), or when at most one of them is worth keeping, so the empty copies merge into it;
    /// anything ambiguous is left to the normal refresh rules.
    ///
    /// When the server's copy is already matched, but only by rows the refresh may merge away (no
    /// files, nothing pinned), an old row worth keeping is attached to it as well. The refresh then
    /// keeps the row with the files and deletes the empty copy, instead of showing the book twice.
    /// Rows known only by an edition id (an Audible ASIN) count as old once no server book shares
    /// that id, and "Part 1", "Book 1" and "Volume 1" compare equal to a bare "1". A series label
    /// after the title ("Opening Moves: Symbiote Wars book 2") is not another book.
    /// Nothing is written to the database here.
    /// </summary>
    public static class TalearrStaleIdentityReconciler
    {
        private static readonly Regex TrailingNumber = new Regex(@"\s*\d+$", RegexOptions.Compiled);
        private static readonly Regex NumberedPartRegex = new Regex(@"\b(?:part|book|volume|vol|episode)\s+(\d+)$", RegexOptions.Compiled);

        // isDisposable: whether the refresh may merge a row away (no files, not pinned). Without
        // it a server book another row matches is never taken.
        public static int Attach(IReadOnlyCollection<Book> localBooks, List<Book> remoteBooks, Logger logger = null, string authorName = null, Func<Book, bool> isDisposable = null)
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

            // A row with no work id is matched by its edition ids, so it is old only when no server
            // book of its media type shares one.
            var localEditions = locals.ToDictionary(b => b.Id, BookIdentity.GetEditionProviderIdentityTokens);
            var remoteEditions = remotes.ToDictionary(r => r, BookIdentity.GetEditionProviderIdentityTokens, BookReferenceComparer.Instance);

            bool IsStale(Book local)
            {
                if (localWork[local.Id].Count > 0)
                {
                    return !remotes.Any(remote => SameWork(local, remote));
                }

                return localEditions[local.Id].Count > 0 &&
                       !remotes.Any(remote => remote.MediaType == local.MediaType && localEditions[local.Id].Overlaps(remoteEditions[remote]));
            }

            var stale = locals.Where(IsStale).ToList();

            if (stale.Count == 0)
            {
                return 0;
            }

            // Server books no row matches, or only rows the refresh may merge away
            var takenBy = remotes.ToDictionary(r => r, r => locals.Where(local => SameWork(local, r)).ToList(), BookReferenceComparer.Instance);
            var unmatchedRemotes = remotes
                .Where(remote => remoteWork[remote].Count > 0 &&
                                 (takenBy[remote].Count == 0 || (isDisposable != null && takenBy[remote].All(isDisposable))))
                .ToList();

            var attached = 0;
            foreach (var group in stale.GroupBy(local => (local.MediaType, Title: MainKey(local.Title))))
            {
                if (string.IsNullOrWhiteSpace(group.Key.Title))
                {
                    continue;
                }

                // Merging an empty copy away only makes sense for an old row worth keeping
                var keepsSomething = isDisposable != null && group.Any(local => !isDisposable(local));
                var candidates = unmatchedRemotes
                    .Where(remote => remote.MediaType == group.Key.MediaType &&
                                     (takenBy[remote].Count == 0 || keepsSomething) &&
                                     group.All(local => SameTitle(local.Title, remote.Title)))
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

                // Old copies under different ids (an old Goodreads row next to an Audiobookshelf
                // import) are one book when at most one of them is worth keeping.
                var token = SharedToken(group.ToList());
                if (token == null && (isDisposable == null || group.Count(local => !isDisposable(local)) > 1))
                {
                    logger?.Debug("[TALEARR-STALE-ID] {0} stale rows of '{1}' for author '{2}' have different ids; left to the normal refresh rules",
                        group.Count(),
                        group.First().Title,
                        authorName ?? "Unknown");
                    continue;
                }

                var remote = candidates[0];
                if (token == null)
                {
                    foreach (var local in group)
                    {
                        if (localWork[local.Id].Count == 0)
                        {
                            local.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            local.RemoteProviderIds.UnionWith(remoteWork[remote]);
                        }
                        else
                        {
                            remote.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            remote.RemoteProviderIds.UnionWith(localWork[local.Id]);
                        }
                    }
                }
                else if (group.All(local => localWork[local.Id].Count == 0))
                {
                    // The refresh never matches a server book with a work id by an edition id, so
                    // rows known only by an ASIN take the server's work ids for this refresh instead.
                    foreach (var local in group)
                    {
                        local.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        local.RemoteProviderIds.UnionWith(remoteWork[remote]);
                    }
                }
                else
                {
                    remote.RemoteProviderIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    remote.RemoteProviderIds.Add(token);
                }

                unmatchedRemotes.Remove(remote);
                attached++;

                logger?.Info("[TALEARR-STALE-ID] Book {0} '{1}' ({2}) for author '{3}' is no longer listed under that id; updating it from '{4}' ({5})",
                    string.Join(",", group.Select(b => b.Id)),
                    group.First().Title,
                    token ?? "different ids",
                    authorName ?? "Unknown",
                    remote.Title,
                    string.Join(",", remoteWork[remote].Where(t => !string.Equals(t, token, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t, StringComparer.Ordinal)));
            }

            return attached;
        }

        // "Part 1", "Book 1" or "Volume 1" at the end of a normalized title read as "1"
        private static string PartNumber(string normalized)
        {
            return NumberedPartRegex.Replace(normalized, "$1");
        }

        private static string MainKey(string title)
        {
            return PartNumber(TalearrTitleNormalizer.NormalizeMain(title));
        }

        // The same title, or one side has no subtitle and the main titles agree ("The
        // Synchronicity War 1" and "The Synchronicity War Part 1: Scifi action and adventure").
        // Two different subtitles never match, nor does a subtitle that is only a number ("Fist of
        // Orion: Book 2" is the next book, not "Fist of Orion"). A series label with a number
        // does ("Opening Moves: Symbiote Wars book 2" is "Opening Moves").
        private static bool SameTitle(string left, string right)
        {
            var leftFull = PartNumber(TalearrTitleNormalizer.NormalizeFull(left));
            var rightFull = PartNumber(TalearrTitleNormalizer.NormalizeFull(right));
            if (string.Equals(leftFull, rightFull, StringComparison.Ordinal))
            {
                return true;
            }

            var leftHasSubtitle = TalearrTitleNormalizer.HasSubtitle(TalearrTitleNormalizer.StripFormatBrackets(left));
            var rightHasSubtitle = TalearrTitleNormalizer.HasSubtitle(TalearrTitleNormalizer.StripFormatBrackets(right));
            if ((leftHasSubtitle && rightHasSubtitle) ||
                (leftHasSubtitle && IsNumberedSubtitle(left)) ||
                (rightHasSubtitle && IsNumberedSubtitle(right)))
            {
                return false;
            }

            var main = MainKey(left);
            return main.Length > 0 && string.Equals(main, MainKey(right), StringComparison.Ordinal);
        }

        // A subtitle that numbers another book of the series: "Book 2", or the title again with a
        // number ("Fist of Orion: Fist of Orion, Book 2")
        private static bool IsNumberedSubtitle(string title)
        {
            var stripped = TalearrTitleNormalizer.StripFormatBrackets(title);
            var subtitle = PartNumber(TalearrTitleNormalizer.NormalizeFull(TalearrTitleNormalizer.GetSubtitle(stripped)));
            var label = TrailingNumber.Replace(subtitle, string.Empty);
            if (label.Length == subtitle.Length)
            {
                return false;
            }

            return label.Length == 0 || string.Equals(label, TalearrTitleNormalizer.NormalizeMain(stripped), StringComparison.Ordinal);
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
