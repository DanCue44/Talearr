using System.Collections.Generic;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    public interface IComicMamRules
    {
        // Why a MAM release may not be grabbed for this account (Freeleech Only, VIP-only, seed-only
        // or ratio, unsatisfied torrent slots). Empty for releases from other indexers.
        List<string> GetRejections(ReleaseInfo release);

        // Grab time: the standing check again and a slot reservation, as book grabs do in
        // DownloadService. Null when the grab may go ahead.
        string Reserve(RemoteBook remoteBook);
    }

    // Talearr: comic grabs skip the book decision engine and DownloadService, so MAM's account
    // rules are applied here with the same specifications and guards books use.
    public class ComicMamRules : IComicMamRules
    {
        private readonly IMyAnonaMouseStandingGuard _standingGuard;
        private readonly IMamUnsatisfiedSlotGuard _slotGuard;
        private readonly List<IDecisionEngineSpecification> _specifications;

        public ComicMamRules(IMyAnonaMouseReleasePolicy releasePolicy,
                             IMyAnonaMouseStandingGuard standingGuard,
                             IMamUnsatisfiedSlotGuard slotGuard,
                             IIndexerFactory indexerFactory,
                             Logger logger)
        {
            _standingGuard = standingGuard;
            _slotGuard = slotGuard;
            _specifications = new List<IDecisionEngineSpecification>
            {
                new MyAnonaMouseFreeleechOnlySpecification(releasePolicy, logger),
                new MyAnonaMouseVipAccessSpecification(indexerFactory, logger),
                new MyAnonaMouseAccountStandingSpecification(standingGuard, logger),
                new MyAnonaMouseUnsatisfiedSlotSpecification(slotGuard, logger)
            };
        }

        public List<string> GetRejections(ReleaseInfo release)
        {
            var rejections = new List<string>();
            if (!IsMamRelease(release))
            {
                return rejections;
            }

            var remoteBook = new RemoteBook { Release = release, IsComic = true };
            foreach (var specification in _specifications)
            {
                var decision = specification.IsSatisfiedBy(remoteBook, null);
                if (!decision.Accepted && !string.IsNullOrWhiteSpace(decision.Reason) && !rejections.Contains(decision.Reason))
                {
                    rejections.Add(decision.Reason);
                }
            }

            return rejections;
        }

        public string Reserve(RemoteBook remoteBook)
        {
            if (!IsMamRelease(remoteBook?.Release))
            {
                return null;
            }

            var standing = _standingGuard.GetRejection(remoteBook);
            if (standing != null)
            {
                return standing;
            }

            var availability = _slotGuard.TryReserve(remoteBook);
            return availability.Accepted ? null : availability.Reason;
        }

        public static bool IsMamRelease(ReleaseInfo release)
        {
            return release != null &&
                   ((release.Guid?.StartsWith("MAM-", System.StringComparison.OrdinalIgnoreCase) ?? false) ||
                    MyAnonaMouseReleasePolicy.IsMamIndexerName(release.Indexer));
        }
    }
}
