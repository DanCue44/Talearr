using System;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    // Talearr: OPDS catalog (README.md, "Ebook delivery"). A read-only OPDS 1.2 feed at /opds
    // that ebook readers such as KOReader, Panels, Thorium or Moon+ Reader can browse.
    public sealed class TalearrOpdsSettings
    {
        public const int DefaultPageSize = 50;
        public const int MinPageSize = 10;
        public const int MaxPageSize = 200;

        // Off by default: /opds answers 404 until this is turned on.
        public bool Enabled { get; set; }

        // Entries per feed page.
        public int PageSize { get; set; } = DefaultPageSize;

        // Offer a KEPUB download next to each EPUB, converted on the fly with kepubify when it is
        // installed. The library file is never changed.
        public bool OfferKepub { get; set; }

        public static TalearrOpdsSettings Default => new TalearrOpdsSettings();

        public static int ClampPageSize(int pageSize)
        {
            if (pageSize <= 0)
            {
                return DefaultPageSize;
            }

            return Math.Clamp(pageSize, MinPageSize, MaxPageSize);
        }

        public TalearrOpdsSettings Clone()
        {
            return new TalearrOpdsSettings
            {
                Enabled = Enabled,
                PageSize = ClampPageSize(PageSize),
                OfferKepub = OfferKepub
            };
        }
    }

    public class TalearrOpdsSettingsChangedEvent : IEvent
    {
    }

    public interface ITalearrOpdsSettingsService
    {
        TalearrOpdsSettings Get();
        TalearrOpdsSettings Save(TalearrOpdsSettings settings);
    }

    // Stored as talearr.opds.* rows in the existing Config table. No schema change; Chaptarr
    // ignores rows it does not know, so a rollback simply removes the feed.
    public class TalearrOpdsSettingsService : ITalearrOpdsSettingsService
    {
        public const string EnabledKey = "talearr.opds.enabled";
        public const string PageSizeKey = "talearr.opds.pagesize";
        public const string KepubKey = "talearr.opds.kepub";

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrOpdsSettings _cached;

        public TalearrOpdsSettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public TalearrOpdsSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrOpdsSettings Save(TalearrOpdsSettings settings)
        {
            settings ??= TalearrOpdsSettings.Default;
            TalearrOpdsSettings saved;

            lock (_lock)
            {
                _repository.Upsert(EnabledKey, settings.Enabled.ToString());
                _repository.Upsert(PageSizeKey, TalearrOpdsSettings.ClampPageSize(settings.PageSize).ToString());
                _repository.Upsert(KepubKey, settings.OfferKepub.ToString());
                _logger.Info("Talearr OPDS settings saved: enabled={0}, pageSize={1}, kepub={2}",
                    settings.Enabled,
                    TalearrOpdsSettings.ClampPageSize(settings.PageSize),
                    settings.OfferKepub);

                _cached = Load();
                saved = _cached.Clone();
            }

            _eventAggregator.PublishEvent(new TalearrOpdsSettingsChangedEvent());

            return saved;
        }

        private TalearrOpdsSettings Load()
        {
            var settings = TalearrOpdsSettings.Default;

            try
            {
                settings.Enabled = ReadBool(EnabledKey, settings.Enabled);
                settings.OfferKepub = ReadBool(KepubKey, settings.OfferKepub);

                if (int.TryParse(_repository.Get(PageSizeKey)?.Value?.Trim(), out var pageSize))
                {
                    settings.PageSize = TalearrOpdsSettings.ClampPageSize(pageSize);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr OPDS settings, using the defaults");
                settings = TalearrOpdsSettings.Default;
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
