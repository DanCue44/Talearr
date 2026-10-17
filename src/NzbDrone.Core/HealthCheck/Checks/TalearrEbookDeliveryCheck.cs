using System;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.EbookDelivery;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: System > Status warning when ebook delivery is on but cannot work as set: Send to
    // Kindle without an address or SMTP server, or OPDS KEPUB downloads without kepubify.
    // English text on purpose: it is TaleArr-only and stays out of the shared translation files.
    [CheckOn(typeof(TalearrKindleSettingsChangedEvent))]
    [CheckOn(typeof(TalearrOpdsSettingsChangedEvent))]
    public class TalearrEbookDeliveryCheck : HealthCheckBase
    {
        private readonly ITalearrKindleSettingsService _kindleSettings;
        private readonly ITalearrKindleService _kindleService;
        private readonly ITalearrOpdsSettingsService _opdsSettings;
        private readonly ITalearrKepubify _kepubify;

        public TalearrEbookDeliveryCheck(ITalearrKindleSettingsService kindleSettings,
                                         ITalearrKindleService kindleService,
                                         ITalearrOpdsSettingsService opdsSettings,
                                         ITalearrKepubify kepubify,
                                         ILocalizationService localizationService)
            : base(localizationService)
        {
            _kindleSettings = kindleSettings;
            _kindleService = kindleService;
            _opdsSettings = opdsSettings;
            _kepubify = kepubify;
        }

        public override HealthCheck Check()
        {
            var message = GetMessage(_kindleSettings.Get(),
                _kindleService.ServerError,
                _opdsSettings.Get(),
                () => _kepubify.FindExecutable() != null);

            return message == null
                ? new HealthCheck(GetType())
                : new HealthCheck(GetType(), HealthCheckResult.Warning, message, "#ebook-delivery");
        }

        public static string GetMessage(TalearrKindleSettings kindle,
                                        Func<TalearrKindleSettings, string> serverError,
                                        TalearrOpdsSettings opds,
                                        Func<bool> kepubifyInstalled)
        {
            if (kindle?.Enabled == true)
            {
                if (TalearrKindleSettings.CleanAddresses(kindle.KindleAddresses).Count == 0)
                {
                    return "Send to Kindle is on but no Kindle address is set, so nothing is emailed. Add one in Settings > Conversion.";
                }

                var error = serverError?.Invoke(kindle);
                if (error != null)
                {
                    return "Send to Kindle cannot send: " + error;
                }
            }

            if (opds?.Enabled == true && opds.OfferKepub && !kepubifyInstalled())
            {
                return "OPDS: KEPUB downloads are on but kepubify was not found on PATH, so only the original files are offered. Install kepubify in the container or turn the option off.";
            }

            return null;
        }
    }
}
