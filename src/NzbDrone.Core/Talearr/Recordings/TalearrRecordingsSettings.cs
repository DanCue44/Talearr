using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Where Audible facts come from.
    public enum TalearrAudibleProvider
    {
        // The Audible catalog API of the chosen marketplace (batch lookups and search).
        Audible = 0,

        // api.audnex.us, one ASIN per call, no search. Community run.
        Audnexus = 1,

        // No lookups at all; recordings use metadata server facts only.
        Off = 2
    }

    // Which editions the scheduled refresh looks up.
    public enum TalearrEnrichScope
    {
        // Monitored editions of monitored audiobooks, including copy rows.
        Monitored = 0,

        // Every audiobook edition of monitored audiobooks.
        All = 1,

        // Nothing in the background; on-demand refresh still works.
        Off = 2
    }

    // TaleArr: recordings settings. Stored as talearr.recordings.* rows in the existing Config
    // table, which Chaptarr ignores, so a rollback is unaffected.
    public sealed class TalearrRecordingsSettings
    {
        public string Marketplace { get; set; } = TalearrAudibleMarketplaces.Default;

        public TalearrAudibleProvider Provider { get; set; } = TalearrAudibleProvider.Audible;

        // Off by default: ask Audnexus when the Audible catalog has no answer or is throttling.
        public bool AudnexusFallback { get; set; }

        public TalearrEnrichScope Enrich { get; set; } = TalearrEnrichScope.Monitored;

        // On by default: "add the unabridged version" (and other added recordings) search right away.
        public bool SearchOnAdd { get; set; } = true;

        // On by default: an arriving unabridged recording is kept next to the abridged one, never
        // in place of it. Removing a recording is always a manual action.
        public bool KeepBoth { get; set; } = true;

        public static TalearrRecordingsSettings Default => new TalearrRecordingsSettings();

        public TalearrRecordingsSettings Clone()
        {
            return new TalearrRecordingsSettings
            {
                Marketplace = Marketplace,
                Provider = Provider,
                AudnexusFallback = AudnexusFallback,
                Enrich = Enrich,
                SearchOnAdd = SearchOnAdd,
                KeepBoth = KeepBoth
            };
        }
    }

    public interface ITalearrRecordingsSettingsService
    {
        TalearrRecordingsSettings Get();
        TalearrRecordingsSettings Save(TalearrRecordingsSettings settings);
    }

    public class TalearrRecordingsSettingsService : ITalearrRecordingsSettingsService
    {
        public const string MarketplaceKey = "talearr.recordings.marketplace";
        public const string ProviderKey = "talearr.recordings.provider";
        public const string AudnexusFallbackKey = "talearr.recordings.audnexusfallback";
        public const string EnrichKey = "talearr.recordings.enrich";
        public const string SearchOnAddKey = "talearr.recordings.searchonadd";
        public const string KeepBothKey = "talearr.recordings.keepboth";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrRecordingsSettings _cached;

        public TalearrRecordingsSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        // Null-safe accessor for optional dependencies (tests and partially wired services).
        public static TalearrRecordingsSettings Resolve(ITalearrRecordingsSettingsService service)
        {
            try
            {
                return service?.Get() ?? TalearrRecordingsSettings.Default;
            }
            catch (Exception)
            {
                return TalearrRecordingsSettings.Default;
            }
        }

        public TalearrRecordingsSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrRecordingsSettings Save(TalearrRecordingsSettings settings)
        {
            settings ??= TalearrRecordingsSettings.Default;
            var marketplace = TalearrAudibleMarketplaces.Get(settings.Marketplace).Code;

            lock (_lock)
            {
                _repository.Upsert(MarketplaceKey, marketplace);
                _repository.Upsert(ProviderKey, Defined(settings.Provider, TalearrAudibleProvider.Audible).ToString().ToLowerInvariant());
                _repository.Upsert(AudnexusFallbackKey, settings.AudnexusFallback.ToString());
                _repository.Upsert(EnrichKey, Defined(settings.Enrich, TalearrEnrichScope.Monitored).ToString().ToLowerInvariant());
                _repository.Upsert(SearchOnAddKey, settings.SearchOnAdd.ToString());
                _repository.Upsert(KeepBothKey, settings.KeepBoth.ToString());

                _cached = Load();
                _logger.Info("TaleArr recordings settings saved: marketplace={0}, provider={1}, audnexusFallback={2}, enrich={3}, searchOnAdd={4}, keepBoth={5}",
                    _cached.Marketplace,
                    _cached.Provider,
                    _cached.AudnexusFallback,
                    _cached.Enrich,
                    _cached.SearchOnAdd,
                    _cached.KeepBoth);

                return _cached.Clone();
            }
        }

        private TalearrRecordingsSettings Load()
        {
            var settings = TalearrRecordingsSettings.Default;

            var marketplace = TalearrAudibleMarketplaces.Find(Read(MarketplaceKey));
            if (marketplace != null)
            {
                settings.Marketplace = marketplace.Code;
            }

            settings.Provider = ReadEnum(ProviderKey, settings.Provider);
            settings.Enrich = ReadEnum(EnrichKey, settings.Enrich);
            settings.AudnexusFallback = ReadBool(AudnexusFallbackKey, settings.AudnexusFallback);
            settings.SearchOnAdd = ReadBool(SearchOnAddKey, settings.SearchOnAdd);
            settings.KeepBoth = ReadBool(KeepBothKey, settings.KeepBoth);

            return settings;
        }

        private static T Defined<T>(T value, T fallback)
            where T : struct, Enum
        {
            return Enum.IsDefined(typeof(T), value) ? value : fallback;
        }

        private T ReadEnum<T>(string key, T fallback)
            where T : struct, Enum
        {
            var raw = Read(key);
            if (raw != null && !int.TryParse(raw, out _) && Enum.TryParse<T>(raw, true, out var parsed) && Enum.IsDefined(typeof(T), parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private bool ReadBool(string key, bool fallback)
        {
            return bool.TryParse(Read(key), out var value) ? value : fallback;
        }

        private string Read(string key)
        {
            try
            {
                var value = _repository.Get(key)?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr setting {0}, using the default", key);
                return null;
            }
        }
    }
}
