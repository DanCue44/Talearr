using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.SeriesMonitoring
{
    public interface ITalearrSeriesRuleStore
    {
        List<TalearrSeriesRule> All();
        TalearrSeriesRule Get(string seriesKey);
        void Save(TalearrSeriesRule rule);
        void Remove(string seriesKey);
    }

    // TaleArr: series rules live in the existing Config key/value table, one row per series
    // ("talearr.series.monitor.<series key>", JSON). User choices, so not in the rebuildable
    // talearr.db. Stock Chaptarr never reads these keys.
    public class TalearrSeriesRuleStore : ITalearrSeriesRuleStore
    {
        public const string KeyPrefix = "talearr.series.monitor.";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        public TalearrSeriesRuleStore(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static string ConfigKey(string seriesKey) => KeyPrefix + seriesKey;

        public List<TalearrSeriesRule> All()
        {
            lock (_lock)
            {
                try
                {
                    return (_repository.All() ?? Enumerable.Empty<Config>())
                        .Where(c => c?.Key != null && c.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(c => Parse(c.Key.Substring(KeyPrefix.Length), c.Value))
                        .Where(r => r != null)
                        .OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "TaleArr: unable to read the series monitoring rules");
                    return new List<TalearrSeriesRule>();
                }
            }
        }

        public TalearrSeriesRule Get(string seriesKey)
        {
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return null;
            }

            lock (_lock)
            {
                var row = _repository.Get(ConfigKey(seriesKey));
                return row == null ? null : Parse(seriesKey, row.Value);
            }
        }

        public void Save(TalearrSeriesRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.SeriesKey))
            {
                return;
            }

            lock (_lock)
            {
                _repository.Upsert(ConfigKey(rule.SeriesKey), JsonSerializer.Serialize(rule, JsonOptions));
            }
        }

        public void Remove(string seriesKey)
        {
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return;
            }

            lock (_lock)
            {
                var row = _repository.Get(ConfigKey(seriesKey));
                if (row != null)
                {
                    _repository.Delete(row);
                }
            }
        }

        private TalearrSeriesRule Parse(string seriesKey, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var rule = JsonSerializer.Deserialize<TalearrSeriesRule>(json, JsonOptions);
                if (rule == null)
                {
                    return null;
                }

                rule.SeriesKey = seriesKey;
                rule.Handled = new Dictionary<string, List<string>>(rule.Handled ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                rule.PendingSearch = new Dictionary<string, List<string>>(rule.PendingSearch ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);
                return rule;
            }
            catch (JsonException ex)
            {
                _logger.Warn(ex, "TaleArr: ignoring an unreadable series rule for {0}", seriesKey);
                return null;
            }
        }
    }
}
