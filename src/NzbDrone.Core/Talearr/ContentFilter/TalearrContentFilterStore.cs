using System;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.ContentFilter
{
    public interface ITalearrContentFilterStore
    {
        // The profile's rules, or an empty (inactive) filter.
        TalearrContentFilter Get(int metadataProfileId);
        void Save(int metadataProfileId, TalearrContentFilter filter);
        void Remove(int metadataProfileId);
    }

    // Talearr: one Config row per metadata profile ("talearr.metadataprofile.<id>", JSON). User
    // choices, so not in the rebuildable talearr.db. Stock Chaptarr never reads these keys, and a
    // rollback simply stops applying the rules.
    public class TalearrContentFilterStore : ITalearrContentFilterStore
    {
        public const string KeyPrefix = "talearr.metadataprofile.";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;

        public TalearrContentFilterStore(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static string ConfigKey(int metadataProfileId) => KeyPrefix + metadataProfileId;

        public TalearrContentFilter Get(int metadataProfileId)
        {
            if (metadataProfileId <= 0)
            {
                return new TalearrContentFilter();
            }

            try
            {
                var row = _repository.Get(ConfigKey(metadataProfileId));
                if (row == null || string.IsNullOrWhiteSpace(row.Value))
                {
                    return new TalearrContentFilter();
                }

                return (JsonSerializer.Deserialize<TalearrContentFilter>(row.Value, JsonOptions) ?? new TalearrContentFilter()).Normalized();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: ignoring unreadable genre and content rules for metadata profile {0}", metadataProfileId);
                return new TalearrContentFilter();
            }
        }

        public void Save(int metadataProfileId, TalearrContentFilter filter)
        {
            if (metadataProfileId <= 0)
            {
                return;
            }

            var normalized = (filter ?? new TalearrContentFilter()).Normalized();
            if (!normalized.IsActive)
            {
                Remove(metadataProfileId);
                return;
            }

            _repository.Upsert(ConfigKey(metadataProfileId), JsonSerializer.Serialize(normalized, JsonOptions));
        }

        public void Remove(int metadataProfileId)
        {
            var row = _repository.Get(ConfigKey(metadataProfileId));
            if (row != null)
            {
                _repository.Delete(row);
            }
        }
    }
}
