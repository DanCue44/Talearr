using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    public interface ITalearrAbsStore
    {
        // Replaces everything cached for the connection in one transaction.
        void ReplaceConnection(TalearrAbsSyncResult result, TalearrAbsSyncState state);

        // Records a failed sync without touching the cached items.
        void SaveState(TalearrAbsSyncState state);

        void RemoveConnectionsExcept(IEnumerable<int> connectionIds);

        List<TalearrAbsItem> GetItems();
        List<TalearrAbsItemBook> GetMatches();
        List<TalearrAbsSyncState> GetStates();
    }

    // Talearr: Audiobookshelf library cache in talearr.db. Like the recordings store, every call is
    // best effort: a failure is logged and read as "nothing cached", so a broken talearr.db never
    // blocks a search. A corrupt file is recreated and refilled by the next sync.
    public class TalearrAbsStore : ITalearrAbsStore
    {
        private const int ChunkSize = 200;

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrAbsStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public void ReplaceConnection(TalearrAbsSyncResult result, TalearrAbsSyncState state)
        {
            if (result == null)
            {
                return;
            }

            var connectionId = result.ConnectionId;
            var items = (result.Items ?? new List<TalearrAbsItem>())
                .Where(i => i != null && !string.IsNullOrWhiteSpace(i.ItemId))
                .GroupBy(i => i.ItemId, StringComparer.Ordinal)
                .Select(g => ToRow(connectionId, g.First()))
                .ToList();

            var itemIds = new HashSet<string>(items.Select(i => i.ItemId), StringComparer.Ordinal);
            var matches = (result.Matches ?? new List<TalearrAbsItemBook>())
                .Where(m => m != null && m.BookId > 0 && itemIds.Contains(m.ItemId))
                .GroupBy(m => (m.ItemId, m.BookId))
                .Select(g => g.OrderByDescending(m => m.Managed).First())
                .Select(m => new MatchRow
                {
                    ConnectionId = connectionId,
                    ItemId = m.ItemId,
                    BookId = m.BookId,
                    Method = m.Method ?? TalearrAbsMatchMethods.Title,
                    Managed = m.Managed ? 1 : 0
                })
                .ToList();

            Run("write the Audiobookshelf library", 0, connection =>
            {
                using var transaction = connection.BeginTransaction();
                connection.Execute("DELETE FROM AbsItemBooks WHERE ConnectionId = @ConnectionId", new { ConnectionId = connectionId }, transaction);
                connection.Execute("DELETE FROM AbsItems WHERE ConnectionId = @ConnectionId", new { ConnectionId = connectionId }, transaction);

                foreach (var chunk in items.Chunk(ChunkSize))
                {
                    connection.Execute(@"
INSERT INTO AbsItems (ConnectionId, ItemId, LibraryId, Path, LocalPath, Title, Subtitle, Authors, Narrators, Asin, Isbn, DurationSeconds, Abridged, HasAudio, HasEbook, EbookFormat, UpdatedAt, SyncedAt)
VALUES (@ConnectionId, @ItemId, @LibraryId, @Path, @LocalPath, @Title, @Subtitle, @Authors, @Narrators, @Asin, @Isbn, @DurationSeconds, @Abridged, @HasAudio, @HasEbook, @EbookFormat, @UpdatedAt, @SyncedAt);", chunk, transaction);
                }

                foreach (var chunk in matches.Chunk(ChunkSize))
                {
                    connection.Execute(@"
INSERT INTO AbsItemBooks (ConnectionId, ItemId, BookId, Method, Managed)
VALUES (@ConnectionId, @ItemId, @BookId, @Method, @Managed);", chunk, transaction);
                }

                UpsertState(connection, transaction, state ?? new TalearrAbsSyncState { ConnectionId = connectionId });
                transaction.Commit();
                return items.Count;
            });
        }

        public void SaveState(TalearrAbsSyncState state)
        {
            if (state == null)
            {
                return;
            }

            Run("write the Audiobookshelf sync state", 0, connection =>
            {
                using var transaction = connection.BeginTransaction();
                UpsertState(connection, transaction, state);
                transaction.Commit();
                return 1;
            });
        }

        public void RemoveConnectionsExcept(IEnumerable<int> connectionIds)
        {
            var keep = (connectionIds ?? Enumerable.Empty<int>()).Distinct().ToList();

            Run("prune Audiobookshelf connections", 0, connection =>
            {
                using var transaction = connection.BeginTransaction();
                var removed = 0;

                if (keep.Count == 0)
                {
                    removed += connection.Execute("DELETE FROM AbsItemBooks", transaction: transaction);
                    removed += connection.Execute("DELETE FROM AbsItems", transaction: transaction);
                    removed += connection.Execute("DELETE FROM AbsSyncState", transaction: transaction);
                }
                else
                {
                    // Integers written inline: upstream registers a global Dapper handler that
                    // turns List<int> parameters into JSON, which would break "IN @list".
                    var list = string.Join(",", keep.Select(id => id.ToString(CultureInfo.InvariantCulture)));
                    removed += connection.Execute($"DELETE FROM AbsItemBooks WHERE ConnectionId NOT IN ({list})", transaction: transaction);
                    removed += connection.Execute($"DELETE FROM AbsItems WHERE ConnectionId NOT IN ({list})", transaction: transaction);
                    removed += connection.Execute($"DELETE FROM AbsSyncState WHERE ConnectionId NOT IN ({list})", transaction: transaction);
                }

                transaction.Commit();
                return removed;
            });
        }

        public List<TalearrAbsItem> GetItems()
        {
            return Run("read the Audiobookshelf library", new List<TalearrAbsItem>(), connection => connection
                .Query<ItemRow>("SELECT * FROM AbsItems")
                .Select(ToModel)
                .ToList());
        }

        public List<TalearrAbsItemBook> GetMatches()
        {
            return Run("read Audiobookshelf matches", new List<TalearrAbsItemBook>(), connection => connection
                .Query<MatchRow>("SELECT * FROM AbsItemBooks")
                .Select(row => new TalearrAbsItemBook
                {
                    ConnectionId = (int)row.ConnectionId,
                    ItemId = row.ItemId,
                    BookId = (int)row.BookId,
                    Method = row.Method,
                    Managed = row.Managed != 0
                })
                .ToList());
        }

        public List<TalearrAbsSyncState> GetStates()
        {
            return Run("read Audiobookshelf sync state", new List<TalearrAbsSyncState>(), connection => connection
                .Query<StateRow>("SELECT * FROM AbsSyncState")
                .Select(row => new TalearrAbsSyncState
                {
                    ConnectionId = (int)row.ConnectionId,
                    LastSyncAt = string.IsNullOrWhiteSpace(row.LastSyncAt) ? null : TalearrRecordingsStore.ParseDate(row.LastSyncAt),
                    LastError = row.LastError,
                    ItemCount = (int)row.ItemCount,
                    MatchedCount = (int)row.MatchedCount
                })
                .ToList());
        }

        private static void UpsertState(IDbConnection connection, IDbTransaction transaction, TalearrAbsSyncState state)
        {
            connection.Execute(@"
INSERT INTO AbsSyncState (ConnectionId, LastSyncAt, LastError, ItemCount, MatchedCount)
VALUES (@ConnectionId, @LastSyncAt, @LastError, @ItemCount, @MatchedCount)
ON CONFLICT (ConnectionId) DO UPDATE SET
    LastSyncAt = excluded.LastSyncAt, LastError = excluded.LastError,
    ItemCount = excluded.ItemCount, MatchedCount = excluded.MatchedCount;", new
            {
                state.ConnectionId,
                LastSyncAt = state.LastSyncAt.HasValue ? TalearrRecordingsStore.FormatDate(state.LastSyncAt.Value) : null,
                state.LastError,
                state.ItemCount,
                state.MatchedCount
            }, transaction);
        }

        private T Run<T>(string what, T fallback, Func<IDbConnection, T> action)
        {
            try
            {
                using var connection = _database.OpenConnection();
                return action(connection);
            }
            catch (Exception ex) when (_database.IsCorruption(ex))
            {
                _database.Reset($"corruption found during {what}");
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not {0} in {1}; continuing without it", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private static ItemRow ToRow(int connectionId, TalearrAbsItem item)
        {
            return new ItemRow
            {
                ConnectionId = connectionId,
                ItemId = item.ItemId,
                LibraryId = item.LibraryId ?? string.Empty,
                Path = item.Path,
                LocalPath = item.LocalPath,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Authors = item.Authors,
                Narrators = item.Narrators,
                Asin = item.Asin,
                Isbn = item.Isbn,
                DurationSeconds = item.DurationSeconds,
                Abridged = (int)item.Abridged,
                HasAudio = item.HasAudio ? 1 : 0,
                HasEbook = item.HasEbook ? 1 : 0,
                EbookFormat = item.EbookFormat,
                UpdatedAt = item.UpdatedAt.HasValue ? TalearrRecordingsStore.FormatDate(item.UpdatedAt.Value) : null,
                SyncedAt = TalearrRecordingsStore.FormatDate(item.SyncedAt == default ? DateTime.UtcNow : item.SyncedAt)
            };
        }

        private static TalearrAbsItem ToModel(ItemRow row)
        {
            return new TalearrAbsItem
            {
                ConnectionId = (int)row.ConnectionId,
                ItemId = row.ItemId,
                LibraryId = row.LibraryId,
                Path = row.Path,
                LocalPath = row.LocalPath,
                Title = row.Title,
                Subtitle = row.Subtitle,
                Authors = row.Authors,
                Narrators = row.Narrators,
                Asin = row.Asin,
                Isbn = row.Isbn,
                DurationSeconds = row.DurationSeconds,
                Abridged = Enum.IsDefined(typeof(AbridgedMarker), (int)row.Abridged) ? (AbridgedMarker)row.Abridged : AbridgedMarker.Unknown,
                HasAudio = row.HasAudio != 0,
                HasEbook = row.HasEbook != 0,
                EbookFormat = row.EbookFormat,
                UpdatedAt = string.IsNullOrWhiteSpace(row.UpdatedAt) ? null : TalearrRecordingsStore.ParseDate(row.UpdatedAt),
                SyncedAt = TalearrRecordingsStore.ParseDate(row.SyncedAt)
            };
        }

        // SQLite native types (INTEGER is long, REAL is double) so Dapper maps them without help.
        private sealed class ItemRow
        {
            public long ConnectionId { get; set; }
            public string ItemId { get; set; }
            public string LibraryId { get; set; }
            public string Path { get; set; }
            public string LocalPath { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public string Authors { get; set; }
            public string Narrators { get; set; }
            public string Asin { get; set; }
            public string Isbn { get; set; }
            public double? DurationSeconds { get; set; }
            public long Abridged { get; set; }
            public long HasAudio { get; set; }
            public long HasEbook { get; set; }
            public string EbookFormat { get; set; }
            public string UpdatedAt { get; set; }
            public string SyncedAt { get; set; }
        }

        private sealed class MatchRow
        {
            public long ConnectionId { get; set; }
            public string ItemId { get; set; }
            public long BookId { get; set; }
            public string Method { get; set; }
            public long Managed { get; set; }
        }

        private sealed class StateRow
        {
            public long ConnectionId { get; set; }
            public string LastSyncAt { get; set; }
            public string LastError { get; set; }
            public long ItemCount { get; set; }
            public long MatchedCount { get; set; }
        }
    }
}
