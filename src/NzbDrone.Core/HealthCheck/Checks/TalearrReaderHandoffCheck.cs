using System.IO;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status warning when the reader hand-off is on but cannot work as set:
    // the watch folder is missing, or Convert to KEPUB is on without kepubify installed.
    // English text on purpose: it is Talearr-only and stays out of the shared translation files.
    [CheckOn(typeof(TalearrReaderHandoffSettingsChangedEvent))]
    [CheckOn(typeof(TalearrReaderHandoffDeliveriesChangedEvent))]
    public class TalearrReaderHandoffCheck : HealthCheckBase
    {
        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrKepubify _kepubify;
        private readonly ITalearrReaderHandoffStore _store;

        public TalearrReaderHandoffCheck(ITalearrReaderHandoffSettingsService settingsService,
                                         ITalearrKepubify kepubify,
                                         ILocalizationService localizationService,
                                         ITalearrReaderHandoffStore store = null)
            : base(localizationService)
        {
            _settingsService = settingsService;
            _kepubify = kepubify;
            _store = store;
        }

        public override HealthCheck Check()
        {
            var settings = _settingsService.Get();
            var message = GetMessage(settings, Directory.Exists, () => _kepubify.FindExecutable() != null) ??
                          GetFailedMessage(settings, _store?.GetFailed().Count ?? 0);

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

        public static string GetFailedMessage(TalearrReaderHandoffSettings settings, int failedCount)
        {
            if (settings == null || !settings.IsActive || failedCount <= 0)
            {
                return null;
            }

            return failedCount == 1
                ? "Reader hand-off: 1 ebook could not be copied to the reader folder. Retry it in Settings > Conversion."
                : $"Reader hand-off: {failedCount} ebooks could not be copied to the reader folder. Retry them in Settings > Conversion.";
        }
    }
}
