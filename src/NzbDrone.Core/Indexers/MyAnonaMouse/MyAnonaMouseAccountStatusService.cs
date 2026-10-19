using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseAccountStatusService : IHandle<ApplicationStartedEvent>,
                                                    IHandle<HealthCheckCompleteEvent>,
                                                    IExecute<RefreshMyAnonaMouseAccountStatusCommand>
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;
        private readonly IMamUnsatisfiedSlotGuard _slotGuard;
        private readonly object _refreshLock = new object();

        // TaleArr: last refresh attempt per indexer. Each MAM indexer is refreshed on its own
        // Account Status Refresh interval (was one fixed hour for all of them).
        private readonly Dictionary<int, DateTime> _lastRefreshAttemptUtc = new Dictionary<int, DateTime>();
        private bool _refreshInProgress;

        // Scheduler jitter: a run may start a little before a full interval has passed. Only
        // allowed for intervals of 10 minutes or more, so a short interval is not halved.
        private static readonly TimeSpan SchedulerJitter = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan JitterFrom = TimeSpan.FromMinutes(10);

        public MyAnonaMouseAccountStatusService(IIndexerFactory indexerFactory,
                                                IMamUnsatisfiedSlotGuard slotGuard,
                                                Logger logger)
        {
            _indexerFactory = indexerFactory;
            _logger = logger;
            _slotGuard = slotGuard;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(ApplicationStartedEvent message)
        {
            RefreshIfDue("application startup");
        }

        public void Handle(HealthCheckCompleteEvent message)
        {
            RefreshIfDue("health cycle");
        }

        // TaleArr: running the task by hand always refreshes. It used to share the one-hour
        // throttle with the health-cycle trigger, so running it by hand right after any other
        // refresh did nothing (a 3 ms "completed" run) and a class change on MAM (for example
        // Mouse back to User after buying upload credit) stayed stale for up to an hour. The
        // scheduled run (its task interval follows the shortest Account Status Refresh) and the
        // health cycle refresh each indexer once its own interval has passed.
        public void Execute(RefreshMyAnonaMouseAccountStatusCommand message)
        {
            var manual = message?.Trigger == CommandTrigger.Manual;
            RefreshIfDue(manual ? "manual task" : "scheduled task", force: manual);
        }

        private void RefreshIfDue(string source, bool force = false)
        {
            if (!TryBeginRefresh())
            {
                _logger.Debug("Skipping MAM account status refresh from {0}: a refresh is already running", source);
                return;
            }

            try
            {
                RefreshAccountStatuses(source, force);
            }
            finally
            {
                EndRefresh();
            }
        }

        private void RefreshAccountStatuses(string source, bool force)
        {
            _logger.Debug("Refreshing MAM account status from {0}", source);

            var refreshed = 0;
            var changed = 0;

            foreach (var indexer in _indexerFactory.GetAvailableProviders().OfType<MyAnonaMouse>())
            {
                if (indexer.Definition is not IndexerDefinition definition ||
                    definition.Settings is not MyAnonaMouseSettings settings)
                {
                    continue;
                }

                if (!TryClaim(definition.Id, settings, force))
                {
                    _logger.Debug("Skipping MAM account status refresh for '{0}' from {1}: refreshed less than {2} minutes ago",
                        definition.Name,
                        source,
                        settings.ResolveAccountStatusRefreshInterval().TotalMinutes);
                    continue;
                }

                var previousUserClass = settings.UserClass;
                var previousIsVip = settings.IsVip;
                var previousUnsatisfiedCount = settings.UnsatisfiedCount;
                var previousUnsatisfiedLimit = settings.UnsatisfiedLimit;
                var previousSnapshotUtc = settings.UnsatisfiedSnapshotUtc;

                try
                {
                    var status = indexer.RefreshAccountStatus().GetAwaiter().GetResult();
                    refreshed++;

                    // Persist every successful fetch because its refresh timestamp is the restart-safe freshness boundary.
                    _indexerFactory.Update(definition);
                    _slotGuard.Reconcile(indexer, status);

                    if (!string.Equals(previousUserClass, status.UserClass, StringComparison.Ordinal) ||
                        previousIsVip != status.IsVip ||
                        previousUnsatisfiedCount != status.UnsatisfiedCount ||
                        previousUnsatisfiedLimit != status.UnsatisfiedLimit ||
                        previousSnapshotUtc != status.SnapshotCreatedUtc)
                    {
                        changed++;
                        _logger.Info("Updated MAM account status for indexer '{0}': {1} (VIP: {2}), unsatisfied {3}/{4}",
                            definition.Name,
                            status.UserClass,
                            status.IsVip,
                            status.UnsatisfiedCount,
                            status.UnsatisfiedLimit);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to refresh MAM account status for indexer '{0}' ({1})", definition.Name, ex.GetType().Name);
                }
            }

            _logger.Debug("Refreshed MAM account status for {0} indexers; {1} changed", refreshed, changed);
        }

        private bool TryBeginRefresh()
        {
            lock (_refreshLock)
            {
                if (_refreshInProgress)
                {
                    return false;
                }

                _refreshInProgress = true;
                return true;
            }
        }

        private bool TryClaim(int indexerId, MyAnonaMouseSettings settings, bool force)
        {
            var now = UtcNow();

            lock (_refreshLock)
            {
                if (!force &&
                    _lastRefreshAttemptUtc.TryGetValue(indexerId, out var last) &&
                    now - last < Due(settings))
                {
                    return false;
                }

                _lastRefreshAttemptUtc[indexerId] = now;
                return true;
            }
        }

        private static TimeSpan Due(MyAnonaMouseSettings settings)
        {
            var interval = settings.ResolveAccountStatusRefreshInterval();
            return interval >= JitterFrom ? interval - SchedulerJitter : interval;
        }

        private void EndRefresh()
        {
            lock (_refreshLock)
            {
                _refreshInProgress = false;
            }
        }
    }
}
