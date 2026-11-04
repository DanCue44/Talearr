using System;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Ebook.Epub
{
    // Talearr: settings for converting Kindle ebooks (AZW3, MOBI) to EPUB.
    public sealed class TalearrEpubSettings
    {
        // Off by default: after an ebook in a Kindle format is imported, an EPUB copy is made
        // in a background command. The "Convert library to EPUB" action works either way.
        public bool ConvertAfterImport { get; set; }

        // On by default: the Kindle file stays next to the EPUB. Off replaces it (to the recycle
        // bin when one is set) once the EPUB has been checked.
        public bool KeepOriginal { get; set; } = true;

        public static TalearrEpubSettings Default => new TalearrEpubSettings();

        public TalearrEpubSettings Clone()
        {
            return new TalearrEpubSettings { ConvertAfterImport = ConvertAfterImport, KeepOriginal = KeepOriginal };
        }
    }

    public interface ITalearrEpubSettingsService
    {
        TalearrEpubSettings Get();
        TalearrEpubSettings Save(TalearrEpubSettings settings);
    }

    // Stored as talearr.ebook.* rows in the existing Config key/value table, like the M4B merge.
    // No schema change; Chaptarr ignores rows it does not know.
    public class TalearrEpubSettingsService : ITalearrEpubSettingsService
    {
        public const string ConvertAfterImportKey = "talearr.ebook.epubafterimport";
        public const string KeepOriginalKey = "talearr.ebook.epubkeeporiginal";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrEpubSettings _cached;

        public TalearrEpubSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public TalearrEpubSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrEpubSettings Save(TalearrEpubSettings settings)
        {
            settings ??= TalearrEpubSettings.Default;

            lock (_lock)
            {
                _repository.Upsert(ConvertAfterImportKey, settings.ConvertAfterImport.ToString());
                _repository.Upsert(KeepOriginalKey, settings.KeepOriginal.ToString());
                _logger.Info("Talearr EPUB settings saved: convertAfterImport={0}, keepOriginal={1}", settings.ConvertAfterImport, settings.KeepOriginal);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrEpubSettings Load()
        {
            var settings = TalearrEpubSettings.Default;

            try
            {
                settings.ConvertAfterImport = ReadBool(ConvertAfterImportKey, settings.ConvertAfterImport);
                settings.KeepOriginal = ReadBool(KeepOriginalKey, settings.KeepOriginal);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not read the Talearr EPUB settings, using defaults");
            }

            return settings;
        }

        private bool ReadBool(string key, bool fallback)
        {
            var value = _repository.Get(key)?.Value;
            return bool.TryParse(value?.Trim(), out var parsed) ? parsed : fallback;
        }
    }
}
