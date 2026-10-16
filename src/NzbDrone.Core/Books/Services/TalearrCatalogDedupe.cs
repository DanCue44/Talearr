using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Books.Services
{
    /// <summary>
    /// TaleArr: folds Audible-only catalog pockets into the real work.
    ///
    /// The metadata server returns some Audible products as their own "book" with only an
    /// az: identity (no Hardcover/Goodreads/OpenLibrary work id). Upstream mirrors those pockets
    /// as separate books, so a library ends up with "Animal Farm" (hc:356467) next to
    /// "Animal Farm" (az:B002ZVK8HM), and imports can attach to the duplicate.
    ///
    /// This runs on the remote author snapshot during add and refresh, before edition retention.
    /// An Audible-only pocket is folded into a book of the same media type whose title is the same
    /// work title, and its editions are added to that book. A pocket is kept as is when there is no
    /// such book, or when more than one book could be the target ("True Crime Stories" when the
    /// author has several "True Crime Stories: ..." books). Local rows of a folded pocket are then
    /// pruned by the normal refresh path, which already keeps rows that have files or were added
    /// manually. Nothing is written to the database here and no setting is involved.
    /// </summary>
    public static class TalearrCatalogDedupe
    {
        public static List<Book> CollapseAudibleOnlyPockets(List<Book> books, Logger logger = null, Author local = null, string logContext = null)
        {
            if (books == null || books.Count < 2)
            {
                return books ?? new List<Book>();
            }

            var result = new List<Book>(books.Count);
            var folded = 0;

            var canonicalCandidates = books
                .Where(book => book != null && !IsAudibleOnly(book) && BookIdentity.GetStableWorkProviderIdentityTokens(book).Count > 0)
                .ToList();

            foreach (var book in books)
            {
                if (book == null)
                {
                    continue;
                }

                if (!IsAudibleOnly(book))
                {
                    result.Add(book);
                    continue;
                }

                var target = FindTarget(book, canonicalCandidates);
                if (target == null)
                {
                    result.Add(book);
                    continue;
                }

                var added = MergeEditions(book, target);
                folded++;

                logger?.Info("[TALEARR-DEDUP] {0}: folded Audible-only book '{1}' ({2}) into '{3}' ({4}) for author '{5}', {6} edition(s) moved",
                    logContext ?? "refresh",
                    book.Title,
                    book.BaseBookId ?? "no id",
                    target.Title,
                    string.Join(",", BookIdentity.GetStableWorkProviderIdentityTokens(target).OrderBy(t => t, StringComparer.Ordinal)),
                    local?.Name ?? "Unknown",
                    added);
            }

            if (folded > 0)
            {
                logger?.Info("[TALEARR-DEDUP] {0}: folded {1} Audible-only duplicate book(s) for author '{2}'",
                    logContext ?? "refresh",
                    folded,
                    local?.Name ?? "Unknown");
            }

            return result;
        }

        public static bool IsAudibleOnly(Book book)
        {
            if (book == null || BookIdentity.GetStableWorkProviderIdentityTokens(book).Count > 0)
            {
                return false;
            }

            if (IsAudibleId(book.BaseBookId))
            {
                return true;
            }

            var editions = (book.Editions ?? new List<Edition>()).Where(e => e != null).ToList();
            return editions.Count > 0 && editions.All(e => IsAudibleId(e.ForeignEditionId));
        }

        internal static Book FindTarget(Book audibleBook, IReadOnlyCollection<Book> candidates)
        {
            var title = audibleBook?.Title;
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var sameMediaType = (candidates ?? Array.Empty<Book>())
                .Where(c => c != null && c.MediaType == audibleBook.MediaType)
                .ToList();

            var fullTitle = TalearrTitleNormalizer.NormalizeFull(title);
            var exact = sameMediaType
                .Where(c => string.Equals(TalearrTitleNormalizer.NormalizeFull(c.Title), fullTitle, StringComparison.Ordinal))
                .ToList();

            if (exact.Count == 1)
            {
                return exact[0];
            }

            if (exact.Count > 1)
            {
                return SingleWork(exact);
            }

            var sameWork = sameMediaType
                .Where(c => TalearrTitleNormalizer.IsSameWorkTitle(c.Title, title))
                .ToList();

            return sameWork.Count == 1 ? sameWork[0] : SingleWork(sameWork);
        }

        private static Book SingleWork(List<Book> candidates)
        {
            // Several pockets of the same work (shared work id) still identify one target.
            if (candidates.Count < 2)
            {
                return null;
            }

            var first = candidates[0];
            return candidates.Skip(1).All(c => BookIdentity.MatchesByStableWorkProviderId(first, c)) ? first : null;
        }

        private static int MergeEditions(Book source, Book target)
        {
            target.Editions ??= new List<Edition>();

            var existing = new HashSet<string>(target.Editions
                .Where(e => e?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
                .Select(e => e.ForeignEditionId.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var edition in (source.Editions ?? new List<Edition>()).Where(e => e != null))
            {
                if (edition.ForeignEditionId.IsNotNullOrWhiteSpace() && !existing.Add(edition.ForeignEditionId.Trim()))
                {
                    continue;
                }

                edition.Monitored = false;
                edition.ManualAdd = false;
                target.Editions.Add(edition);
                added++;
            }

            return added;
        }

        private static bool IsAudibleId(string providerId)
        {
            return providerId.IsNotNullOrWhiteSpace() &&
                   providerId.Trim().StartsWith("az:", StringComparison.OrdinalIgnoreCase);
        }
    }
}
