using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // TaleArr: settings for merging multi-file audiobooks into one chaptered M4B.
    public sealed class TalearrM4bSettings
    {
        // Off by default: after an import, books made of several mp3/m4a files are merged into a
        // single M4B in a background command. The manual "Convert to M4B" action works either way.
        public bool ConvertAfterImport { get; set; }

        public static TalearrM4bSettings Default => new TalearrM4bSettings();

        public TalearrM4bSettings Clone()
        {
            return new TalearrM4bSettings { ConvertAfterImport = ConvertAfterImport };
        }
    }

    public interface ITalearrM4bSettingsService
    {
        TalearrM4bSettings Get();
        TalearrM4bSettings Save(TalearrM4bSettings settings);
    }

    // Stored as talearr.audiobook.* rows in the existing Config key/value table, like the grab
    // policy. No schema change; Chaptarr ignores rows it does not know.
    public class TalearrM4bSettingsService : ITalearrM4bSettingsService
    {
        public const string ConvertAfterImportKey = "talearr.audiobook.m4bafterimport";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrM4bSettings _cached;

        public TalearrM4bSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public TalearrM4bSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrM4bSettings Save(TalearrM4bSettings settings)
        {
            settings ??= TalearrM4bSettings.Default;

            lock (_lock)
            {
                _repository.Upsert(ConvertAfterImportKey, settings.ConvertAfterImport.ToString());
                _logger.Info("TaleArr M4B settings saved: convertAfterImport={0}", settings.ConvertAfterImport);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrM4bSettings Load()
        {
            var settings = TalearrM4bSettings.Default;

            try
            {
                var value = _repository.Get(ConvertAfterImportKey)?.Value;
                if (bool.TryParse(value?.Trim(), out var convert))
                {
                    settings.ConvertAfterImport = convert;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr M4B settings, using the defaults");
            }

            return settings;
        }
    }
}
