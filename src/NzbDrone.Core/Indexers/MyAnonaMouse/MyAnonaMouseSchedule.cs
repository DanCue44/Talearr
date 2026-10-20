using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: scheduled task intervals that follow the MAM indexer settings, so a shorter Account
    // Status Refresh or Dynamic Seedbox Interval actually runs that often. The services still check
    // each indexer against its own interval; these only make the task tick often enough.
    public static class MyAnonaMouseSchedule
    {
        // The account status task used a fixed 60 minutes; with MAM indexers it ticks at the
        // shortest Account Status Refresh among them.
        public const int DefaultAccountStatusTaskMinutes = MyAnonaMouseSettings.DefaultAccountStatusRefreshInterval;

        public static int AccountStatusTaskMinutes(IEnumerable<IndexerDefinition> definitions)
        {
            var intervals = MamSettings(definitions)
                .Select(s => (int)s.ResolveAccountStatusRefreshInterval().TotalMinutes)
                .ToList();

            return intervals.Count == 0 ? DefaultAccountStatusTaskMinutes : intervals.Min();
        }

        // The seedbox task keeps its 5-minute tick (so a changed setting is picked up quickly)
        // and only ticks faster for an interval below 5 minutes.
        public static int DynamicSeedboxTaskMinutes(IEnumerable<IndexerDefinition> definitions)
        {
            var shortest = MamSettings(definitions)
                .Where(s => s.DynamicSeedboxEnabled)
                .Select(s => Math.Clamp(s.DynamicSeedboxInterval,
                    MyAnonaMouseSettings.MinimumDynamicSeedboxInterval,
                    MyAnonaMouseSettings.MaximumDynamicSeedboxInterval))
                .DefaultIfEmpty(MyAnonaMouseDynamicSeedboxService.SchedulerIntervalMinutes)
                .Min();

            return Math.Min(MyAnonaMouseDynamicSeedboxService.SchedulerIntervalMinutes, shortest);
        }

        private static IEnumerable<MyAnonaMouseSettings> MamSettings(IEnumerable<IndexerDefinition> definitions)
        {
            return (definitions ?? Enumerable.Empty<IndexerDefinition>())
                .Where(d => d != null && d.Enable)
                .Select(d => d.Settings as MyAnonaMouseSettings)
                .Where(s => s != null);
        }
    }
}
