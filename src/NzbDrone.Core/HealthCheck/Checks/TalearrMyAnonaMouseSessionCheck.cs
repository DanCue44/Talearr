using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status messages for MAM sessions: an expired or revoked mam_id, and a
    // dynamic seedbox update MAM refused. English text on purpose: Talearr-only, kept out of
    // the shared translation files.
    [CheckOn(typeof(MyAnonaMouseSessionStatusChangedEvent))]
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class TalearrMyAnonaMouseSessionCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMyAnonaMouseSessionMonitor _sessionMonitor;

        public TalearrMyAnonaMouseSessionCheck(IIndexerFactory indexerFactory, IMyAnonaMouseSessionMonitor sessionMonitor, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
            _sessionMonitor = sessionMonitor;
        }

        public override HealthCheck Check()
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (var definition in _indexerFactory.All().Where(d => d.Enable && d.Settings is MyAnonaMouseSettings))
            {
                var settings = (MyAnonaMouseSettings)definition.Settings;
                var health = _sessionMonitor.Get(definition.Id);

                var sessionMessage = GetSessionMessage(definition.Name, settings, health);
                if (sessionMessage != null)
                {
                    errors.Add(sessionMessage);
                }

                var seedboxMessage = GetSeedboxMessage(definition.Name, settings, health);
                if (seedboxMessage != null)
                {
                    warnings.Add(seedboxMessage);
                }
            }

            if (errors.Count > 0)
            {
                return new HealthCheck(GetType(), HealthCheckResult.Error, string.Join(" ", errors.Concat(warnings)), "#mam-session");
            }

            if (warnings.Count > 0)
            {
                return new HealthCheck(GetType(), HealthCheckResult.Warning, string.Join(" ", warnings), "#mam-dynamic-seedbox");
            }

            return new HealthCheck(GetType());
        }

        public static string GetSessionMessage(string indexerName, MyAnonaMouseSettings settings, MyAnonaMouseSessionHealth health)
        {
            if (health?.RejectedMamId == null ||
                settings == null ||
                !string.Equals(settings.MamId, health.RejectedMamId, System.StringComparison.Ordinal))
            {
                return null;
            }

            return $"{MyAnonaMouse.SessionExpiredMessage} (indexer '{indexerName}'). Create a session under Preferences > Security on MAM and paste its mam_id into the indexer settings.";
        }

        public static string GetSeedboxMessage(string indexerName, MyAnonaMouseSettings settings, MyAnonaMouseSessionHealth health)
        {
            var result = health?.Seedbox;
            if (settings == null || !settings.DynamicSeedboxEnabled || result == null || !result.IsProblem)
            {
                return null;
            }

            // A seedbox mam_id that has since been replaced is no longer the one at fault.
            var currentSeedboxMamId = string.IsNullOrWhiteSpace(settings.SeedboxMamId) ? settings.MamId : settings.SeedboxMamId;
            if (!string.IsNullOrWhiteSpace(health.SeedboxMamId) &&
                !string.Equals(currentSeedboxMamId, health.SeedboxMamId, System.StringComparison.Ordinal))
            {
                return null;
            }

            var prefix = $"MAM dynamic seedbox update failed for '{indexerName}'";

            switch (result.Outcome)
            {
                case MyAnonaMouseSeedboxOutcome.InvalidSession:
                    return $"{prefix}: the session is invalid or expired ({result.Message}). Paste a new mam_id (or Seedbox mam_id).";
                case MyAnonaMouseSeedboxOutcome.NetworkMismatch:
                    return $"{prefix}: MAM saw a different IP or ASN than the session allows ({result.Message}). The request must leave from the torrent client's VPN IP: check Seedbox Proxy, or recreate the session from the VPN IP.";
                case MyAnonaMouseSeedboxOutcome.NotAllowed:
                    return $"{prefix}: this session may not set a dynamic seedbox IP ({result.Message}). Create a session on MAM with 'Allow session to set dynamic seedbox' checked.";
                default:
                    return $"{prefix}: {result.Message}.";
            }
        }
    }
}
