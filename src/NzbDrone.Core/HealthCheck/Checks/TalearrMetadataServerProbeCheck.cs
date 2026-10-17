using System;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: System > Status warning from the active metadata server probe
    // (TalearrMetadataServerProbe). Re-runs whenever the probe state changes, so an outage
    // shows up within a minute or so instead of at the next 6-hourly health check.
    // English text on purpose: it is TaleArr-only and stays out of the shared translation files.
    [CheckOn(typeof(MetadataServerProbeStatusChangedEvent))]
    public class TalearrMetadataServerProbeCheck : HealthCheckBase
    {
        private readonly ITalearrMetadataServerProbe _probe;

        public TalearrMetadataServerProbeCheck(ITalearrMetadataServerProbe probe, ILocalizationService localizationService)
            : base(localizationService)
        {
            _probe = probe;
        }

        public override HealthCheck Check()
        {
            var result = _probe.LastResult;
            var message = GetMessage(result);

            if (message == null)
            {
                return new HealthCheck(GetType());
            }

            var level = result.State == MetadataServerProbeState.Down
                ? HealthCheckResult.Error
                : HealthCheckResult.Warning;

            return new HealthCheck(GetType(), level, message, "#metadata-server-unavailable");
        }

        public static string GetMessage(MetadataServerProbeResult result)
        {
            if (result == null || !result.IsProblem)
            {
                return null;
            }

            var source = result.SourceName ?? "The metadata server";
            var nextCheck = FormatNextCheck(result.NextProbeAt);

            switch (result.State)
            {
                case MetadataServerProbeState.Down:
                    return $"Metadata server {source} is not responding ({result.Error}). Your authors and books are kept as they are and refreshes are paused until it recovers. Searching and adding use Open Library while the metadata fallback is on (Settings > Metadata). {nextCheck}";
                case MetadataServerProbeState.Slow:
                    return $"Metadata server {source} is responding slowly ({result.Latency?.TotalSeconds ?? 0:0.0}s). Searching for, adding and refreshing books may time out. {nextCheck}";
                case MetadataServerProbeState.RateLimited:
                    return $"Metadata server {source} is rate limiting requests. Refreshes are paused and retried automatically. {nextCheck}";
                default:
                    return null;
            }
        }

        private static string FormatNextCheck(DateTime? nextProbeAt)
        {
            if (!nextProbeAt.HasValue)
            {
                return "TaleArr will check again shortly.";
            }

            var wait = nextProbeAt.Value - DateTime.UtcNow;
            if (wait < TimeSpan.FromSeconds(1))
            {
                return "TaleArr will check again shortly.";
            }

            return $"Next check in {MetadataServerHealthGate.FormatRetryAfter(wait)}.";
        }
    }
}
