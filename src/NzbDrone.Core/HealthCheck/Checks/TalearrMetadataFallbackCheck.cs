using System;
using System.Globalization;
using NzbDrone.Core.Localization;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: System > Status notice for the Open Library metadata fallback: which source search
    // and add use right now, and when the fallback was last used. English text on purpose, like
    // TalearrMetadataServerProbeCheck.
    [CheckOn(typeof(MetadataServerProbeStatusChangedEvent))]
    [CheckOn(typeof(TalearrMetadataFallbackUsedEvent))]
    public class TalearrMetadataFallbackCheck : HealthCheckBase
    {
        public static readonly TimeSpan RecentUseWindow = TimeSpan.FromHours(24);

        private readonly ITalearrMetadataFallback _fallback;

        public TalearrMetadataFallbackCheck(ITalearrMetadataFallback fallback, ILocalizationService localizationService)
            : base(localizationService)
        {
            _fallback = fallback;
        }

        public override HealthCheck Check()
        {
            var message = GetMessage(_fallback?.GetStatus(), DateTime.UtcNow);
            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Notice, message, "#metadata-fallback");
        }

        public static string GetMessage(TalearrMetadataFallbackStatus status, DateTime utcNow)
        {
            if (status == null || status.Mode == TalearrMetadataFallbackMode.Off)
            {
                return null;
            }

            var lastUsed = status.LastUsed.HasValue
                ? $" Last used {status.LastUsed.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC ({status.LastOperation})."
                : string.Empty;

            if (status.PrimaryUnavailable)
            {
                return $"Search and add are using Open Library while the metadata server is unavailable. Refresh is paused and existing authors and books are kept as they are.{lastUsed}";
            }

            if (status.LastUsed.HasValue && utcNow - status.LastUsed.Value < RecentUseWindow)
            {
                return $"The metadata server is back. Open Library was used as a fallback recently; books added from it are matched to the metadata server on their author's next refresh.{lastUsed}";
            }

            return null;
        }
    }
}
