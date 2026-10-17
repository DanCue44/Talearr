using System;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: a notice when Freeleech Wedges is Prefer Wedge but the account has no wedges left,
    // so grabs quietly download without one (and count against ratio). Uses the wedge balance
    // from the hourly account refresh; nothing is bought. English text on purpose (TaleArr-only).
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class TalearrMyAnonaMouseWedgeCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;

        public TalearrMyAnonaMouseWedgeCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
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

            return new HealthCheck(GetType(), HealthCheckResult.Notice, string.Join(" ", messages), "#mam-freeleech-wedges");
        }

        public static string GetMessage(string indexerName, MyAnonaMouseSettings settings, DateTime now)
        {
            if (settings == null ||
                settings.UseFreeleechWedge != (int)MyAnonaMouseFreeleechWedgeAction.Preferred ||
                settings.FreeleechWedges is not 0 ||
                !MamUnsatisfiedSlotGuard.HasFreshStatus(settings, now))
            {
                return null;
            }

            var points = settings.BonusPoints.HasValue
                ? $" You have {Math.Floor(settings.BonusPoints.Value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} bonus points; wedges can be bought on MAM's Bonus page."
                : string.Empty;

            return $"MAM '{indexerName}' has no freeleech wedges left: Prefer Wedge grabs download without one.{points}";
        }
    }
}
