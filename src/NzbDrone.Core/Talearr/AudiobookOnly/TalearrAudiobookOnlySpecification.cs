using System;
using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.AudiobookOnly
{
    // Talearr: with Audiobook Only on, automatic grabs (RSS and the missing searches) skip a
    // release when every book it is for is hidden: an ebook row, or an audiobook row with no
    // known audiobook edition. A soft filter, so Interactive Search shows the reason and can
    // still grab it.
    public class TalearrAudiobookOnlySpecification : IDecisionEngineSpecification
    {
        public const string Category = "Audiobook Only";

        private readonly ITalearrAudiobookOnlyService _audiobookOnly;
        private readonly Logger _logger;

        public TalearrAudiobookOnlySpecification(Logger logger, ITalearrAudiobookOnlyService audiobookOnly = null)
        {
            _audiobookOnly = audiobookOnly;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_audiobookOnly == null || subject?.Books == null || subject.Books.Count == 0)
            {
                return Decision.Accept();
            }

            try
            {
                if (!_audiobookOnly.Enabled)
                {
                    return Decision.Accept();
                }

                var ids = subject.Books.Where(b => b != null && b.Id > 0).Select(b => b.Id).Distinct().ToList();
                if (ids.Count == 0)
                {
                    return Decision.Accept();
                }

                var hidden = _audiobookOnly.HiddenBookIds(ids);
                if (ids.Any(id => !hidden.Contains(id)))
                {
                    return Decision.Accept();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: Audiobook Only check failed for release {0}", subject.Release?.Title);
                return Decision.Accept();
            }

            const string reason = "Audiobook Only: no audiobook edition is known for this book yet";
            _logger.Debug("Release {0} rejected: {1}", subject.Release?.Title, reason);
            return Decision.RejectSoftFilter(reason, Category);
        }
    }
}
