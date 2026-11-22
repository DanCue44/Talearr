using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.EditionLocks
{
    // One locked book: the edition it keeps, by provider key and last known row id.
    public sealed class TalearrEditionLock
    {
        public int BookId { get; set; }
        public string EditionKey { get; set; }
        public int? EditionIdHint { get; set; }
        public DateTime LockedAt { get; set; }
    }

    public interface ITalearrEditionLockStore
    {
        // Every lock by book row id. Empty when talearr.db is unusable.
        Dictionary<int, TalearrEditionLock> GetAll();

        bool Upsert(TalearrEditionLock editionLock);
        void Remove(IEnumerable<int> bookIds);
    }

    // Talearr: the EditionLocks table in talearr.db. Best effort like the other talearr.db stores:
    // a failure is logged and reads as "not locked", so the book behaves as it does in Chaptarr.
    public class TalearrEditionLockStore : ITalearrEditionLockStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrEditionLockStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public Dictionary<int, TalearrEditionLock> GetAll()
        {
            return Run("read edition locks", new Dictionary<int, TalearrEditionLock>(), connection =>
                connection.Query<Row>("SELECT * FROM EditionLocks")
                    .Select(r => new TalearrEditionLock
                    {
                        BookId = (int)r.BookId,
                        EditionKey = r.EditionKey,
                        EditionIdHint = r.EditionIdHint.HasValue ? (int)r.EditionIdHint.Value : null,
                        LockedAt = TalearrRecordingsStore.ParseDate(r.LockedAt)
                    })
                    .GroupBy(l => l.BookId)
                    .ToDictionary(g => g.Key, g => g.First()));
        }

        public bool Upsert(TalearrEditionLock editionLock)
        {
            if (editionLock == null || editionLock.BookId <= 0)
            {
                return false;
            }

            return Run("write an edition lock", false, connection => connection.Execute(@"
INSERT INTO EditionLocks (BookId, EditionKey, EditionIdHint, LockedAt)
VALUES (@BookId, @EditionKey, @EditionIdHint, @LockedAt)
ON CONFLICT (BookId) DO UPDATE SET
    EditionKey = excluded.EditionKey, EditionIdHint = excluded.EditionIdHint, LockedAt = excluded.LockedAt;", new
            {
                editionLock.BookId,
                editionLock.EditionKey,
                editionLock.EditionIdHint,
                LockedAt = TalearrRecordingsStore.FormatDate(editionLock.LockedAt == default ? DateTime.UtcNow : editionLock.LockedAt)
            }) > 0);
        }

        public void Remove(IEnumerable<int> bookIds)
        {
            var ids = (bookIds ?? Enumerable.Empty<int>()).Where(id => id > 0).Distinct().ToList();
            if (!ids.Any())
            {
                return;
            }

            // Ids are inlined: Chaptarr registers Dapper type handlers for int lists (JSON), which
            // would turn an IN @list parameter into a single string.
            Run("remove edition locks", 0, connection => connection.Execute($"DELETE FROM EditionLocks WHERE BookId IN ({string.Join(", ", ids)})"));
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public long BookId { get; set; }
            public string EditionKey { get; set; }
            public long? EditionIdHint { get; set; }
            public string LockedAt { get; set; }
        }
    }
}
