using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Audible did not answer for an author set to Audible and no earlier read is stored. The
    // refresh is skipped instead of dropping the books only Audible lists.
    public class TalearrAudibleUnavailableException : TalearrHardcoverException
    {
        public TalearrAudibleUnavailableException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public interface ITalearrAudibleFill
    {
        // Adds the author's Audible catalog to a payload from Talearr's source (TalearrAudibleMerge).
        TalearrAudibleMergeStats Apply(V5AuthorResponse response);
    }

    // Talearr: the Audible step of Talearr's metadata source. Authors set to Audible always get
    // it, the others while the fill is on or when they have audiobooks added from Audible
    // (TalearrAudibleSourceSettings).
    public class TalearrAudibleFill : ITalearrAudibleFill
    {
        private readonly ITalearrAudibleSourceSettings _settings;
        private readonly ITalearrAudibleAuthorCatalog _catalog;
        private readonly Logger _logger;

        public TalearrAudibleFill(ITalearrAudibleSourceSettings settings, ITalearrAudibleAuthorCatalog catalog, Logger logger)
        {
            _settings = settings;
            _catalog = catalog;
            _logger = logger;
        }

        public TalearrAudibleMergeStats Apply(V5AuthorResponse response)
        {
            var author = response?.Author;
            if (author == null || string.IsNullOrWhiteSpace(author.Id))
            {
                return new TalearrAudibleMergeStats();
            }

            var leads = _settings.GetAuthorSource(author.Id) == TalearrAudiobookSource.Audible;
            var kept = leads ? new List<string>() : _settings.GetKeptBooks(author.Id).ToList();
            var fill = _settings.FillEnabled;
            if (!leads && !fill && kept.Count == 0)
            {
                return new TalearrAudibleMergeStats();
            }

            var names = new List<string> { author.Name };
            names.AddRange(author.Pseudonyms ?? new List<string>());

            var catalog = _catalog.Get(author.Id, names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList());
            if (catalog == null)
            {
                if (leads)
                {
                    throw new TalearrAudibleUnavailableException("Audible did not answer for {0}; refresh skipped until it does", author.Name);
                }

                _logger.Debug("[TALEARR-AUDIBLE] Audible did not answer for {0}; Hardcover's details only", author.Name);
                return new TalearrAudibleMergeStats();
            }

            // With the fill off, only the books added from Audible are used.
            var items = leads || fill ? catalog.Items : catalog.Items.Where(i => kept.Contains(i.Asin, StringComparer.OrdinalIgnoreCase)).ToList();
            var stats = TalearrAudibleMerge.Apply(response, items, leads, catalog.Marketplace, kept);
            _logger.Debug("[TALEARR-AUDIBLE] {0} ({1}, {2} Audible audiobooks{3}): {4}",
                author.Name, leads ? "Audible leads" : "fill", catalog.Items.Count, catalog.Stale ? ", stored copy" : string.Empty, stats);

            return stats;
        }
    }
}
