using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // TaleArr: unabridged audiobooks by default. Abridged audiobook releases are rejected when
    // the grab policy is "unabridged only", unless the wanted edition is itself abridged.
    // "Prefer unabridged" only ranks them lower (DownloadDecisionComparer).
    public class AbridgedReleaseSpecification : IDecisionEngineSpecification
    {
        public const string Category = "Abridged";

        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly Logger _logger;

        public AbridgedReleaseSpecification(Logger logger, ITalearrGrabPolicyService grabPolicyService = null)
        {
            _grabPolicyService = grabPolicyService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var policy = TalearrGrabPolicyService.Resolve(_grabPolicyService);

            if (policy.AbridgedPolicy != AbridgedAudiobookPolicy.UnabridgedOnly ||
                !AbridgedReleaseDetector.IsAbridgedRelease(subject) ||
                !AbridgedReleaseDetector.IsAudiobookRelease(subject))
            {
                return Decision.Accept();
            }

            if (AbridgedReleaseDetector.WantsAbridged(subject))
            {
                _logger.Trace("Abridged release {0} accepted because the wanted edition is abridged", subject.Release?.Title);
                return Decision.Accept();
            }

            _logger.Debug("Abridged release {0} rejected (grab policy: unabridged only)", subject.Release?.Title);
            return Decision.RejectSoftFilter("Abridged audiobook (grab policy is unabridged only)", Category);
        }
    }
}
