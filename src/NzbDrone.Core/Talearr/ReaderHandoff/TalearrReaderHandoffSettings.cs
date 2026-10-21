using System;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.ReaderHandoff
{
    // Talearr: reader hand-off (docs/TALEARR_REFERENCE.md, "Reader hand-off"). After an ebook import, copy the
    // file to a watch folder such as Calibre-Web-Automated's ingest folder.
    public sealed class TalearrReaderHandoffSettings
    {
        // Off by default: nothing is copied anywhere until a folder is set and this is turned on.
        public bool Enabled { get; set; }

        // The watch folder as Talearr sees it (inside the container).
        public string Path { get; set; } = string.Empty;

        // Put each copy in a subfolder named after the author.
        public bool AuthorFolder { get; set; }

        // Convert EPUB copies to KEPUB with kepubify (the library file is never changed).
        public bool ConvertToKepub { get; set; }

        public static TalearrReaderHandoffSettings Default => new TalearrReaderHandoffSettings();

        public bool IsActive => Enabled && !string.IsNullOrWhiteSpace(Path);

        public TalearrReaderHandoffSettings Clone()
        {
            return new TalearrReaderHandoffSettings
            {
                Enabled = Enabled,
                Path = Path ?? string.Empty,
                AuthorFolder = AuthorFolder,
                ConvertToKepub = ConvertToKepub
            };
        }
    }

    public class TalearrReaderHandoffSettingsChangedEvent : IEvent
    {
    }

    public interface ITalearrReaderHandoffSettingsService
    {
        TalearrReaderHandoffSettings Get();
        TalearrReaderHandoffSettings Save(TalearrReaderHandoffSettings settings);
    }

    // Stored as talearr.readerhandoff.* rows in the existing Config key/value table. No schema
    // change; Chaptarr ignores rows it does not know, so a rollback simply stops the copies.
    public class TalearrReaderHandoffSettingsService : ITalearrReaderHandoffSettingsService
    {
        public const string EnabledKey = "talearr.readerhandoff.enabled";
        public const string PathKey = "talearr.readerhandoff.path";
        public const string AuthorFolderKey = "talearr.readerhandoff.authorfolder";
        public const string KepubKey = "talearr.readerhandoff.kepub";

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrReaderHandoffSettings _cached;

        public TalearrReaderHandoffSettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public TalearrReaderHandoffSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrReaderHandoffSettings Save(TalearrReaderHandoffSettings settings)
        {
            settings ??= TalearrReaderHandoffSettings.Default;
            TalearrReaderHandoffSettings saved;

            lock (_lock)
            {
                _repository.Upsert(EnabledKey, settings.Enabled.ToString());
                _repository.Upsert(PathKey, (settings.Path ?? string.Empty).Trim());
                _repository.Upsert(AuthorFolderKey, settings.AuthorFolder.ToString());
                _repository.Upsert(KepubKey, settings.ConvertToKepub.ToString());
                _logger.Info("Talearr reader hand-off settings saved: enabled={0}, path='{1}', authorFolder={2}, kepub={3}",
                    settings.Enabled,
                    settings.Path,
                    settings.AuthorFolder,
                    settings.ConvertToKepub);

                _cached = Load();
                saved = _cached.Clone();
            }

            _eventAggregator.PublishEvent(new TalearrReaderHandoffSettingsChangedEvent());

            return saved;
        }

        private TalearrReaderHandoffSettings Load()
        {
            var settings = TalearrReaderHandoffSettings.Default;

            try
            {
                settings.Enabled = ReadBool(EnabledKey, settings.Enabled);
                settings.Path = _repository.Get(PathKey)?.Value?.Trim() ?? string.Empty;
                settings.AuthorFolder = ReadBool(AuthorFolderKey, settings.AuthorFolder);
                settings.ConvertToKepub = ReadBool(KepubKey, settings.ConvertToKepub);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr reader hand-off settings, using the defaults");
                settings = TalearrReaderHandoffSettings.Default;
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
