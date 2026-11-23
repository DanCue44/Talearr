using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Titles
{
    public sealed class TalearrTitleSettings
    {
        // Off by default: titles stay as the metadata gives them.
        public bool CleanSeriesFromTitles { get; set; }

        public static TalearrTitleSettings Default => new TalearrTitleSettings();

        public TalearrTitleSettings Clone()
        {
            return new TalearrTitleSettings { CleanSeriesFromTitles = CleanSeriesFromTitles };
        }
    }

    public interface ITalearrTitleSettingsService
    {
        TalearrTitleSettings Get();
        TalearrTitleSettings Save(TalearrTitleSettings settings);
    }

    // Talearr: talearr.titles.* rows in the existing Config table. No schema change.
    public class TalearrTitleSettingsService : ITalearrTitleSettingsService
    {
        public const string CleanSeriesKey = "talearr.titles.cleanseries";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrTitleSettings _cached;

        public TalearrTitleSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public TalearrTitleSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrTitleSettings Save(TalearrTitleSettings settings)
        {
            settings ??= TalearrTitleSettings.Default;

            lock (_lock)
            {
                _repository.Upsert(CleanSeriesKey, settings.CleanSeriesFromTitles ? "true" : "false");
                _logger.Info("Talearr title settings saved: cleanSeriesFromTitles={0}", settings.CleanSeriesFromTitles);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrTitleSettings Load()
        {
            var settings = TalearrTitleSettings.Default;

            try
            {
                if (bool.TryParse(_repository.Get(CleanSeriesKey)?.Value?.Trim(), out var clean))
                {
                    settings.CleanSeriesFromTitles = clean;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", CleanSeriesKey);
            }

            return settings;
        }
    }
}
