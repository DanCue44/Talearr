using System;
using System.Collections.Concurrent;
using System.Linq;
using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: keeps MAM's dynamic seedbox IP in step with the torrent client's VPN egress.
    // Runs once on startup, then the 5-minute scheduled task (faster when an interval is below 5
    // minutes, see MyAnonaMouseSchedule) checks each MAM indexer that has
    // the update enabled against its own Dynamic Seedbox Interval. A manual run from
    // System > Tasks checks every enabled indexer right away.
    public class MyAnonaMouseDynamicSeedboxService : IHandleAsync<ApplicationStartedEvent>,
                                                     IExecute<UpdateMyAnonaMouseDynamicSeedboxCommand>
    {
        public const int SchedulerIntervalMinutes = 5;

        private readonly IIndexerFactory _indexerFactory;
        private readonly IMyAnonaMouseSessionMonitor _sessionMonitor;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<int, DateTime> _lastAttemptUtc = new ConcurrentDictionary<int, DateTime>();
        private readonly object _runLock = new object();

        public MyAnonaMouseDynamicSeedboxService(IIndexerFactory indexerFactory,
                                                 IMyAnonaMouseSessionMonitor sessionMonitor,
                                                 Logger logger)
        {
            _indexerFactory = indexerFactory;
            _sessionMonitor = sessionMonitor;
            _logger = logger;
        }

        // Async so a slow proxy or MAM outage never holds up startup.
        public void HandleAsync(ApplicationStartedEvent message)
        {
            Run(force: true, source: "application startup");
        }

        public void Execute(UpdateMyAnonaMouseDynamicSeedboxCommand message)
        {
            Run(force: message.Trigger == CommandTrigger.Manual, source: message.Trigger == CommandTrigger.Manual ? "manual run" : "scheduled task");
        }

        public void Run(bool force, string source, DateTime? nowUtc = null)
        {
            if (!System.Threading.Monitor.TryEnter(_runLock))
            {
                return;
            }

            try
            {
                var now = nowUtc ?? DateTime.UtcNow;

                foreach (var indexer in _indexerFactory.GetAvailableProviders().OfType<MyAnonaMouse>())
                {
                    if (indexer.Definition is not IndexerDefinition definition ||
                        definition.Settings is not MyAnonaMouseSettings settings ||
                        !settings.DynamicSeedboxEnabled)
                    {
                        continue;
                    }

                    var interval = TimeSpan.FromMinutes(Math.Clamp(settings.DynamicSeedboxInterval,
                        MyAnonaMouseSettings.MinimumDynamicSeedboxInterval,
                        MyAnonaMouseSettings.MaximumDynamicSeedboxInterval));

                    // Allow a minute of scheduler jitter so a 30-minute interval runs every
                    // 30 minutes rather than every 35. Talearr: not below 10 minutes, where the
                    // task ticks at the interval itself (MyAnonaMouseSchedule) and a minute off
                    // would halve a 2-minute interval.
                    var due = interval >= TimeSpan.FromMinutes(10) ? interval - TimeSpan.FromMinutes(1) : interval;
                    if (!force &&
                        _lastAttemptUtc.TryGetValue(definition.Id, out var last) &&
                        now - last < due)
                    {
                        continue;
                    }

                    _lastAttemptUtc[definition.Id] = now;
                    Update(indexer, definition, settings, source);
                }
            }
            finally
            {
                System.Threading.Monitor.Exit(_runLock);
            }
        }

        private void Update(MyAnonaMouse indexer, IndexerDefinition definition, MyAnonaMouseSettings settings, string source)
        {
            var mamId = string.IsNullOrWhiteSpace(settings.SeedboxMamId) ? settings.MamId : settings.SeedboxMamId;
            MyAnonaMouseSeedboxResult result;

            try
            {
                result = indexer.UpdateDynamicSeedboxAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = new MyAnonaMouseSeedboxResult
                {
                    Outcome = MyAnonaMouseSeedboxOutcome.Failed,
                    Message = $"{ex.GetType().Name}: {ex.Message}",
                    CheckedUtc = DateTime.UtcNow
                };
            }

            _sessionMonitor.RecordSeedboxResult(definition.Id, mamId, result);
            Log(definition, result, source);
        }

        private void Log(IndexerDefinition definition, MyAnonaMouseSeedboxResult result, string source)
        {
            var where = string.IsNullOrWhiteSpace(result.Ip) ? string.Empty : $" (IP {result.Ip}{(string.IsNullOrWhiteSpace(result.Asn) ? string.Empty : ", ASN " + result.Asn)})";

            switch (result.Outcome)
            {
                case MyAnonaMouseSeedboxOutcome.NoChange:
                    _logger.Debug("MAM dynamic seedbox for '{0}' ({1}): no change{2}", definition.Name, source, where);
                    break;
                case MyAnonaMouseSeedboxOutcome.Updated:
                    _logger.Info("MAM dynamic seedbox for '{0}' ({1}): MAM registered the new IP{2}", definition.Name, source, where);
                    break;
                case MyAnonaMouseSeedboxOutcome.TooRecent:
                    _logger.Info("MAM dynamic seedbox for '{0}' ({1}): last change too recent, MAM allows one IP change per hour; will retry", definition.Name, source);
                    break;
                default:
                    _logger.Warn("MAM dynamic seedbox for '{0}' ({1}) failed: {2} ({3}){4}", definition.Name, source, result.Outcome, result.Message, where);
                    break;
            }
        }
    }
}
