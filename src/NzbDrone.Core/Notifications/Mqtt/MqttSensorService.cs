using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.History;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Notifications.Mqtt
{
    public sealed class MqttLastImported
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string MediaType { get; set; }
        public DateTime ImportedAt { get; set; }
    }

    public sealed class MqttSensorState
    {
        public int Queue { get; set; }
        public int Missing { get; set; }
        public int HealthIssues { get; set; }
        public MqttLastImported LastImported { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public interface IMqttSensorService
    {
        MqttSensorState BuildState();

        // The retained state, availability and (when enabled) Home Assistant discovery messages.
        List<MqttMessage> BuildMessages(MqttSettings settings, MqttSensorState state);

        void PublishNow(MqttSettings settings);
    }

    // Talearr: MQTT sensors. A one minute timer checks every MQTT connection with sensors on and
    // publishes when its interval is due; an import refreshes them at once. Nothing is stored:
    // the broker keeps the retained state, and the last import is re-read from History on start.
    public class MqttSensorService : IMqttSensorService,
                                     IHandle<ApplicationStartedEvent>,
                                     IHandle<ApplicationShutdownRequested>,
                                     IHandleAsync<BookImportedEvent>
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

        // Lazy: the MQTT notification depends on this service, and these services reach the notification
        // factory (and so every notification) through health checks and event handlers
        private readonly Lazy<INotificationFactory> _notificationFactory;
        private readonly IMqttProxy _proxy;
        private readonly Lazy<IQueueService> _queueService;
        private readonly Lazy<IBookService> _bookService;
        private readonly Lazy<IHealthCheckService> _healthCheckService;
        private readonly Lazy<IHistoryService> _historyService;
        private readonly Logger _logger;

        private readonly Dictionary<int, DateTime> _lastPublished = new Dictionary<int, DateTime>();
        private readonly object _lock = new object();
        private MqttLastImported _lastImported;
        private bool _lastImportedLoaded;
        private Timer _timer;
        private int _running;

        public MqttSensorService(Lazy<INotificationFactory> notificationFactory,
                                 IMqttProxy proxy,
                                 Lazy<IQueueService> queueService,
                                 Lazy<IBookService> bookService,
                                 Lazy<IHealthCheckService> healthCheckService,
                                 Lazy<IHistoryService> historyService,
                                 Logger logger)
        {
            _notificationFactory = notificationFactory;
            _proxy = proxy;
            _queueService = queueService;
            _bookService = bookService;
            _healthCheckService = healthCheckService;
            _historyService = historyService;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            _timer = new Timer(_ => PublishDue(false), null, Tick, Tick);
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            _timer?.Dispose();
            _timer = null;
        }

        public void HandleAsync(BookImportedEvent message)
        {
            if (message?.Book == null || !message.NewDownload)
            {
                return;
            }

            lock (_lock)
            {
                _lastImported = new MqttLastImported
                {
                    BookId = message.Book.Id,
                    Title = message.Book.Title,
                    Author = (message.Author ?? message.Book.Author)?.Name,
                    MediaType = message.Book.MediaType.ToString().ToLowerInvariant(),
                    ImportedAt = DateTime.UtcNow
                };
                _lastImportedLoaded = true;
            }

            PublishDue(true);
        }

        public MqttSensorState BuildState()
        {
            return new MqttSensorState
            {
                Queue = Safe(() => _queueService.Value.GetQueue().Count, "queue"),
                Missing = Safe(CountMissing, "missing books"),
                HealthIssues = Safe(() => _healthCheckService.Value.Results().Count(r => r.Type != HealthCheckResult.Ok), "health checks"),
                LastImported = GetLastImported(),
                UpdatedAt = DateTime.UtcNow
            };
        }

        public List<MqttMessage> BuildMessages(MqttSettings settings, MqttSensorState state)
        {
            var messages = new List<MqttMessage>();

            if (settings.HomeAssistantDiscovery)
            {
                var version = BuildInfo.Version.ToString();
                messages.AddRange(MqttTopics.Sensors.Select(sensor => new MqttMessage(
                    MqttTopics.Discovery(settings, sensor),
                    MqttTopics.DiscoveryPayload(settings, sensor, version).ToJson(),
                    true)));
            }

            messages.Add(new MqttMessage(MqttTopics.SensorState(settings), ToCamelCaseJson(state), true));
            messages.Add(new MqttMessage(MqttTopics.Availability(settings), "online", true));

            return messages;
        }

        public void PublishNow(MqttSettings settings)
        {
            _proxy.Publish(settings, BuildMessages(settings, BuildState()));
        }

        private void PublishDue(bool force)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }

            try
            {
                var definitions = _notificationFactory.Value.All()
                    .Where(d => d.Implementation == nameof(Mqtt) && d.Settings is MqttSettings s && s.PublishSensors)
                    .ToList();

                if (definitions.Count == 0)
                {
                    return;
                }

                MqttSensorState state = null;

                foreach (var definition in definitions)
                {
                    var settings = (MqttSettings)definition.Settings;
                    var interval = TimeSpan.FromMinutes(Math.Max(1, settings.SensorInterval));

                    if (!force && _lastPublished.TryGetValue(definition.Id, out var last) && DateTime.UtcNow - last < interval)
                    {
                        continue;
                    }

                    state ??= BuildState();

                    try
                    {
                        _proxy.Publish(settings, BuildMessages(settings, state));
                        _lastPublished[definition.Id] = DateTime.UtcNow;
                    }
                    catch (MqttPublishException ex)
                    {
                        // Try again on the next tick rather than waiting a whole interval.
                        _logger.Warn("MQTT '{0}': {1}", definition.Name, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "MQTT sensors could not be published");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private int CountMissing()
        {
            var spec = new PagingSpec<Book>
            {
                Page = 1,
                PageSize = 1,
                SortKey = "releaseDate",
                SortDirection = SortDirection.Descending
            };

            // Same filter as Wanted > Missing (Book.Monitored is a legacy column that stays false).
            spec.FilterExpressions.Add(AuthorExtensions.GetBookMonitoringFilter(null, true));

            return _bookService.Value.BooksWithoutFiles(spec).TotalRecords;
        }

        private MqttLastImported GetLastImported()
        {
            lock (_lock)
            {
                if (_lastImportedLoaded)
                {
                    return _lastImported;
                }

                _lastImportedLoaded = true;
            }

            try
            {
                var latest = _historyService.Value.Since(DateTime.UtcNow.AddDays(-90), EntityHistoryEventType.BookFileImported)
                    .OrderByDescending(h => h.Date)
                    .FirstOrDefault();

                if (latest == null)
                {
                    return null;
                }

                var book = latest.Book ?? _bookService.Value.GetBook(latest.BookId);
                var imported = new MqttLastImported
                {
                    BookId = latest.BookId,
                    Title = book?.Title ?? latest.SourceTitle,
                    Author = (latest.Author ?? book?.Author)?.Name,
                    MediaType = book?.MediaType.ToString().ToLowerInvariant(),
                    ImportedAt = latest.Date
                };

                lock (_lock)
                {
                    _lastImported ??= imported;
                    return _lastImported;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "MQTT: could not read the last import from History");
                return null;
            }
        }

        private int Safe(Func<int> read, string what)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "MQTT: could not count {0}", what);
                return 0;
            }
        }

        public static string ToCamelCaseJson(object value)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(value, new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
                DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc
            });
        }
    }
}
