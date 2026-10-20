using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class DiscographySpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly ITalearrGrabPolicyService _grabPolicyService;

        public DiscographySpecification(Logger logger, ITalearrGrabPolicyService grabPolicyService = null)
        {
            _logger = logger;
            _grabPolicyService = grabPolicyService;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public virtual Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.ParsedBookInfo?.Discography == true)
            {
                // Talearr: a wanted omnibus is not a stray bundle; other discographies follow the
                // same last-resort rule as bundles.
                if (MultiBookReleaseSpecification.IsWantedOmnibus(subject))
                {
                    return Decision.Accept();
                }

                return MultiBookReleaseSpecification.EvaluateBundle(subject,
                    _grabPolicyService,
                    _logger,
                    "discography",
                    "Release appears to contain multiple books (discography)");
            }

            return Decision.Accept();
        }
    }
}
