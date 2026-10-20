using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status notice for the talearr.metadata.source setting. With Talearr's
    // source selected, authors and books with Hardcover ids come from Hardcover; import matching
    // and books known only by other ids still use the metadata server, and this notice says so.
    // Without a Hardcover token the setting has no effect (a warning). Silent while the default
    // (the metadata server) is selected. English text on purpose, like TalearrMetadataFallbackCheck.
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

            return new HealthCheck(GetType(), tokenSet ? HealthCheckResult.Notice : HealthCheckResult.Warning, message, "#metadata-source");
        }

        public static string GetMessage(TalearrMetadataSource source, bool hardcoverTokenSet)
        {
            if (source != TalearrMetadataSource.Talearr)
            {
                return null;
            }

            if (!hardcoverTokenSet)
            {
                return "Metadata source is set to Talearr (Hardcover), but no Hardcover API token is saved, so Talearr still uses the metadata server. " +
                       "Set the token in Settings > Metadata.";
            }

            return "Metadata source is Talearr (Hardcover): authors and books with Hardcover ids are added and refreshed from Hardcover. " +
                   "Import matching and books known only by other ids still use the metadata server.";
        }
    }
}
