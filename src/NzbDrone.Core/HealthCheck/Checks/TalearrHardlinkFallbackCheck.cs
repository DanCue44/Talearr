using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Import;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: System > Status warning when imports keep asking for a hardlink and getting a full
    // copy, which means the download and library folders cannot share files (see README, Hardlinks).
    // English text on purpose: it is TaleArr-only and stays out of the shared translation files.
    [CheckOn(typeof(TalearrHardlinkFallbackChangedEvent))]
    public class TalearrHardlinkFallbackCheck : HealthCheckBase
    {
        private readonly ITalearrHardlinkMonitor _monitor;

        public TalearrHardlinkFallbackCheck(ITalearrHardlinkMonitor monitor, ILocalizationService localizationService)
            : base(localizationService)
        {
            _monitor = monitor;
        }

        public override HealthCheck Check()
        {
            var message = TalearrHardlinkMonitor.GetMessage(_monitor.GetStatus());

            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#hardlinks");
        }
    }
}
