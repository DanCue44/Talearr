using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status notice for the talearr.metadata.source setting. In Phase 3a the
    // Talearr (Hardcover) source runs in shadow mode only, so choosing it changes nothing yet;
    // this notice says so. Silent while the default (the metadata server) is selected. English
    // text on purpose, like TalearrMetadataFallbackCheck.
    [CheckOn(typeof(TalearrMetadataSourceSavedEvent))]
    public class TalearrMetadataSourceCheck : HealthCheckBase
    {
        private readonly ITalearrMetadataSourceSettingsService _settings;
        private readonly ITalearrHardcoverMetadataClient _hardcover;

        public TalearrMetadataSourceCheck(ITalearrMetadataSourceSettingsService settings,
                                          ITalearrHardcoverMetadataClient hardcover,
                                          ILocalizationService localizationService)
            : base(localizationService)
        {
            _settings = settings;
            _hardcover = hardcover;
        }

        public override HealthCheck Check()
        {
            var message = GetMessage(_settings?.GetSource() ?? TalearrMetadataSourceSettingsService.DefaultSource, _hardcover?.IsConfigured == true);
            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Notice, message, "#metadata-source");
        }

        public static string GetMessage(TalearrMetadataSource source, bool hardcoverTokenSet)
        {
            if (source != TalearrMetadataSource.Talearr)
            {
                return null;
            }

            var token = hardcoverTokenSet
                ? string.Empty
                : " Set a Hardcover API token in Settings > Metadata first; the comparison needs it.";

            return "Metadata source is set to Talearr (Hardcover), which is in shadow mode: refresh, add and import matching still use the metadata server. " +
                   "Run the TalearrMetadataShadowCompare command to see the differences." + token;
        }
    }
}
