using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Catalog
{
    // What the rules know about one book row.
    public sealed class TalearrBookRowFacts
    {
        public Book Book { get; set; }
        public bool HasFiles { get; set; }
        public List<Edition> Editions { get; set; } = new List<Edition>();

        public bool IsMonitored => Book != null && (Book.Monitored || Book.AudiobookMonitored || Book.EbookMonitored);

        public bool IsManual => Book?.AddOptions?.AddType == BookAddType.Manual;

        // A deliberate local instance: a recording copy (UnitKeyHash, pinned edition, manual edition)
        // or a row the user added. Same rules upstream refresh uses before pruning a merged row.
        public bool IsDeliberateInstance =>
            Book == null ||
            IsManual ||
            !string.IsNullOrWhiteSpace(Book.UnitKeyHash) ||
            !Book.AnyEditionOk ||
            Editions.Any(e => e?.ManualAdd == true);
    }

    public sealed class TalearrDuplicateGroup
    {
        public int AuthorId { get; set; }
        public BookMediaType MediaType { get; set; }
        public string Title { get; set; }
        public string WorkIds { get; set; }
        public TalearrBookRowFacts Keep { get; set; }
        public List<TalearrBookRowFacts> Remove { get; set; } = new List<TalearrBookRowFacts>();

        // Other rows of the same work that stay (files, monitored, manual or recording copies).
        public List<TalearrBookRowFacts> Kept { get; set; } = new List<TalearrBookRowFacts>();
    }

    // TaleArr: rules for "the same work stored twice for one media type". Two rows are the same
    // stored work only when they belong to the same author, have the same media type, the same
    // stable work ids (hc:/gr:/ol:, all of them, not just one in common) and the same normalized
    // title. Distinct works never match: a bundle has its own work id and title, and two works
    // that merely share one provider id are left alone.
    public static class TalearrDuplicateBooks
    {
        public static string GetWorkKey(Book book)
        {
            if (book == null)
            {
                return null;
            }

            var tokens = BookIdentity.GetStableWorkProviderIdentityTokens(book);
            if (tokens.Count == 0)
            {
                return null;
            }

            var title = TalearrTitleNormalizer.NormalizeFull(book.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            return string.Join("|",
                book.AuthorId.ToString(),
                book.MediaType.ToString(),
                string.Join(",", tokens.Select(t => t.ToLowerInvariant()).OrderBy(t => t, StringComparer.Ordinal)),
                title);
        }

        public static bool AreSameStoredWork(Book left, Book right)
        {
            var leftKey = GetWorkKey(left);
            return leftKey != null && string.Equals(leftKey, GetWorkKey(right), StringComparison.Ordinal);
        }

        // Rows with files first, then monitored, then manual or deliberate copies, then the oldest.
        public static List<TalearrBookRowFacts> OrderForKeeping(IEnumerable<TalearrBookRowFacts> rows)
        {
            return rows
                .Where(r => r?.Book != null)
                .OrderByDescending(r => r.HasFiles)
                .ThenByDescending(r => r.IsMonitored)
                .ThenByDescending(r => r.IsManual || r.IsDeliberateInstance)
                .ThenBy(r => r.Book.Id)
                .ToList();
        }

        // A row may be removed only when it is an unmonitored, file-less, automatic, plain copy and
        // another row of the same stored work is kept.
        public static bool IsRemovable(TalearrBookRowFacts row)
        {
            return row?.Book != null &&
                   row.Book.Id > 0 &&
                   !row.HasFiles &&
                   !row.IsMonitored &&
                   !row.IsDeliberateInstance;
        }

        // Several local rows answer one requested work. When they are all the same stored work
        // (a duplicate pass, not distinct works), use one of them instead of failing the add:
        // the monitored row, else the oldest. Returns null when the rows are distinct works.
        public static Book PickRequestedBook(IReadOnlyList<Book> matches)
        {
            if (matches == null || matches.Count == 0)
            {
                return null;
            }

            var first = matches[0];
            if (matches.Any(m => !AreSameStoredWork(first, m)))
            {
                return null;
            }

            return matches
                .OrderByDescending(m => m.Monitored || m.AudiobookMonitored || m.EbookMonitored)
                .ThenBy(m => m.Id)
                .First();
        }

        public static List<TalearrDuplicateGroup> FindGroups(IEnumerable<TalearrBookRowFacts> rows)
        {
            var groups = new List<TalearrDuplicateGroup>();

            foreach (var group in (rows ?? Enumerable.Empty<TalearrBookRowFacts>())
                         .Where(r => r?.Book != null && r.Book.Id > 0)
                         .Select(r => (Key: GetWorkKey(r.Book), Row: r))
                         .Where(x => x.Key != null)
                         .GroupBy(x => x.Key, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                var ordered = OrderForKeeping(group.Select(x => x.Row));

                // Deliberate instances (recording copies, manual adds) always stay, and so does at
                // least one plain row: the primary a recording copy belongs to is never removed.
                var plain = ordered.Where(r => !r.IsDeliberateInstance).ToList();
                var plainKeep = plain.FirstOrDefault();
                var keep = ordered.First();

                var result = new TalearrDuplicateGroup
                {
                    AuthorId = keep.Book.AuthorId,
                    MediaType = keep.Book.MediaType,
                    Title = keep.Book.Title,
                    WorkIds = string.Join(", ", BookIdentity.GetStableWorkProviderIdentityTokens(keep.Book).OrderBy(t => t, StringComparer.OrdinalIgnoreCase)),
                    Keep = keep
                };

                foreach (var row in ordered.Where(r => !ReferenceEquals(r, keep)))
                {
                    if (!ReferenceEquals(row, plainKeep) && IsRemovable(row))
                    {
                        result.Remove.Add(row);
                    }
                    else
                    {
                        result.Kept.Add(row);
                    }
                }

                groups.Add(result);
            }

            return groups.OrderBy(g => g.AuthorId).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.MediaType).ToList();
        }
    }
}
