using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    // TaleArr: when the keyless fallback metadata source (Open Library) may be used.
    public enum TalearrMetadataFallbackMode
    {
        // Never use the fallback (upstream Chaptarr behaviour).
        Off = 0,

        // Search and add use Open Library while the metadata server is down or a request to it
        // fails with a server or network error. Refresh never uses it.
        WhenPrimaryDown = 1,

        // As WhenPrimaryDown, and search results from Open Library are labelled in the UI.
        WhenPrimaryDownShowSource = 2
    }

    public interface ITalearrMetadataFallbackSettingsService
    {
        TalearrMetadataFallbackMode GetMode();
        TalearrMetadataFallbackMode SaveMode(TalearrMetadataFallbackMode mode);
    }

    // TaleArr: stored as a talearr.metadatafallback.* row in the existing Config table, which
    // Chaptarr ignores, so a rollback to Chaptarr on the same database is unaffected.
    public class TalearrMetadataFallbackSettingsService : ITalearrMetadataFallbackSettingsService
    {
        public const string ModeKey = "talearr.metadatafallback.mode";
        public const TalearrMetadataFallbackMode DefaultMode = TalearrMetadataFallbackMode.WhenPrimaryDown;

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrMetadataFallbackMode? _cached;

        public TalearrMetadataFallbackSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static string ToApiValue(TalearrMetadataFallbackMode mode)
        {
            return mode switch
            {
                TalearrMetadataFallbackMode.Off => "off",
                TalearrMetadataFallbackMode.WhenPrimaryDownShowSource => "whenPrimaryDownShowSource",
                _ => "whenPrimaryDown"
            };
        }

        public static bool TryParse(string value, out TalearrMetadataFallbackMode mode)
        {
            mode = DefaultMode;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "off":
                    mode = TalearrMetadataFallbackMode.Off;
                    return true;
                case "whenprimarydown":
                    mode = TalearrMetadataFallbackMode.WhenPrimaryDown;
                    return true;
                case "whenprimarydownshowsource":
                    mode = TalearrMetadataFallbackMode.WhenPrimaryDownShowSource;
                    return true;
                default:
                    return false;
            }
        }

        public TalearrMetadataFallbackMode GetMode()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Value;
            }
        }

        public TalearrMetadataFallbackMode SaveMode(TalearrMetadataFallbackMode mode)
        {
            if (!Enum.IsDefined(typeof(TalearrMetadataFallbackMode), mode))
            {
                mode = DefaultMode;
            }

            lock (_lock)
            {
                _repository.Upsert(ModeKey, ToApiValue(mode));
                _cached = null;
                _logger.Info("TaleArr metadata fallback saved: {0}", ToApiValue(mode));
                _cached = Load();
                return _cached.Value;
            }
        }

        private TalearrMetadataFallbackMode Load()
        {
            try
            {
                var value = _repository.Get(ModeKey)?.Value;
                return TryParse(value, out var mode) ? mode : DefaultMode;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr setting {0}, using the default", ModeKey);
                return DefaultMode;
            }
        }
    }
}
