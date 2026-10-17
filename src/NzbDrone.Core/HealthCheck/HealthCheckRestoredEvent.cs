using NzbDrone.Common.Messaging;

namespace NzbDrone.Core.HealthCheck
{
    // TaleArr: a check that was failing now passes (Sonarr/Radarr "On Health Restored").
    public class HealthCheckRestoredEvent : IEvent
    {
        public HealthCheck PreviousCheck { get; private set; }
        public bool IsInStartupGracePeriod { get; private set; }

        public HealthCheckRestoredEvent(HealthCheck previousCheck, bool isInStartupGracePeriod)
        {
            PreviousCheck = previousCheck;
            IsInStartupGracePeriod = isInStartupGracePeriod;
        }
    }
}
