using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: warns before MAM's unsatisfied-torrent limit is reached. The grab guard
    // (MamUnsatisfiedSlotGuard) already stops grabs at the limit; this makes the pause visible
    // and warns early when protection is off. Re-runs after every account status refresh,
    // which saves the indexer definition. English text on purpose (TaleArr-only).
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class TalearrMyAnonaMouseUnsatisfiedCheck : HealthCheckBase
    {
        // Warn once this share of the limit is used, or when automatic grabs are paused.
        public const double NearLimitRatio = 0.8;

        private readonly IIndexerFactory _indexerFactory;

        public TalearrMyAnonaMouseUnsatisfiedCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            var now = DateTime.UtcNow;
            var messages = _indexerFactory.All()
                .Where(d => d.Enable && d.Settings is MyAnonaMouseSettings)
                .Select(d => GetMessage(d.Name, (MyAnonaMouseSettings)d.Settings, now))
                .Where(m => m != null)
                .ToList();

            if (messages.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(), HealthCheckResult.Warning, string.Join(" ", messages), "#mam-unsatisfied-limit");
        }

        public static string GetMessage(string indexerName, MyAnonaMouseSettings settings, DateTime now)
        {
            if (settings == null || !MamUnsatisfiedSlotGuard.HasFreshStatus(settings, now))
            {
                return null;
            }

            var count = settings.UnsatisfiedCount.Value;
            var limit = settings.UnsatisfiedLimit.Value;
            var reserved = Math.Max(0, settings.UnsatisfiedSlotReserve) + Math.Max(0, settings.ManualGrabBuffer);
            var automaticPaused = settings.ProtectUnsatisfiedSlots && count >= Math.Max(0, limit - reserved);
            var nearLimit = count >= Math.Ceiling(limit * NearLimitRatio);

            if (!automaticPaused && !nearLimit)
            {
                return null;
            }

            var prefix = $"MAM '{indexerName}' has {count} of {limit} unsatisfied torrents";

            if (automaticPaused)
            {
                return $"{prefix}: automatic MAM grabs are paused until torrents finish seeding (safety reserve and user buffer: {reserved} slot(s)).";
            }

            if (!settings.ProtectUnsatisfiedSlots)
            {
                return $"{prefix} and Protect Unsatisfied Slots is off: going over the limit triggers MAM's 24-hour download suspension.";
            }

            return $"{prefix}: approaching the limit. Keep torrents seeding for 72 hours to free slots.";
        }
    }
}
