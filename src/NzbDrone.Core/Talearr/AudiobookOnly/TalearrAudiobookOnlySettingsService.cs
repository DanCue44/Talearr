using System;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.AudiobookOnly
{
    public class TalearrAudiobookOnlyChangedEvent : IEvent
    {
        public TalearrAudiobookOnlyChangedEvent(bool enabled)
        {
            Enabled = enabled;
        }

        public bool Enabled { get; }
    }

    public interface ITalearrAudiobookOnlySettingsService
    {
        bool IsEnabled();
        bool Save(bool enabled);
    }

    // Talearr: Audiobook Only (Settings > Metadata). One talearr.library.audiobookonly row in the
    // existing Config table, off by default. No schema change: Chaptarr ignores the row, and turning
    // it off shows everything again because nothing is deleted or unmonitored.
    public class TalearrAudiobookOnlySettingsService : ITalearrAudiobookOnlySettingsService
    {
        public const string EnabledKey = "talearr.library.audiobookonly";

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private bool? _cached;

        public TalearrAudiobookOnlySettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // Never throws: a read failure means the setting is off, so stock behavior is kept.
        public static bool Resolve(ITalearrAudiobookOnlySettingsService service)
        {
            try
            {
                return service?.IsEnabled() == true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsEnabled()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Value;
            }
        }

        public bool Save(bool enabled)
        {
            bool changed;

            lock (_lock)
            {
                var previous = _cached ?? Load();
                _repository.Upsert(EnabledKey, enabled ? "true" : "false");
                _cached = Load();
                changed = previous != _cached.Value;
                _logger.Info("Talearr Audiobook Only saved: {0}", _cached.Value);
            }

            if (changed)
            {
                _eventAggregator?.PublishEvent(new TalearrAudiobookOnlyChangedEvent(enabled));
            }

            return enabled;
        }

        private bool Load()
        {
            try
            {
                return bool.TryParse(_repository.Get(EnabledKey)?.Value?.Trim(), out var enabled) && enabled;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", EnabledKey);
                return false;
            }
        }
    }
}
