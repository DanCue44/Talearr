using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // TaleArr: unabridged audiobooks by default. Abridged audiobook releases are rejected when
    // the grab policy is "unabridged only", unless the wanted edition is itself abridged; with
    // "abridged only" unabridged releases are rejected instead. A per-book override wins over the
    // global policy (TalearrAbridgedRules). "Prefer unabridged" only ranks (DownloadDecisionComparer).
    public class AbridgedReleaseSpecification : IDecisionEngineSpecification
    {
        public const string Category = "Abridged";

        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;
        private readonly Logger _logger;

        public AbridgedReleaseSpecification(Logger logger, ITalearrGrabPolicyService grabPolicyService = null, ITalearrAbridgedResolver abridgedResolver = null, ITalearrBookGrabPolicyService bookGrabPolicyService = null)
        {
            _grabPolicyService = grabPolicyService;
            _abridgedResolver = abridgedResolver;
            _bookGrabPolicyService = bookGrabPolicyService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var policy = TalearrGrabPolicyService.Resolve(_grabPolicyService);
            var target = TalearrAbridgedRules.Target(subject, policy.AbridgedPolicy, _bookGrabPolicyService, _abridgedResolver);
            var judgement = TalearrAbridgedRules.Judge(subject, target, _abridgedResolver);

            if (judgement.Accepted)
            {
                return Decision.Accept();
            }

            _logger.Debug("Release {0} rejected: {1}", subject.Release?.Title, judgement.Reason);
            return Decision.RejectSoftFilter(judgement.Reason, Category);
        }
    }
}
