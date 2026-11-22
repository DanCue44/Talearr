using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.PreferredSeries
{
    public interface ITalearrPreferredSeriesStore
    {
        // Every preference. Empty when talearr.db is unusable.
        List<TalearrPreferredSeriesEntry> GetAll();

        // False when the write did not reach talearr.db.
        bool Upsert(TalearrPreferredSeriesEntry entry);
        bool Remove(string workKey);
    }

    // Talearr: the PreferredSeries table in talearr.db. Best effort like the other talearr.db
    // stores: a failure is logged and reads as "no preference", so the provider's order is used.
    public class TalearrPreferredSeriesStore : ITalearrPreferredSeriesStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrPreferredSeriesStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public List<TalearrPreferredSeriesEntry> GetAll()
        {
            return Run("read preferred series", new List<TalearrPreferredSeriesEntry>(), connection =>
                connection.Query<Row>("SELECT * FROM PreferredSeries")
                    .Select(ToModel)
                    .Where(e => e != null)
                    .ToList());
        }

        public bool Upsert(TalearrPreferredSeriesEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.WorkKey) || string.IsNullOrWhiteSpace(entry.SeriesTitle))
            {
                return false;
            }

            return Run("write a preferred series", false, connection => connection.Execute(@"
INSERT INTO PreferredSeries (WorkKey, SeriesTitle, Provider, BookIdHint, UpdatedAt)
VALUES (@WorkKey, @SeriesTitle, @Provider, @BookIdHint, @UpdatedAt)
ON CONFLICT (WorkKey) DO UPDATE SET
    SeriesTitle = excluded.SeriesTitle, Provider = excluded.Provider,
    BookIdHint = excluded.BookIdHint, UpdatedAt = excluded.UpdatedAt;", new
            {
                WorkKey = entry.WorkKey.Trim(),
                SeriesTitle = entry.SeriesTitle.Trim(),
                Provider = JsonSerializer.Serialize(entry.Provider ?? new Dictionary<int, TalearrPreferredSeriesProvider>(), JsonOptions),
                entry.BookIdHint,
                UpdatedAt = TalearrRecordingsStore.FormatDate(entry.UpdatedAt == default ? DateTime.UtcNow : entry.UpdatedAt)
            }) > 0);
        }

        public bool Remove(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return false;
            }

            return Run("clear a preferred series", false, connection =>
            {
                connection.Execute("DELETE FROM PreferredSeries WHERE WorkKey = @WorkKey", new { WorkKey = workKey.Trim() });
                return true;
            });
        }

        private TalearrPreferredSeriesEntry ToModel(Row row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.WorkKey) || string.IsNullOrWhiteSpace(row.SeriesTitle))
            {
                return null;
            }

            Dictionary<int, TalearrPreferredSeriesProvider> provider = null;
            if (!string.IsNullOrWhiteSpace(row.Provider))
            {
                try
                {
                    provider = JsonSerializer.Deserialize<Dictionary<int, TalearrPreferredSeriesProvider>>(row.Provider, JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.Debug(ex, "Talearr: unreadable provider values for the preferred series of {0}", row.WorkKey);
                }
            }

            return new TalearrPreferredSeriesEntry
            {
                WorkKey = row.WorkKey,
                SeriesTitle = row.SeriesTitle,
                BookIdHint = row.BookIdHint.HasValue ? (int)row.BookIdHint.Value : null,
                Provider = provider ?? new Dictionary<int, TalearrPreferredSeriesProvider>(),
                UpdatedAt = TalearrRecordingsStore.ParseDate(row.UpdatedAt)
            };
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
            public string SeriesTitle { get; set; }
            public string Provider { get; set; }
            public long? BookIdHint { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
