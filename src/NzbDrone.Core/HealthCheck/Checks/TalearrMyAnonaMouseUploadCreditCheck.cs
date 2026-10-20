using System;
using System.Globalization;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: a warning when the hourly account refresh says MAM demoted the account to Mouse or
    // made it seed-only (ratio too low). Points at the upload credit that fixes it, and says whether
    // the Ratio Safety Net can buy it. English text on purpose (Talearr-only).
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class TalearrMyAnonaMouseUploadCreditCheck : HealthCheckBase
    {
        public const string Advice = "Buy upload credit in the Bonus Points Store (API minimum 50 GB = 25,000 points).";

        private readonly IIndexerFactory _indexerFactory;

        public TalearrMyAnonaMouseUploadCreditCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            var now = DateTime.UtcNow;
            var messages = _indexerFactory.All()
                .Where(d => d.Enable && d.Settings is MyAnonaMouseSettings)
                .Select(d => GetMessage(d.Name, (MyAnonaMouseSettings)d.Settings, now))
                .Where(m => m != null)
                .ToList();

            if (messages.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(), HealthCheckResult.Warning, string.Join(" ", messages), "#mam-bonus-auto-spend");
        }

        public static string GetMessage(string indexerName, MyAnonaMouseSettings settings, DateTime now)
        {
            if (settings == null ||
                !MyAnonaMouseBonusSpendRules.IsRestricted(settings) ||
                !MamUnsatisfiedSlotGuard.HasFreshStatus(settings, now))
            {
                return null;
            }

            var state = settings.SeedOnly == true ? "seed-only" : "demoted to Mouse";
            var message = $"MAM '{indexerName}' is {state} (ratio too low). {Advice}";

            if (settings.BonusPoints.HasValue)
            {
                message += $" You have {Math.Floor(settings.BonusPoints.Value).ToString("N0", CultureInfo.InvariantCulture)} bonus points.";
            }

            if (settings.BonusRatioSafetyNet)
            {
                var cost = MyAnonaMouseBonusSpendRules.UploadCost(Math.Max(MyAnonaMouseBonusSpendRules.MinimumUploadGiB, settings.BonusUploadGiB));
                message += settings.BonusDryRun
                    ? " Ratio Safety Net is in dry run, so nothing is bought."
                    : $" Ratio Safety Net buys it once the balance covers {cost.ToString("N0", CultureInfo.InvariantCulture)} points above the reserve.";
            }

            return message;
        }
    }
}
