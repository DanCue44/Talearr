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
                // TaleArr: bundles are opt-in through the grab policy.
                if (TalearrGrabPolicyService.Resolve(_grabPolicyService).AllowBundles ||
                    MultiBookReleaseSpecification.IsWantedOmnibus(subject))
                {
                    return Decision.Accept();
                }

                _logger.Trace("Discography release {0} rejected as a multi-book pack.", subject.Release?.Title);
                return Decision.RejectHardFilter("Release appears to contain multiple books (discography)", "Pack");
            }

            return Decision.Accept();
        }
    }
}
