using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: warns when a private-tracker torrent may end as a hit and run (paused, queued,
    // errored, tracker error or removed before its seeding requirement). Health warnings also go
    // out through notifications with "On Health Issue". Re-checked after download processing but
    // the client is read at most every few minutes for this. The message only holds titles and
    // reasons (no running times) so a notification is sent when the problem changes, not every
    // minute. English text on purpose (TaleArr-only).
    [CheckOn(typeof(DownloadsProcessedEvent))]
    public class TalearrSeedingRiskCheck : HealthCheckBase
    {
        public const int MaxListed = 3;
        private static readonly TimeSpan MaxReportAge = TimeSpan.FromMinutes(5);

        private readonly ITalearrSeedingService _seedingService;
        private readonly Logger _logger;

        public TalearrSeedingRiskCheck(ITalearrSeedingService seedingService, ILocalizationService localizationService, Logger logger)
            : base(localizationService)
        {
            _seedingService = seedingService;
            _logger = logger;
        }

        public override HealthCheck Check()
        {
            TalearrSeedingReport report;

            try
            {
                report = _seedingService.GetReport(MaxReportAge);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Seeding risk check skipped");
                return new HealthCheck(GetType());
            }

            var message = GetMessage(report?.Torrents);

            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#seeding-status");
        }

        public static string GetMessage(IEnumerable<TalearrSeedingTorrent> torrents)
        {
            var risky = (torrents ?? Enumerable.Empty<TalearrSeedingTorrent>())
                .Where(t => t.AtRisk && t.IsPrivateTracker == true)
                .OrderBy(t => t.DownloadId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (risky.Count == 0)
            {
                return null;
            }

            var listed = risky
                .Take(MaxListed)
                .Select(t => $"'{t.BookTitle ?? t.Title ?? t.DownloadId}' ({t.RiskReason})");

            var more = risky.Count > MaxListed ? $" and {risky.Count - MaxListed} more" : string.Empty;
            var noun = risky.Count == 1 ? "torrent is" : "torrents are";

            return $"{risky.Count} private-tracker {noun} at risk of a hit and run: {string.Join(", ", listed)}{more}. See Activity > Seeding.";
        }
    }
}
