using System.IO;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: System > Status warning when the reader hand-off is on but cannot work as set:
    // the watch folder is missing, or Convert to KEPUB is on without kepubify installed.
    // English text on purpose: it is TaleArr-only and stays out of the shared translation files.
    [CheckOn(typeof(TalearrReaderHandoffSettingsChangedEvent))]
    public class TalearrReaderHandoffCheck : HealthCheckBase
    {
        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrKepubify _kepubify;

        public TalearrReaderHandoffCheck(ITalearrReaderHandoffSettingsService settingsService,
                                         ITalearrKepubify kepubify,
                                         ILocalizationService localizationService)
            : base(localizationService)
        {
            _settingsService = settingsService;
            _kepubify = kepubify;
        }

        public override HealthCheck Check()
        {
            var message = GetMessage(_settingsService.Get(), Directory.Exists, () => _kepubify.FindExecutable() != null);

            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#reader-hand-off");
        }

        public static string GetMessage(TalearrReaderHandoffSettings settings, System.Func<string, bool> folderExists, System.Func<bool> kepubifyInstalled)
        {
            if (settings == null || !settings.IsActive)
            {
                return null;
            }

            if (!folderExists(settings.Path.Trim()))
            {
                return $"Reader hand-off folder '{settings.Path}' does not exist, so imported ebooks are not copied to it. Check the path and the container's volume mappings in Settings > Conversion.";
            }

            if (settings.ConvertToKepub && !kepubifyInstalled())
            {
                return "Reader hand-off: Convert to KEPUB is on but kepubify was not found on PATH, so EPUBs are copied unconverted. Install kepubify in the container or turn the option off.";
            }

            return null;
        }
    }
}
