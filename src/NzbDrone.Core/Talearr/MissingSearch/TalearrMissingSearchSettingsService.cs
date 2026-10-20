using System;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.MissingSearch
{
    public interface ITalearrMissingSearchSettingsService
    {
        TalearrMissingSearchSettings Get();
        TalearrMissingSearchSettings Save(TalearrMissingSearchSettings settings);
    }

    // Talearr: talearr.missingsearch.* rows in the existing Config table. No schema change.
    public class TalearrMissingSearchSettingsService : ITalearrMissingSearchSettingsService
    {
        public const string ScheduleKey = "talearr.missingsearch.schedule";
        public const string BooksPerRunKey = "talearr.missingsearch.booksperrun";
        public const string StaggerSecondsKey = "talearr.missingsearch.staggerseconds";

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrMissingSearchSettings _cached;

        public TalearrMissingSearchSettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public static TalearrMissingSearchSettings Resolve(ITalearrMissingSearchSettingsService service)
        {
            try
            {
                return service?.Get() ?? TalearrMissingSearchSettings.Default;
            }
            catch (Exception)
            {
                return TalearrMissingSearchSettings.Default;
            }
        }

        public TalearrMissingSearchSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrMissingSearchSettings Save(TalearrMissingSearchSettings settings)
        {
            settings ??= TalearrMissingSearchSettings.Default;
            TalearrMissingSearchSettings saved;
            bool wasOff;

            lock (_lock)
            {
                wasOff = (_cached ?? Load()).Schedule == MissingSearchSchedule.Off;

                _repository.Upsert(ScheduleKey, settings.Schedule.ToString().ToLowerInvariant());
                _repository.Upsert(BooksPerRunKey, settings.EffectiveBooksPerRun.ToString());
                _repository.Upsert(StaggerSecondsKey, settings.EffectiveStaggerSeconds.ToString());

                _cached = Load();
                saved = _cached.Clone();
            }

            _logger.Info("Talearr scheduled missing search saved: schedule={0}, booksPerRun={1}, staggerSeconds={2}",
                saved.Schedule,
                saved.EffectiveBooksPerRun,
                saved.EffectiveStaggerSeconds);

            _eventAggregator?.PublishEvent(new TalearrMissingSearchSettingsSavedEvent(saved.Clone(), wasOff));
            return saved;
        }

        private TalearrMissingSearchSettings Load()
        {
            var settings = TalearrMissingSearchSettings.Default;

            if (Enum.TryParse<MissingSearchSchedule>(Read(ScheduleKey), true, out var schedule) &&
                Enum.IsDefined(typeof(MissingSearchSchedule), schedule))
            {
                settings.Schedule = schedule;
            }

            if (int.TryParse(Read(BooksPerRunKey), out var booksPerRun))
            {
                settings.BooksPerRun = booksPerRun;
            }

            if (int.TryParse(Read(StaggerSecondsKey), out var stagger))
            {
                settings.StaggerSeconds = stagger;
            }

            return settings;
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
                _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", key);
                return null;
            }
        }
    }
}
