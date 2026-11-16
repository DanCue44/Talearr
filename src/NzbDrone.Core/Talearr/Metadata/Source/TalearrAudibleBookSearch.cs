using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public sealed class TalearrAudibleSearchHit
    {
        // The work's lead listing (TalearrAudibleMerge.GroupWorks).
        public AudibleCatalogItem Item { get; set; }
        public string Title { get; set; }

        // "hc:123": the author the audiobook is added to.
        public string HardcoverAuthorId { get; set; }
        public string AuthorName { get; set; }
    }

    public interface ITalearrAudibleBookSearch
    {
        // Audible audiobooks for a search term that the Hardcover results do not have for the
        // same author. Each is placed under an author with a Hardcover id: one in the library,
        // else one of the Hardcover results. Empty when Audible does not answer.
        List<TalearrAudibleSearchHit> Search(string term, IReadOnlyCollection<object> hardcoverResults);

        // The library author with a Hardcover id that an Audible author name belongs to (its name,
        // an alias, or the same name with or without middle names), or null when there is none or
        // more than one. Only HardcoverAuthorId and AuthorName are set.
        TalearrAudibleSearchHit FindLibraryAuthor(IEnumerable<string> audibleAuthorNames);
    }

    // Talearr: the Audible part of the add search while Talearr's metadata source is on. Hardcover
    // does not list every audiobook under every author (Gaining the Edge, Symbiote Wars #3, only
    // under its co-author); Audible's own search finds it, and adding it keeps it in the author's
    // catalog (TalearrAudibleSourceSettings.KeepBook, AddBookService).
    public class TalearrAudibleBookSearch : ITalearrAudibleBookSearch
    {
        public const int MaxHits = 10;

        private readonly ITalearrAudibleClient _client;
        private readonly ITalearrRecordingsSettingsService _recordingsSettings;
        private readonly IAuthorService _authorService;
        private readonly Logger _logger;

        public TalearrAudibleBookSearch(ITalearrAudibleClient client,
                                        ITalearrRecordingsSettingsService recordingsSettings,
                                        IAuthorService authorService,
                                        Logger logger)
        {
            _client = client;
            _recordingsSettings = recordingsSettings;
            _authorService = authorService;
            _logger = logger;
        }

        public List<TalearrAudibleSearchHit> Search(string term, IReadOnlyCollection<object> hardcoverResults)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return new List<TalearrAudibleSearchHit>();
            }

            var marketplace = TalearrRecordingsSettingsService.Resolve(_recordingsSettings).Marketplace;
            AudibleCatalogPage page;
            try
            {
                page = _client.SearchKeywords(marketplace, term);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[TALEARR-AUDIBLE] Audible search for '{0}' failed", term);
                return new List<TalearrAudibleSearchHit>();
            }

            if (page == null || !page.Answered || page.Items.Count == 0)
            {
                return new List<TalearrAudibleSearchHit>();
            }

            var results = hardcoverResults ?? Array.Empty<object>();
            var authors = KnownAuthors(results);
            var language = TalearrAudibleAuthorCatalog.MarketplaceLanguage(marketplace);
            var wanted = new HashSet<string>(authors.Keys, StringComparer.Ordinal);
            var hardcoverBooks = results.OfType<Book>().Where(b => !string.IsNullOrWhiteSpace(b?.Title)).ToList();

            return TalearrAudibleMerge.GroupWorks(page.Items.Where(i => TalearrAudibleAuthorCatalog.IsAuthorsBook(i, wanted, language)))
                .Where(w => !w.IsCollection)
                .Select(w => Hit(w.Lead, w.FullTitle, authors))
                .Where(h => h != null && !HardcoverHas(hardcoverBooks, h))
                .Take(MaxHits)
                .ToList();
        }

        public TalearrAudibleSearchHit FindLibraryAuthor(IEnumerable<string> audibleAuthorNames)
        {
            var names = audibleAuthorNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();
            if (names.Count == 0)
            {
                return null;
            }

            return Hit(new AudibleCatalogItem { Authors = names }, null, KnownAuthors(Array.Empty<object>()));
        }

        private static TalearrAudibleSearchHit Hit(AudibleCatalogItem lead, string title, Dictionary<string, (string Id, string Name)> authors)
        {
            // Audible lists the lead author first.
            foreach (var name in lead.Authors)
            {
                var author = TalearrAuthorNameMatch.FindUnique(TalearrSourceGapFill.Normalize(name), authors, a => a.Id);
                if (author.HasValue)
                {
                    return new TalearrAudibleSearchHit
                    {
                        Item = lead,
                        Title = title,
                        HardcoverAuthorId = author.Value.Id,
                        AuthorName = author.Value.Name
                    };
                }
            }

            return null;
        }

        // A Hardcover result of the same work under the same author makes the Audible one redundant.
        private static bool HardcoverHas(List<Book> hardcoverBooks, TalearrAudibleSearchHit hit)
        {
            var author = TalearrSourceGapFill.Normalize(hit.AuthorName);
            return hardcoverBooks.Any(b =>
                TalearrTitleNormalizer.IsSameWorkTitle(b.Title, hit.Title) &&
                (string.Equals(b.Author?.HardcoverAuthorId, hit.HardcoverAuthorId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(TalearrSourceGapFill.Normalize(b.Author?.Name), author, StringComparison.Ordinal)));
        }

        // Normalized name to Hardcover author: library authors first, then the Hardcover results.
        private Dictionary<string, (string Id, string Name)> KnownAuthors(IEnumerable<object> hardcoverResults)
        {
            var authors = new Dictionary<string, (string Id, string Name)>(StringComparer.Ordinal);

            void Add(Author author)
            {
                var id = author?.HardcoverAuthorId?.Trim();
                if (string.IsNullOrEmpty(id) || !id.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // The author's other names (Hardcover's alternate names) find it too.
                foreach (var other in new[] { author.Name }.Concat(author.Aliases ?? new List<string>()))
                {
                    var name = TalearrSourceGapFill.Normalize(other);
                    if (name.Length > 0 && !authors.ContainsKey(name))
                    {
                        authors[name] = (id, author.Name);
                    }
                }
            }

            try
            {
                foreach (var author in _authorService.GetAllAuthors() ?? new List<Author>())
                {
                    Add(author);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "[TALEARR-AUDIBLE] Unable to read library authors for the Audible search");
            }

            foreach (var result in hardcoverResults)
            {
                Add(result as Author ?? (result as Book)?.Author);
            }

            return authors;
        }
    }
}
