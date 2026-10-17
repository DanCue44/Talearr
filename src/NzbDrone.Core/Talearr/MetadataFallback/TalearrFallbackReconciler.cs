using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    /// <summary>
    /// TaleArr: attaches books that were added from Open Library during a metadata server outage
    /// to the server's own books on the first refresh after recovery.
    ///
    /// Such a local book only has an Open Library work id (ol:OL...W). The metadata server does
    /// not return Open Library ids, so without help the refresh would add the server's copy as a
    /// new book next to it (or prune it, losing monitoring). This runs on the remote snapshot,
    /// like TalearrCatalogDedupe, before the normal matching: for each Open Library-only local
    /// book it looks for exactly one remote book of the same media type that has no Open Library
    /// id yet, first by a shared ISBN-13, then by the same full title. On a match the remote book
    /// is given the local book's ol: work id, so the normal refresh matches the two by that id
    /// and updates the existing row in place (files, monitoring and history stay). No match, or
    /// more than one candidate: nothing changes and the local book is kept or pruned by the
    /// normal refresh rules (rows with files or a manual add are always kept).
    /// Nothing is written to the database here.
    /// </summary>
    public static class TalearrFallbackReconciler
    {
        public static bool IsOpenLibraryOnly(Book book)
        {
            if (book == null || string.IsNullOrWhiteSpace(book.OpenLibraryWorkId) ||
                !string.IsNullOrWhiteSpace(book.HardcoverBookId) || !string.IsNullOrWhiteSpace(book.GoodreadsWorkId))
            {
                return false;
            }

            return BookIdentity.GetStableWorkProviderIdentityTokens(book)
                .All(token => token.StartsWith("ol:", StringComparison.OrdinalIgnoreCase));
        }

        public static int Attach(IEnumerable<Book> localBooks, IReadOnlyCollection<Book> remoteBooks, Logger logger = null, string authorName = null)
        {
            var fallbackBooks = (localBooks ?? Enumerable.Empty<Book>())
                .Where(IsOpenLibraryOnly)
                .ToList();

            if (fallbackBooks.Count == 0 || remoteBooks == null || remoteBooks.Count == 0)
            {
                return 0;
            }

            var remoteOlTokens = new HashSet<string>(remoteBooks
                .Where(b => b != null && !string.IsNullOrWhiteSpace(b.OpenLibraryWorkId))
                .Select(b => NormalizeWorkId(b.OpenLibraryWorkId))
                .Where(id => id != null),
                StringComparer.OrdinalIgnoreCase);

            var claimed = new HashSet<Book>(BookReferenceComparer.Instance);
            var attached = 0;

            foreach (var local in fallbackBooks)
            {
                var olId = NormalizeWorkId(local.OpenLibraryWorkId);
                if (olId == null || remoteOlTokens.Contains(olId))
                {
                    // Already matched by id (the server knows this Open Library work).
                    continue;
                }

                var candidates = remoteBooks
                    .Where(r => r != null &&
                                r.MediaType == local.MediaType &&
                                string.IsNullOrWhiteSpace(r.OpenLibraryWorkId) &&
                                BookIdentity.GetStableWorkProviderIdentityTokens(r).Count > 0 &&
                                !claimed.Contains(r))
                    .ToList();

                var target = FindByIsbn(local, candidates) ?? FindByTitle(local, candidates);
                if (target == null)
                {
                    logger?.Debug("[TALEARR-FALLBACK] No unique metadata server match for Open Library book '{0}' ({1}); left to the normal refresh rules",
                        local.Title,
                        olId);
                    continue;
                }

                target.OpenLibraryWorkId = olId;
                claimed.Add(target);
                remoteOlTokens.Add(olId);
                attached++;

                logger?.Info("[TALEARR-FALLBACK] Attached Open Library book '{0}' ({1}) to '{2}' ({3}) for author '{4}'",
                    local.Title,
                    olId,
                    target.Title,
                    string.Join(",", BookIdentity.GetStableWorkProviderIdentityTokens(target).Where(t => !t.StartsWith("ol:", StringComparison.OrdinalIgnoreCase)).OrderBy(t => t, StringComparer.Ordinal)),
                    authorName ?? "Unknown");
            }

            return attached;
        }

        private static Book FindByIsbn(Book local, List<Book> candidates)
        {
            var localIsbns = Isbn13s(local);
            if (localIsbns.Count == 0)
            {
                return null;
            }

            var matches = candidates.Where(c => Isbn13s(c).Overlaps(localIsbns)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static Book FindByTitle(Book local, List<Book> candidates)
        {
            var title = TalearrTitleNormalizer.NormalizeFull(local.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var matches = candidates
                .Where(c => string.Equals(TalearrTitleNormalizer.NormalizeFull(c.Title), title, StringComparison.Ordinal))
                .ToList();

            return matches.Count == 1 ? matches[0] : null;
        }

        private static HashSet<string> Isbn13s(Book book)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            void Add(string isbn)
            {
                var normalized = OpenLibraryJson.NormalizeIsbn(isbn);
                if (normalized?.Length == 13)
                {
                    result.Add(normalized);
                }
            }

            Add(book?.ISBN13);

            List<Edition> editions;
            try
            {
                editions = book?.Editions;
            }
            catch (Exception)
            {
                // Editions of a stored book load lazily; without them only the title is compared.
                editions = null;
            }

            foreach (var edition in editions ?? new List<Edition>())
            {
                Add(edition?.Isbn13);
            }

            return result;
        }

        private static string NormalizeWorkId(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            try
            {
                return ProviderIdHelper.Canonicalize(id.Trim(), "ol");
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
