using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.ImportListExclusions
{
    public interface ITalearrExclusionDateStore
    {
        // Records now as the added date of each new exclusion.
        void RecordAdded(IEnumerable<ImportListExclusion> exclusions);

        // Added date per exclusion row id. Exclusions added before Talearr kept dates (or while
        // stock Chaptarr ran) have none. Empty when talearr.db is unusable.
        Dictionary<int, DateTime> GetAddedDates(IEnumerable<ImportListExclusion> exclusions);
    }

    // Talearr: when each import list exclusion was added (Chaptarr #367, Readarr #3437). The
    // shared ImportListExclusions table has no date column, so the date lives in talearr.db,
    // keyed by the exclusion's provider id and media scope rather than its chaptarr.db row id.
    // Best effort: a failure only means the date shows blank.
    public class TalearrExclusionDateStore : ITalearrExclusionDateStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrExclusionDateStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public static string Key(ImportListExclusion exclusion)
        {
            if (exclusion == null || string.IsNullOrWhiteSpace(exclusion.ForeignId))
            {
                return null;
            }

            var scope = exclusion.MediaType.HasValue ? exclusion.MediaType.Value.ToString().ToLowerInvariant() : "all";
            return exclusion.ForeignId.Trim() + "|" + scope;
        }

        public void RecordAdded(IEnumerable<ImportListExclusion> exclusions)
        {
            var keys = (exclusions ?? Enumerable.Empty<ImportListExclusion>())
                .Select(Key)
                .Where(key => key != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keys.Count == 0)
            {
                return;
            }

            var now = TalearrRecordingsStore.FormatDate(DateTime.UtcNow);
            Run("record exclusion dates", 0, connection => connection.Execute(@"
INSERT INTO ImportListExclusionDates (ExclusionKey, AddedAt) VALUES (@ExclusionKey, @AddedAt)
ON CONFLICT (ExclusionKey) DO UPDATE SET AddedAt = excluded.AddedAt;",
                keys.Select(key => new { ExclusionKey = key, AddedAt = now })));
        }

        public Dictionary<int, DateTime> GetAddedDates(IEnumerable<ImportListExclusion> exclusions)
        {
            var result = new Dictionary<int, DateTime>();
            var list = (exclusions ?? Enumerable.Empty<ImportListExclusion>()).Where(e => Key(e) != null).ToList();
            if (list.Count == 0)
            {
                return result;
            }

            var stored = Run("read exclusion dates", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), connection =>
                connection.Query<Row>("SELECT ExclusionKey, AddedAt FROM ImportListExclusionDates")
                    .Where(row => !string.IsNullOrWhiteSpace(row.ExclusionKey))
                    .GroupBy(row => row.ExclusionKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().AddedAt, StringComparer.OrdinalIgnoreCase));

            foreach (var exclusion in list)
            {
                if (stored.TryGetValue(Key(exclusion), out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    var date = TalearrRecordingsStore.ParseDate(value);
                    if (date != default)
                    {
                        result[exclusion.Id] = date;
                    }
                }
            }

            return result;
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; exclusion dates show blank", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public string ExclusionKey { get; set; }
            public string AddedAt { get; set; }
        }
    }
}
