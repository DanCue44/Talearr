using System.Linq;
using NLog;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    public class MultiBookReleaseSpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;
        private readonly ITalearrGrabPolicyService _grabPolicyService;

        public MultiBookReleaseSpecification(Logger logger, ITalearrGrabPolicyService grabPolicyService = null)
        {
            _logger = logger;
            _grabPolicyService = grabPolicyService;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var detection = subject?.PackDetection;
            if (detection == null)
            {
                return Decision.Accept();
            }

            switch (detection.Verdict)
            {
                case ReleasePackDetectionVerdict.MultipleBooks:
                    // TaleArr: bundles are opt-in, and a wanted omnibus is not a stray bundle.
                    if (IsWantedOmnibus(subject))
                    {
                        _logger.Trace("Release {0} looks like a pack but the wanted book is itself an omnibus", subject.Release?.Title);
                        return Decision.Accept();
                    }

                    if (TalearrGrabPolicyService.Resolve(_grabPolicyService).AllowBundles)
                    {
                        _logger.Debug("Release {0} looks like a multi-book bundle ({1}: {2}); allowed by grab policy",
                            subject.Release?.Title,
                            detection.PackType,
                            detection.MatchedValue);
                        return Decision.Accept();
                    }

                    _logger.Trace("Release {0} rejected as multi-book pack. Type={1}, Match={2}",
                        subject.Release?.Title,
                        detection.PackType,
                        detection.MatchedValue);
                    return Decision.RejectHardFilter("Release appears to contain multiple books", "Pack");

                case ReleasePackDetectionVerdict.AudiobookFragment:
                    _logger.Trace("Release {0} soft-rejected as audiobook fragment. Type={1}, Match={2}",
                        subject.Release?.Title,
                        detection.PackType,
                        detection.MatchedValue);
                    return Decision.RejectSoftFilter("Release may be one disc/part of a larger audiobook", "Pack");

                default:
                    return Decision.Accept();
            }
        }

        internal static bool IsWantedOmnibus(RemoteBook subject)
        {
            var books = subject?.Books?.Where(book => book != null).ToList();

            return books != null &&
                   books.Count > 0 &&
                   books.All(book => TalearrBundleDetector.LooksLikeBundleTitle(book.Title));
        }
    }
}
