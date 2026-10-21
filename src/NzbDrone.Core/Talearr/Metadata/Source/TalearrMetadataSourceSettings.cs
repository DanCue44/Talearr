using System;
using NLog;
using NzbDrone.Core.Configuration;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: which metadata source author and book data comes from.
    public enum TalearrMetadataSource
    {
        // The Chaptarr metadata server (upstream behaviour; existing libraries stay on it).
        Chaptarr = 0,

        // Talearr's own source built on Hardcover: authors and books with Hardcover ids are added,
        // refreshed and looked up there (BookInfoProxy). New installs start here (TalearrMetadataSourceDefaults).
        Talearr = 1
    }

    public class TalearrMetadataSourceSavedEvent : IEvent
    {
        public TalearrMetadataSourceSavedEvent(TalearrMetadataSource source)
        {
            Source = source;
        }

        public TalearrMetadataSource Source { get; }
    }

    public interface ITalearrMetadataSourceSettingsService
    {
        TalearrMetadataSource GetSource();
        TalearrMetadataSource SaveSource(TalearrMetadataSource source);
    }

    // Talearr: stored as the talearr.metadata.source row in the existing Config table, which
    // Chaptarr ignores, so a rollback to Chaptarr on the same database is unaffected.
    public class TalearrMetadataSourceSettingsService : ITalearrMetadataSourceSettingsService
    {
        public const string SourceKey = "talearr.metadata.source";
        public const TalearrMetadataSource DefaultSource = TalearrMetadataSource.Chaptarr;

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrMetadataSource? _cached;

        public TalearrMetadataSourceSettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public static string ToApiValue(TalearrMetadataSource source)
        {
            return source == TalearrMetadataSource.Talearr ? "talearr" : "chaptarr";
        }

        public static bool TryParse(string value, out TalearrMetadataSource source)
        {
            source = DefaultSource;
            switch (value?.Trim().ToLowerInvariant())
            {
                case "chaptarr":
                    source = TalearrMetadataSource.Chaptarr;
                    return true;
                case "talearr":
                    source = TalearrMetadataSource.Talearr;
                    return true;
                default:
                    return false;
            }
        }

        public TalearrMetadataSource GetSource()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Value;
            }
        }

        public TalearrMetadataSource SaveSource(TalearrMetadataSource source)
        {
            if (!Enum.IsDefined(typeof(TalearrMetadataSource), source))
            {
                source = DefaultSource;
            }

            lock (_lock)
            {
                _repository.Upsert(SourceKey, ToApiValue(source));
                _logger.Info("Talearr metadata source saved: {0}", ToApiValue(source));
                _cached = Load();
            }

            // Re-runs TalearrMetadataSourceCheck.
            _eventAggregator?.PublishEvent(new TalearrMetadataSourceSavedEvent(GetSource()));
            return GetSource();
        }

        private TalearrMetadataSource Load()
        {
            try
            {
                var value = _repository.Get(SourceKey)?.Value;
                return TryParse(value, out var source) ? source : DefaultSource;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", SourceKey);
                return DefaultSource;
            }
        }
    }
}
