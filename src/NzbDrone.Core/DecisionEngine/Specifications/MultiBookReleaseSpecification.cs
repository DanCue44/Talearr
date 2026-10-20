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
                    // Talearr: a wanted omnibus is not a stray bundle.
                    if (IsWantedOmnibus(subject))
                    {
                        _logger.Trace("Release {0} looks like a pack but the wanted book is itself an omnibus", subject.Release?.Title);
                        return Decision.Accept();
                    }

                    return EvaluateBundle(subject,
                        _grabPolicyService,
                        _logger,
                        string.Format("{0}: {1}", detection.PackType, detection.MatchedValue),
                        "Release appears to contain multiple books");

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

        // Talearr: with "Allow bundles" on a bundle is a normal release. Otherwise it is kept only
        // as a last resort (flagged, so every other check still runs and the grab waits for the
        // single copies), and rejected where no last resort applies (RSS, pushed releases, an
        // automatic search whose bundle does not name the wanted book).
        internal static Decision EvaluateBundle(RemoteBook subject, ITalearrGrabPolicyService grabPolicyService, Logger logger, string detail, string rejectionReason)
        {
            subject.IsBundle = true;

            if (TalearrGrabPolicyService.Resolve(grabPolicyService).AllowBundles)
            {
                subject.IsBundleFallback = false;
                logger.Debug("Release {0} looks like a multi-book bundle ({1}); allowed by grab policy", subject.Release?.Title, detail);
                return Decision.Accept();
            }

            if (TalearrBundleFallback.CanFallBack(subject))
            {
                subject.IsBundleFallback = true;
                logger.Debug("Release {0} looks like a multi-book bundle ({1}); kept as a last resort", subject.Release?.Title, detail);
                return Decision.Accept();
            }

            subject.IsBundleFallback = false;
            logger.Trace("Release {0} rejected as multi-book pack ({1})", subject.Release?.Title, detail);
            return Decision.RejectHardFilter(rejectionReason, "Pack");
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
