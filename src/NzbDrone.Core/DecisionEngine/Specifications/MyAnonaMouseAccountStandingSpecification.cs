using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // Talearr: rejects MAM releases while the account is seed-only, and non-freeleech MAM releases
    // that would push the share ratio below the indexer's Minimum Ratio After Grab (or while the
    // account status is unknown). See MyAnonaMouseAccountStanding.
    public class MyAnonaMouseAccountStandingSpecification : IDecisionEngineSpecification
    {
        private readonly IMyAnonaMouseStandingGuard _guard;
        private readonly Logger _logger;

        public MyAnonaMouseAccountStandingSpecification(IMyAnonaMouseStandingGuard guard, Logger logger)
        {
            _guard = guard;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var reason = _guard.GetRejection(subject);
            if (reason == null)
            {
                return Decision.Accept();
            }

            _logger.Debug("Rejecting MAM release '{0}': {1}", subject.Release.Title, reason);
            return Decision.Reject(reason);
        }
    }
}
