using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // TaleArr: error while the MAM account is seed-only (class Mouse or a seed-only flag from the
    // account refresh). Every MAM grab is blocked until MAM lifts it. English text on purpose.
    [CheckOn(typeof(ProviderUpdatedEvent<IIndexer>))]
    [CheckOn(typeof(ProviderDeletedEvent<IIndexer>))]
    public class TalearrMyAnonaMouseSeedOnlyCheck : HealthCheckBase
    {
        private readonly IIndexerFactory _indexerFactory;

        public TalearrMyAnonaMouseSeedOnlyCheck(IIndexerFactory indexerFactory, ILocalizationService localizationService)
            : base(localizationService)
        {
            _indexerFactory = indexerFactory;
        }

        public override HealthCheck Check()
        {
            var seedOnly = _indexerFactory.All()
                .Where(d => d.Enable && d.Settings is MyAnonaMouseSettings settings && MyAnonaMouseAccountStanding.IsSeedOnly(settings))
                .Select(d => d.Name)
                .ToList();

            if (seedOnly.Count == 0)
            {
                return new HealthCheck(GetType());
            }

            return new HealthCheck(GetType(),
                HealthCheckResult.Error,
                $"{MyAnonaMouseAccountStanding.SeedOnlyMessage}. TaleArr makes no MAM grabs until the account is out of seed-only mode ({string.Join(", ", seedOnly)}).",
                "#mam-seed-only");
        }
    }
}
