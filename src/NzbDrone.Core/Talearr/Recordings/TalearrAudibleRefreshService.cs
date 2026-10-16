using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Recordings
{
    // TaleArr: runs RefreshAudibleRecordingFactsCommand.
    //
    // Scheduled run: picks the editions the Enrich setting covers, skips those whose cache rows
    // are still fresh, and works through at most MaxBooksPerRun books and MaxSearchesPerRun
    // searches, monitored editions first. Anything left is picked up by the next run. Nothing is
    // written to chaptarr.db.
    public class TalearrAudibleRefreshService : IExecute<RefreshAudibleRecordingFactsCommand>
    {
        public const int MaxBooksPerRun = 150;
        public const int MaxSearchesPerRun = 25;
        public static readonly TimeSpan PruneAfter = TimeSpan.FromDays(180);

        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IAuthorService _authorService;
        private readonly ITalearrAudibleFactsService _factsService;
        private readonly ITalearrRecordingsStore _store;
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly Logger _logger;

        public TalearrAudibleRefreshService(IBookService bookService,
                                            IEditionService editionService,
                                            IAuthorService authorService,
                                            ITalearrAudibleFactsService factsService,
                                            ITalearrRecordingsStore store,
                                            ITalearrRecordingsSettingsService settingsService,
                                            Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _authorService = authorService;
            _factsService = factsService;
            _store = store;
            _settingsService = settingsService;
            _logger = logger;
        }

        public static bool IsAudiobookEdition(Edition edition)
        {
            return edition != null && edition.ReadingFormatId == 2;
        }

        public void Execute(RefreshAudibleRecordingFactsCommand message)
        {
            var settings = TalearrRecordingsSettingsService.Resolve(_settingsService);
            if (settings.Provider == TalearrAudibleProvider.Off)
            {
                _logger.Debug("TaleArr: Audible lookups are off");
                return;
            }

            var onDemand = message.BookIds != null && message.BookIds.Count > 0;
            if (!onDemand && settings.Enrich == TalearrEnrichScope.Off)
            {
                _logger.Debug("TaleArr: background Audible enrichment is off");
                return;
            }

            var work = onDemand ? EditionsForBooks(message.BookIds) : EditionsForLibrary(settings.Enrich);
            var now = DateTime.UtcNow;

            if (!onDemand && !message.Force)
            {
                // Incremental: drop editions whose cache rows are still fresh before any network call.
                var keys = work.SelectMany(w => w.Editions).Select(TalearrEditionKey.For).Where(id => id != null);
                var fresh = _store.GetLinks(keys, settings.Marketplace)
                    .Where(l => !_factsService.NeedsRefresh(l, now))
                    .Select(l => l.ForeignEditionId)
                    .ToHashSet(StringComparer.Ordinal);

                work = work
                    .Select(w => new BookWork(w.Book, w.Editions.Where(e => !fresh.Contains(TalearrEditionKey.For(e) ?? string.Empty)).ToList(), w.Priority))
                    .Where(w => w.Editions.Count > 0)
                    .ToList();
            }

            var searchesLeft = MaxSearchesPerRun;
            var authorNames = new Dictionary<int, string>();
            var total = new AudibleResolveSummary();
            var processed = 0;

            foreach (var item in work.OrderBy(w => w.Priority).ThenBy(w => w.Book.Id).Take(onDemand ? int.MaxValue : MaxBooksPerRun))
            {
                var summary = _factsService.Resolve(item.Editions, item.Book.Title, AuthorName(item.Book, authorNames), new AudibleResolveOptions
                {
                    Force = message.Force,
                    SearchBudget = searchesLeft > 0 ? 1 : 0
                });

                processed++;
                searchesLeft -= summary.Searches;
                total.Editions += summary.Editions;
                total.Matched += summary.Matched;
                total.Unmatched += summary.Unmatched;
                total.Searches += summary.Searches;

                if (summary.Throttled)
                {
                    total.Throttled = true;
                    _logger.Info("TaleArr: Audible is throttling; the rest waits for the next run");
                    break;
                }
            }

            if (!onDemand)
            {
                _store.PruneOlderThan(now - PruneAfter);
            }

            _logger.Info("TaleArr: Audible facts ({0}): {1} books, {2} editions, {3} matched, {4} without a match, {5} searches, {6} books left for later",
                settings.Marketplace,
                processed,
                total.Editions,
                total.Matched,
                total.Unmatched,
                total.Searches,
                Math.Max(0, work.Count - processed));
        }

        private List<BookWork> EditionsForBooks(List<int> bookIds)
        {
            var books = _bookService.GetBooks(bookIds.Distinct()).Where(b => b != null && b.MediaType == BookMediaType.Audiobook).ToList();
            var editions = _editionService.GetEditionsByBook(books.Select(b => b.Id)).Where(IsAudiobookEdition).ToList();

            return books
                .Select(b => new BookWork(b, editions.Where(e => e.BookId == b.Id).ToList(), 0))
                .Where(w => w.Editions.Count > 0)
                .ToList();
        }

        private List<BookWork> EditionsForLibrary(TalearrEnrichScope scope)
        {
            List<Edition> editions;
            if (scope == TalearrEnrichScope.All)
            {
                var bookIds = _bookService.GetAllBooks()
                    .Where(b => b != null && b.MediaType == BookMediaType.Audiobook && b.Monitored && b.AudiobookMonitored)
                    .Select(b => b.Id)
                    .ToList();
                editions = _editionService.GetEditionsByBook(bookIds).Where(IsAudiobookEdition).ToList();
            }
            else
            {
                editions = _editionService.GetAllMonitoredEditions().Where(IsAudiobookEdition).ToList();
            }

            var books = _bookService.GetBooks(editions.Select(e => e.BookId).Distinct())
                .Where(b => b != null && b.MediaType == BookMediaType.Audiobook && b.Monitored)
                .ToDictionary(b => b.Id);

            return editions
                .Where(e => books.ContainsKey(e.BookId))
                .GroupBy(e => e.BookId)
                .Select(g => new BookWork(books[g.Key], g.ToList(), g.Any(e => e.Monitored) ? 0 : 1))
                .ToList();
        }

        private string AuthorName(Book book, Dictionary<int, string> cache)
        {
            if (book.AuthorId <= 0)
            {
                return null;
            }

            if (!cache.TryGetValue(book.AuthorId, out var name))
            {
                try
                {
                    name = _authorService.GetAuthor(book.AuthorId)?.Name;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "TaleArr: no author name for book {0}", book.Id);
                }

                cache[book.AuthorId] = name;
            }

            return name;
        }

        private sealed class BookWork
        {
            public BookWork(Book book, List<Edition> editions, int priority)
            {
                Book = book;
                Editions = editions;
                Priority = priority;
            }

            public Book Book { get; }
            public List<Edition> Editions { get; }
            public int Priority { get; }
        }
    }
}
