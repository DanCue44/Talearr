using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    // TaleArr: optional bitrate floor for audiobook releases (talearr.ladder.minaudiokbps, off by
    // default). Only an estimate that is known counts: a "24kbps" title, or a size that is too
    // small for the book's known runtime. A soft filter, so interactive search can still grab it.
    public class TalearrMinimumBitrateSpecification : IDecisionEngineSpecification
    {
        public const string Category = "Bitrate";

        private readonly ITalearrQualityLadder _ladder;
        private readonly Logger _logger;

        public TalearrMinimumBitrateSpecification(ITalearrQualityLadder ladder, Logger logger)
        {
            _ladder = ladder;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var minimum = _ladder?.GetSettings().MinimumAudioKbps ?? 0;
            if (minimum <= 0 || subject?.Release == null)
            {
                return Decision.Accept();
            }

            var estimate = _ladder.EstimateRelease(subject);
            if (estimate.Tier == AudioQualityTier.Implausible)
            {
                _logger.Debug("TaleArr: '{0}' is implausibly small for an audiobook", subject.Release.Title);
                return Decision.RejectSoftFilter($"TaleArr: audio is implausibly small for the book ({estimate.Describe()})", Category);
            }

            if (estimate.Kbps > 0 && estimate.Kbps < minimum)
            {
                return Decision.RejectSoftFilter($"TaleArr: audio bitrate {estimate.Describe()} is below the {minimum} kbps minimum", Category);
            }

            return Decision.Accept();
        }
    }
}
