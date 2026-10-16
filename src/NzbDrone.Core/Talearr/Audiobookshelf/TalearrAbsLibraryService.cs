using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // An Audiobookshelf item that satisfies a wanted book row, so the row need not be grabbed.
    public sealed class TalearrAbsOwnedCopy
    {
        public TalearrAbsItem Item { get; set; }
        public TalearrAbsConnection Connection { get; set; }
        public AbridgedMarker Abridged { get; set; }
        public string Url { get; set; }
    }

    // An item matched to a book row, for the book page.
    public sealed class TalearrAbsBookItem
    {
        public TalearrAbsItem Item { get; set; }
        public TalearrAbsItemBook Match { get; set; }
        public TalearrAbsConnection Connection { get; set; }
        public AbridgedMarker Abridged { get; set; }
        public string Url { get; set; }
    }

    public interface ITalearrAbsLibraryService
    {
        // Items matched to this book row, for display. Empty when nothing is cached.
        List<TalearrAbsBookItem> GetItems(int bookId);

        // The Audiobookshelf copy that makes grabbing this row unnecessary, or null. Null when the
        // row has TaleArr files of its own (upgrades stay TaleArr's business), when the copy is
        // TaleArr's own import, when "Skip books already in Audiobookshelf" is off, and for an
        // audiobook when the copy is another recording (abridged against a wanted unabridged one,
        // or the narrator of another recording row).
        TalearrAbsOwnedCopy FindOwnedCopy(Book book);

        List<TalearrAbsSyncState> GetStates();

        // Drops the in-memory index so the next call reads talearr.db again.
        void Invalidate();
    }

    // TaleArr: read side of the Audiobookshelf cache. Keeps an in-memory index of talearr.db
    // (rebuilt after a sync and at most every two minutes), because the decision engine asks for
    // every release of every search.
    public class TalearrAbsLibraryService : ITalearrAbsLibraryService
    {
        private const int MinimumComparableSeconds = 30 * 60;
        private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan OwnedMemoLifetime = TimeSpan.FromMinutes(1);

        private readonly ITalearrAbsStore _store;
        private readonly ITalearrAbsConnections _connections;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IEditionService _editionService;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private readonly ConcurrentDictionary<int, (DateTime At, TalearrAbsOwnedCopy Value)> _ownedMemo = new ConcurrentDictionary<int, (DateTime, TalearrAbsOwnedCopy)>();

        private Index _index;

        public TalearrAbsLibraryService(ITalearrAbsStore store,
                                        ITalearrAbsConnections connections,
                                        ITalearrRecordingGroupService groupService,
                                        IMediaFileService mediaFileService,
                                        IEditionService editionService,
                                        Logger logger,
                                        ITalearrAbridgedResolver abridgedResolver = null,
                                        ITalearrGrabPolicyService grabPolicyService = null,
                                        ITalearrBookGrabPolicyService bookGrabPolicyService = null)
        {
            _bookGrabPolicyService = bookGrabPolicyService;
            _store = store;
            _connections = connections;
            _groupService = groupService;
            _mediaFileService = mediaFileService;
            _editionService = editionService;
            _abridgedResolver = abridgedResolver;
            _grabPolicyService = grabPolicyService;
            _logger = logger;
        }

        public void Invalidate()
        {
            lock (_lock)
            {
                _index = null;
            }

            _ownedMemo.Clear();
        }

        public List<TalearrAbsSyncState> GetStates()
        {
            return _store.GetStates();
        }

        public List<TalearrAbsBookItem> GetItems(int bookId)
        {
            var index = GetIndex();
            if (!index.ByBook.TryGetValue(bookId, out var entries))
            {
                return new List<TalearrAbsBookItem>();
            }

            return entries
                .Where(e => index.Connections.ContainsKey(e.Match.ConnectionId))
                .Select(e =>
                {
                    var connection = index.Connections[e.Match.ConnectionId];
                    return new TalearrAbsBookItem
                    {
                        Item = e.Item,
                        Match = e.Match,
                        Connection = connection,
                        Abridged = e.Item.Abridged,
                        Url = connection.ItemUrl(e.Item.ItemId)
                    };
                })
                .OrderByDescending(i => i.Match.Managed)
                .ThenBy(i => i.Item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public TalearrAbsOwnedCopy FindOwnedCopy(Book book)
        {
            if (book == null || book.Id <= 0)
            {
                return null;
            }

            // A search judges many releases for the same rows; answer each row once a minute.
            if (_ownedMemo.TryGetValue(book.Id, out var memo) && DateTime.UtcNow - memo.At < OwnedMemoLifetime)
            {
                return memo.Value;
            }

            var owned = FindOwnedCopyCore(book);
            _ownedMemo[book.Id] = (DateTime.UtcNow, owned);
            return owned;
        }

        private TalearrAbsOwnedCopy FindOwnedCopyCore(Book book)
        {
            var index = GetIndex();
            if (!index.ByBook.TryGetValue(book.Id, out var entries))
            {
                return null;
            }

            var candidates = entries
                .Where(e => !e.Match.Managed)
                .Where(e => index.Connections.TryGetValue(e.Match.ConnectionId, out var c) && c.SkipOwned)
                .Where(e => book.MediaType == BookMediaType.Ebook ? e.Item.HasEbook : e.Item.HasAudio)
                .ToList();

            if (candidates.Count == 0 || HasTalearrFiles(book))
            {
                return null;
            }

            if (book.MediaType == BookMediaType.Ebook)
            {
                return ToOwned(candidates[0], index, AbridgedMarker.Unknown);
            }

            var rows = GetRecordingRows(book);
            foreach (var entry in candidates)
            {
                var assigned = AssignToRecording(entry.Item, rows, out var marker);
                if (assigned != null && assigned.Id == book.Id)
                {
                    return ToOwned(entry, index, marker);
                }
            }

            return null;
        }

        // Which recording row of the work an Audiobookshelf audiobook satisfies, or null when it
        // satisfies none (for example an abridged copy when every row wants the unabridged one).
        public Book AssignToRecording(TalearrAbsItem item, IReadOnlyList<RecordingRow> rows, out AbridgedMarker itemMarker)
        {
            itemMarker = item?.Abridged ?? AbridgedMarker.Unknown;
            if (item == null || rows == null || rows.Count == 0)
            {
                return null;
            }

            var globalPolicy = TalearrGrabPolicyService.Resolve(_grabPolicyService).AbridgedPolicy;
            var itemNarrators = NarratorKeys(item.Narrators);

            var compatible = new List<(RecordingRow Row, NarratorRelation Narrator)>();
            foreach (var row in rows)
            {
                var marker = JudgeItemAbridgement(item, row.Editions);
                if (row == rows[0])
                {
                    itemMarker = marker;
                }

                var wanted = WantedAbridgement(row, globalPolicy);
                if (wanted.HasValue && !AbridgementFits(marker, wanted.Value))
                {
                    continue;
                }

                compatible.Add((row, Relate(itemNarrators, row.Narrators)));
            }

            if (compatible.Count == 0)
            {
                return null;
            }

            var chosen = compatible.FirstOrDefault(c => c.Narrator == NarratorRelation.Same).Row ??
                         compatible.Where(c => c.Narrator == NarratorRelation.Unknown).OrderByDescending(c => c.Row.IsPrimary).FirstOrDefault().Row;

            // One recording only: any reading of the book counts, whoever narrates it.
            if (chosen == null && rows.Count == 1)
            {
                chosen = compatible[0].Row;
            }

            if (chosen != null)
            {
                itemMarker = JudgeItemAbridgement(item, chosen.Editions);
            }

            return chosen?.Book;
        }

        // The item's own marker, else its runtime against the row's longest comparable edition.
        public static AbridgedMarker JudgeItemAbridgement(TalearrAbsItem item, IReadOnlyCollection<Edition> editions)
        {
            if (item.Abridged != AbridgedMarker.Unknown)
            {
                return item.Abridged;
            }

            var duration = item.DurationSeconds ?? 0;
            if (duration < MinimumComparableSeconds)
            {
                return AbridgedMarker.Unknown;
            }

            var reference = (editions ?? Array.Empty<Edition>())
                .Where(e => e != null && (e.DurationSeconds ?? 0) >= MinimumComparableSeconds)
                .Where(e => !AbridgedReleaseDetector.IsAbridgedByMetadata(e))
                .Select(e => (double)e.DurationSeconds.Value)
                .DefaultIfEmpty(0)
                .Max();

            if (reference <= 0)
            {
                return AbridgedMarker.Unknown;
            }

            var ratio = duration / reference;
            if (ratio < AbridgedReleaseDetector.AbridgedRuntimeRatio)
            {
                return AbridgedMarker.Abridged;
            }

            return ratio >= TalearrAbridgedResolver.UnabridgedRuntimeRatio ? AbridgedMarker.Unabridged : AbridgedMarker.Unknown;
        }

        // An unmarked copy counts as unabridged, like an unmarked release under the grab policy.
        public static bool AbridgementFits(AbridgedMarker item, bool wantsAbridged)
        {
            return item switch
            {
                AbridgedMarker.Abridged => wantsAbridged,
                AbridgedMarker.Unabridged => !wantsAbridged,
                _ => !wantsAbridged
            };
        }

        public enum NarratorRelation
        {
            Unknown,
            Same,
            Different
        }

        public static NarratorRelation Relate(ISet<string> itemNarrators, ISet<string> rowNarrators)
        {
            if (itemNarrators == null || rowNarrators == null || itemNarrators.Count == 0 || rowNarrators.Count == 0)
            {
                return NarratorRelation.Unknown;
            }

            return itemNarrators.Overlaps(rowNarrators) ? NarratorRelation.Same : NarratorRelation.Different;
        }

        public static HashSet<string> NarratorKeys(string narrators)
        {
            return new HashSet<string>(
                TalearrAbsMatcher.SplitAuthors(narrators)
                    .Select(TalearrAbsMatcher.NormalizeName)
                    .Where(n => n.Length > 0),
                StringComparer.Ordinal);
        }

        public sealed class RecordingRow
        {
            public Book Book { get; set; }
            public int Id => Book.Id;
            public bool IsPrimary { get; set; }
            public bool WantsAbridged { get; set; }
            public HashSet<string> Narrators { get; set; } = new HashSet<string>(StringComparer.Ordinal);
            public List<Edition> Editions { get; set; } = new List<Edition>();
        }

        private List<RecordingRow> GetRecordingRows(Book book)
        {
            RecordingGroup group = null;
            try
            {
                group = _groupService.GetGroup(book);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not read the recordings of book {0}", book.Id);
            }

            var books = group?.Books?.Where(b => b != null).ToList() ?? new List<Book>();
            if (!books.Any(b => b.Id == book.Id))
            {
                books.Add(book);
            }

            var primaryId = group?.Primary?.Id ?? book.Id;

            return books
                .OrderByDescending(b => b.Id == primaryId)
                .ThenBy(b => b.Id)
                .Select(b => BuildRow(b, b.Id == primaryId))
                .ToList();
        }

        private RecordingRow BuildRow(Book book, bool isPrimary)
        {
            List<Edition> editions;
            try
            {
                editions = _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>();
            }
            catch (Exception)
            {
                editions = new List<Edition>();
            }

            var narrators = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edition in editions.Where(e => e.Monitored))
            {
                foreach (var name in (edition.NarratorNames ?? new List<string>()).Append(edition.Narrator))
                {
                    narrators.UnionWith(NarratorKeys(name));
                }
            }

            if (narrators.Count == 0)
            {
                narrators.UnionWith(NarratorKeys(book.NarratorName));
            }

            return new RecordingRow
            {
                Book = book,
                IsPrimary = isPrimary,
                WantsAbridged = WantsAbridged(book),
                Narrators = narrators,
                Editions = editions
            };
        }

        // The reading a row wants under its effective abridged policy (per book, else global);
        // null when any reading will do.
        private bool? WantedAbridgement(RecordingRow row, AbridgedAudiobookPolicy globalPolicy)
        {
            var target = TalearrAbridgedRules.Target(row.Book, globalPolicy, _bookGrabPolicyService, _abridgedResolver);
            switch (target.Policy)
            {
                case AbridgedAudiobookPolicy.Any:
                    return null;
                case AbridgedAudiobookPolicy.AbridgedOnly:
                    return true;
                case AbridgedAudiobookPolicy.UnabridgedOnly when target.IsStrict:
                    return false;
                default:
                    return row.WantsAbridged;
            }
        }

        private bool WantsAbridged(Book book)
        {
            try
            {
                return AbridgedReleaseDetector.IsAbridgedBook(book, _abridgedResolver);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool HasTalearrFiles(Book book)
        {
            try
            {
                return (_mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>()).Count > 0;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not read the files of book {0}", book.Id);
                return true;
            }
        }

        private static TalearrAbsOwnedCopy ToOwned(Entry entry, Index index, AbridgedMarker marker)
        {
            var connection = index.Connections[entry.Match.ConnectionId];
            return new TalearrAbsOwnedCopy
            {
                Item = entry.Item,
                Connection = connection,
                Abridged = marker,
                Url = connection.ItemUrl(entry.Item.ItemId)
            };
        }

        private Index GetIndex()
        {
            lock (_lock)
            {
                if (_index != null && DateTime.UtcNow - _index.BuiltAt < IndexLifetime)
                {
                    return _index;
                }

                _index = BuildIndex();
                return _index;
            }
        }

        private Index BuildIndex()
        {
            var index = new Index { BuiltAt = DateTime.UtcNow };

            try
            {
                index.Connections = _connections.GetConnections()
                    .Where(c => c.SyncEnabled)
                    .ToDictionary(c => c.Id);

                if (index.Connections.Count == 0)
                {
                    return index;
                }

                var items = _store.GetItems()
                    .GroupBy(i => (i.ConnectionId, i.ItemId))
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var match in _store.GetMatches())
                {
                    if (!items.TryGetValue((match.ConnectionId, match.ItemId), out var item))
                    {
                        continue;
                    }

                    if (!index.ByBook.TryGetValue(match.BookId, out var list))
                    {
                        list = new List<Entry>();
                        index.ByBook[match.BookId] = list;
                    }

                    list.Add(new Entry { Item = item, Match = match });
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: could not load the Audiobookshelf library cache");
            }

            return index;
        }

        private sealed class Entry
        {
            public TalearrAbsItem Item { get; set; }
            public TalearrAbsItemBook Match { get; set; }
        }

        private sealed class Index
        {
            public DateTime BuiltAt { get; set; }
            public Dictionary<int, TalearrAbsConnection> Connections { get; set; } = new Dictionary<int, TalearrAbsConnection>();
            public Dictionary<int, List<Entry>> ByBook { get; } = new Dictionary<int, List<Entry>>();
        }
    }
}
