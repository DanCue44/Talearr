using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.DecisionEngine.Specifications
{
    // Talearr: a book whose copy is in a read-only library folder already counts as present, and
    // that copy is never replaced, so an upgrade release would only be downloaded and then refused
    // at import. Books with no file there are not affected.
    public class TalearrReadOnlyLibrarySpecification : IDecisionEngineSpecification
    {
        private readonly Logger _logger;

        public TalearrReadOnlyLibrarySpecification(Logger logger)
        {
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (subject?.Books == null || ReadOnlyPathGuard.Roots.Count == 0)
            {
                return Decision.Accept();
            }

            foreach (var book in subject.Books.Where(b => b != null))
            {
                var files = book.BookFiles;
                if (files != null && files.Any() && files.All(f => ReadOnlyPathGuard.IsReadOnly(f.Path)))
                {
                    _logger.Debug("'{0}' is in a read-only library folder; not grabbing a replacement", book.Title);
                    return Decision.Reject("Your copy is in a read-only library folder, so Talearr does not replace it");
                }
            }

            return Decision.Accept();
        }
    }
}
