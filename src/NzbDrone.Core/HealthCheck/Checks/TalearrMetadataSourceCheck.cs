using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status warning for the talearr.metadata.source setting. Talearr's source
    // without a Hardcover token has no effect, so that is a warning. A working setup is not a health
    // problem and stays silent (Settings > Metadata explains what each source does). English text on
    // purpose, like TalearrMetadataFallbackCheck.
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
            var source = _settings?.GetSource() ?? TalearrMetadataSourceSettingsService.DefaultSource;
            var tokenSet = _hardcover?.IsConfigured == true;
            var message = GetMessage(source, tokenSet);
            if (message == null)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#metadata-source");
        }

        public static string GetMessage(TalearrMetadataSource source, bool hardcoverTokenSet)
        {
            if (source != TalearrMetadataSource.Talearr || hardcoverTokenSet)
            {
                return null;
            }

            return "Metadata source is set to Talearr (Hardcover), but no Hardcover API token is saved, so Talearr still uses the metadata server. " +
                   "Set the token in Settings > Metadata.";
        }
    }
}
