using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Titles
{
    // The title the user set for one work in Edit book.
    public sealed class TalearrTitleOverride
    {
        // TalearrBookGrabPolicyService.WorkKey: the audiobook and the ebook share it.
        public string WorkKey { get; set; }
        public string Title { get; set; }
        public int? BookIdHint { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public interface ITalearrTitleOverrideStore
    {
        // Every override by work key. Empty when talearr.db is unusable.
        Dictionary<string, TalearrTitleOverride> GetAll();

        bool Upsert(TalearrTitleOverride titleOverride);
        bool Remove(string workKey);
    }

    // Talearr: the TitleOverrides table in talearr.db. Best effort like the other talearr.db
    // stores: a failure is logged and reads as "no override", so the metadata title shows.
    public class TalearrTitleOverrideStore : ITalearrTitleOverrideStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrTitleOverrideStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public Dictionary<string, TalearrTitleOverride> GetAll()
        {
            return Run("read title overrides", new Dictionary<string, TalearrTitleOverride>(StringComparer.OrdinalIgnoreCase), connection =>
                connection.Query<Row>("SELECT * FROM TitleOverrides")
                    .Where(r => !string.IsNullOrWhiteSpace(r.WorkKey) && !string.IsNullOrWhiteSpace(r.Title))
                    .Select(r => new TalearrTitleOverride
                    {
                        WorkKey = r.WorkKey,
                        Title = r.Title,
                        BookIdHint = r.BookIdHint.HasValue ? (int)r.BookIdHint.Value : null,
                        UpdatedAt = TalearrRecordingsStore.ParseDate(r.UpdatedAt)
                    })
                    .GroupBy(o => o.WorkKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
        }

        public bool Upsert(TalearrTitleOverride titleOverride)
        {
            if (titleOverride == null || string.IsNullOrWhiteSpace(titleOverride.WorkKey) || string.IsNullOrWhiteSpace(titleOverride.Title))
            {
                return false;
            }

            return Run("write a title override", false, connection => connection.Execute(@"
INSERT INTO TitleOverrides (WorkKey, Title, BookIdHint, UpdatedAt)
VALUES (@WorkKey, @Title, @BookIdHint, @UpdatedAt)
ON CONFLICT (WorkKey) DO UPDATE SET
    Title = excluded.Title, BookIdHint = excluded.BookIdHint, UpdatedAt = excluded.UpdatedAt;", new
            {
                titleOverride.WorkKey,
                titleOverride.Title,
                titleOverride.BookIdHint,
                UpdatedAt = TalearrRecordingsStore.FormatDate(titleOverride.UpdatedAt == default ? DateTime.UtcNow : titleOverride.UpdatedAt)
            }) > 0);
        }

        public bool Remove(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return false;
            }

            return Run("remove a title override", false, connection =>
            {
                connection.Execute("DELETE FROM TitleOverrides WHERE WorkKey = @WorkKey", new { WorkKey = workKey });
                return true;
            });
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
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public long? BookIdHint { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
