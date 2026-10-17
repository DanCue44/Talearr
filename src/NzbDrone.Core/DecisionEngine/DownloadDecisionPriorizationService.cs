using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.QualityLadder;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.DecisionEngine
{
    public interface IPrioritizeDownloadDecision
    {
        List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions);
    }

    public class DownloadDecisionPriorizationService : IPrioritizeDownloadDecision
    {
        private readonly IConfigService _configService;
        private readonly IDelayProfileService _delayProfileService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly Logger _logger;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;
        private readonly ITalearrQualityLadder _qualityLadder; // TaleArr

        public DownloadDecisionPriorizationService(IConfigService configService, IDelayProfileService delayProfileService, IQualityProfileService qualityProfileService, Logger logger, ITalearrGrabPolicyService grabPolicyService = null, ITalearrAbridgedResolver abridgedResolver = null, ITalearrBookGrabPolicyService bookGrabPolicyService = null, ITalearrQualityLadder qualityLadder = null)
        {
            _qualityLadder = qualityLadder;
            _abridgedResolver = abridgedResolver;
            _bookGrabPolicyService = bookGrabPolicyService;
            _configService = configService;
            _delayProfileService = delayProfileService;
            _qualityProfileService = qualityProfileService;
            _logger = logger;
            _grabPolicyService = grabPolicyService;
        }

        public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
        {
            var comparerLogger = LogManager.GetLogger("DownloadDecisionComparer");
            var comparer = new DownloadDecisionComparer(_configService, _delayProfileService, _qualityProfileService, comparerLogger, TalearrGrabPolicyService.Resolve(_grabPolicyService), _abridgedResolver, _bookGrabPolicyService, _qualityLadder);

            var acceptedDecisions = decisions.Where(c => c.Approved).ToList();
            _logger.Debug("PrioritizeDecisions - Total decisions: {0}, Accepted decisions: {1}", decisions.Count, acceptedDecisions.Count);

            foreach (var decision in acceptedDecisions)
            {
                _logger.Debug("Accepted decision: {0} ({1})", decision.RemoteBook.Release.Title, decision.RemoteBook.Release.Size.SizeSuffix());
            }

            var result = acceptedDecisions
                            .GroupBy(c => c.RemoteBook.Author.Id, (authorId, downloadDecisions) =>
                                {
                                    var sortedDecisions = downloadDecisions.OrderByDescending(decision => decision, comparer).ToList();

                                    _logger.Debug("Sorted decisions for author {0}: {1} decisions", authorId, sortedDecisions.Count);
                                    for (var i = 0; i < sortedDecisions.Count; i++)
                                    {
                                        _logger.Debug("  [{0}] {1} ({2})", i, sortedDecisions[i].RemoteBook.Release.Title, sortedDecisions[i].RemoteBook.Release.Size.SizeSuffix());
                                    }

                                    // The first (best) decision gets auto-pick reasons
                                    if (sortedDecisions.Count > 1)
                                    {
                                        var winner = sortedDecisions[0];
                                        var runnerUp = sortedDecisions[1];
                                        _logger.Debug("Calling CompareWithReasons - Winner: {0} ({1}), Runner-up: {2} ({3})",
                                            winner.RemoteBook.Release.Title,
                                            winner.RemoteBook.Release.Size.SizeSuffix(),
                                            runnerUp.RemoteBook.Release.Title,
                                            runnerUp.RemoteBook.Release.Size.SizeSuffix());
                                        var comparisonResult = comparer.CompareWithReasons(winner, runnerUp);
                                        winner.AutoPickReasons = comparisonResult.Reasons;
                                    }
                                    else if (sortedDecisions.Count == 1)
                                    {
                                        // Only one decision, it wins by default
                                        sortedDecisions[0].AutoPickReasons = new List<DownloadDecisionReason>
                                        {
                                            new DownloadDecisionReason("Default", "Only available release", "No other releases to compare against")
                                        };
                                    }

                                    return sortedDecisions;
                                })
                            .SelectMany(c => c)
                            .Union(decisions.Where(c => !c.Approved))
                            .ToList();

            return result;
        }
    }
}
