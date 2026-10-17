using NLog;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // TaleArr: MAM "Freeleech Only". Rejects MAM releases that are not global, personal or VIP
    // freeleech for this user. Off by default; applies to native and Prowlarr MAM results.
    public class MyAnonaMouseFreeleechOnlySpecification : IDecisionEngineSpecification
    {
        private readonly IMyAnonaMouseReleasePolicy _releasePolicy;
        private readonly Logger _logger;

        public MyAnonaMouseFreeleechOnlySpecification(IMyAnonaMouseReleasePolicy releasePolicy, Logger logger)
        {
            _releasePolicy = releasePolicy;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var release = subject?.Release;
            if (release == null || MyAnonaMouseReleasePolicy.IsFreeleech(release))
            {
                return Decision.Accept();
            }

            var settings = _releasePolicy.ResolveSettings(release);
            if (settings is not { FreeleechOnly: true })
            {
                return Decision.Accept();
            }

            _logger.Debug("Rejecting MAM release '{0}': Freeleech Only is on and the release is not freeleech", release.Title);
            return Decision.Reject("MAM Freeleech Only: release is not freeleech");
        }
    }
}
