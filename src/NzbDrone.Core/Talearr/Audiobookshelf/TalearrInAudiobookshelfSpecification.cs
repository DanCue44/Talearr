using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // Talearr: skips a release when every book it is for is already in Audiobookshelf
    // (docs/TALEARR_AUDIOBOOKSHELF.md). Off per connection with "Skip Books Already in
    // Audiobookshelf". A soft filter: interactive search shows the release as "In Audiobookshelf"
    // and can still grab it. Talearr's own imports and rows with Talearr files never count, so
    // upgrades and the recordings upgrade guard work as before.
    public class TalearrInAudiobookshelfSpecification : IDecisionEngineSpecification
    {
        public const string Category = "In Audiobookshelf";

        private readonly ITalearrAbsLibraryService _absLibrary;
        private readonly Logger _logger;

        public TalearrInAudiobookshelfSpecification(Logger logger, ITalearrAbsLibraryService absLibrary = null)
        {
            _absLibrary = absLibrary;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_absLibrary == null || subject == null)
            {
                return Decision.Accept();
            }

            List<Books.Book> books;
            try
            {
                books = subject.GetBooksMatchingReleaseMediaType()?.Where(b => b != null).ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not read the books of release {0}", subject.Release?.Title);
                return Decision.Accept();
            }

            if (books == null || books.Count == 0)
            {
                return Decision.Accept();
            }

            var owned = new List<TalearrAbsOwnedCopy>();
            foreach (var book in books)
            {
                TalearrAbsOwnedCopy copy;
                try
                {
                    copy = _absLibrary.FindOwnedCopy(book);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: Audiobookshelf check failed for book {0}", book.Id);
                    return Decision.Accept();
                }

                if (copy == null)
                {
                    return Decision.Accept();
                }

                owned.Add(copy);
            }

            var reason = string.Join("; ", owned.Select(Describe));
            _logger.Debug("Release {0} rejected: {1}", subject.Release?.Title, reason);
            return Decision.RejectSoftFilter(reason, Category);
        }

        public static string Describe(TalearrAbsOwnedCopy copy)
        {
            var item = copy.Item;
            var details = new List<string>();

            if (!string.IsNullOrWhiteSpace(item.Narrators) && item.HasAudio)
            {
                details.Add(item.Narrators);
            }

            if (copy.Abridged == AbridgedMarker.Abridged)
            {
                details.Add("abridged");
            }
            else if (copy.Abridged == AbridgedMarker.Unabridged)
            {
                details.Add("unabridged");
            }

            var suffix = details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty;
            return $"Already in Audiobookshelf ({copy.Connection?.Name ?? "Audiobookshelf"}): {item.Title}{suffix}";
        }
    }
}
