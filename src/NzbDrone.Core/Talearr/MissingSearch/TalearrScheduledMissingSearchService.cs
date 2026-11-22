using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Talearr.MissingSearch
{
    // Talearr: the scheduled missing-books search. Each run searches up to BooksPerRun monitored
    // missing books, waiting StaggerSeconds between books so indexers are not hammered. Books not
    // searched since they were released come first, newest release first; the rest follow, the
    // ones searched longest ago first (Book.LastSearchTime, which every indexer search already
    // records, so no new storage is needed).
    //
    // The release-day search (SearchOnReleaseDay, off by default) runs hourly and searches each
    // monitored missing book once on or after its release day: a book whose release date has
    // passed in the last ReleaseDayWindowDays and that has not been searched since. The search
    // records LastSearchTime, which is what makes it once.
    //
    // It is never a user-invoked search, even when started from System > Tasks: delay profiles,
    // the bundle last-resort rules and every other grab protection apply, unlike Wanted >
    // Search All Missing, which is user-invoked and skips delay profiles.
    public class TalearrScheduledMissingSearchService : IExecute<ScheduledMissingBookSearchCommand>, IExecute<ReleaseDayBookSearchCommand>
    {
        private readonly ITalearrMissingSearchSettingsService _settingsService;
        private readonly IBookService _bookService;
        private readonly ISearchForReleases _releaseSearchService;
        private readonly IProcessDownloadDecisions _processDownloadDecisions;
        private readonly IQueueService _queueService;
        private readonly Logger _logger;

        public TalearrScheduledMissingSearchService(ITalearrMissingSearchSettingsService settingsService,
                                                    IBookService bookService,
                                                    ISearchForReleases releaseSearchService,
                                                    IProcessDownloadDecisions processDownloadDecisions,
                                                    IQueueService queueService,
                                                    Logger logger)
        {
            _settingsService = settingsService;
            _bookService = bookService;
            _releaseSearchService = releaseSearchService;
            _processDownloadDecisions = processDownloadDecisions;
            _queueService = queueService;
            _logger = logger;
        }

        // Replaced in tests so the stagger does not really wait.
        internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;

        public void Execute(ScheduledMissingBookSearchCommand message)
        {
            RunAsync(message?.Trigger ?? CommandTrigger.Unspecified).GetAwaiter().GetResult();
        }

        public void Execute(ReleaseDayBookSearchCommand message)
        {
            RunReleaseDayAsync(message?.Trigger ?? CommandTrigger.Unspecified, DateTime.UtcNow).GetAwaiter().GetResult();
        }

        internal async Task<int> RunReleaseDayAsync(CommandTrigger trigger, DateTime now)
        {
            var settings = TalearrMissingSearchSettingsService.Resolve(_settingsService);

            if (!settings.SearchOnReleaseDay && trigger == CommandTrigger.Scheduled)
            {
                _logger.Debug("Talearr: release day search is off");
                return 0;
            }

            var books = SelectReleaseDayBooks(now, settings.EffectiveBooksPerRun);
            if (books.Count == 0)
            {
                _logger.Debug("Talearr: release day search found no newly released missing books");
                return 0;
            }

            _logger.ProgressInfo("Talearr: release day search for {0} newly released book(s)", books.Count);
            var grabbed = await SearchBooksAsync(books, settings.EffectiveStaggerSeconds, "release day search");
            _logger.ProgressInfo("Talearr: release day search completed for {0} book(s). {1} report(s) downloaded.", books.Count, grabbed);
            return grabbed;
        }

        internal async Task<int> RunAsync(CommandTrigger trigger)
        {
            var settings = TalearrMissingSearchSettingsService.Resolve(_settingsService);

            if (settings.Schedule == MissingSearchSchedule.Off && trigger == CommandTrigger.Scheduled)
            {
                _logger.Debug("Talearr: scheduled missing search is off");
                return 0;
            }

            var books = SelectBooks(settings.EffectiveBooksPerRun);
            if (books.Count == 0)
            {
                _logger.ProgressInfo("Talearr: scheduled missing search found no monitored missing books to search");
                return 0;
            }

            _logger.ProgressInfo("Talearr: scheduled missing search for {0} book(s), {1}s apart", books.Count, settings.EffectiveStaggerSeconds);

            var grabbed = await SearchBooksAsync(books, settings.EffectiveStaggerSeconds, "scheduled missing search");

            _logger.ProgressInfo("Talearr: scheduled missing search completed for {0} book(s). {1} report(s) downloaded.", books.Count, grabbed);
            return grabbed;
        }

        private async Task<int> SearchBooksAsync(List<Book> books, int staggerSeconds, string label)
        {
            var grabbed = 0;
            for (var index = 0; index < books.Count; index++)
            {
                if (index > 0 && staggerSeconds > 0)
                {
                    await Delay(TimeSpan.FromSeconds(staggerSeconds));
                }

                var book = books[index];

                try
                {
                    // userInvokedSearch: false, so delay profiles and grab protections apply.
                    var decisions = await _releaseSearchService.BookSearch(book, false, false, false);
                    var processed = await _processDownloadDecisions.ProcessDecisions(decisions);
                    grabbed += processed?.Grabbed?.Count ?? 0;

                    // Approved by the decision engine but not sent: say so instead of a silent 0.
                    var approved = decisions.Count(d => d.Approved);
                    if (approved > 0 && (processed?.Grabbed?.Count ?? 0) == 0)
                    {
                        _logger.Info("Talearr: {4} for [{0}]: {1} approved release(s) not grabbed ({2} pending, {3} rejected at grab time)",
                            book, approved, processed?.Pending?.Count ?? 0, processed?.Rejected?.Count ?? 0, label);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Talearr: {0} failed for book [{1}]", label, book);
                }
            }

            return grabbed;
        }

        // Monitored books without files, not already in the download queue. Books not searched
        // since their release come first, newest release first, then the rest oldest searched first.
        internal List<Book> SelectBooks(int limit)
        {
            return GetCandidateBooks()
                .OrderBy(book => SearchedSinceRelease(book) ? 1 : 0)
                .ThenByDescending(book => SearchedSinceRelease(book) ? DateTime.MinValue : book.ReleaseDate ?? DateTime.MinValue)
                .ThenBy(book => book.LastSearchTime ?? DateTime.MinValue)
                .ThenBy(book => book.Id)
                .Take(limit)
                .ToList();
        }

        // Released in the last ReleaseDayWindowDays and not searched since, newest release first.
        internal List<Book> SelectReleaseDayBooks(DateTime now, int limit)
        {
            var earliest = now.AddDays(-TalearrMissingSearchSettings.ReleaseDayWindowDays);

            return GetCandidateBooks()
                .Where(book => book.ReleaseDate.HasValue &&
                               book.ReleaseDate.Value <= now &&
                               book.ReleaseDate.Value >= earliest &&
                               !SearchedSinceRelease(book))
                .OrderByDescending(book => book.ReleaseDate.Value)
                .ThenBy(book => book.Id)
                .Take(limit)
                .ToList();
        }

        private static bool SearchedSinceRelease(Book book)
        {
            if (!book.LastSearchTime.HasValue)
            {
                return false;
            }

            return !book.ReleaseDate.HasValue || book.LastSearchTime.Value >= book.ReleaseDate.Value;
        }

        private List<Book> GetCandidateBooks()
        {
            var targetIds = _bookService.GetMissingBookSearchTargets(null, null)
                .Select(target => target.BookId)
                .Where(id => id > 0)
                .Distinct()
                .ToList();

            if (targetIds.Count == 0)
            {
                return new List<Book>();
            }

            targetIds = targetIds.Except(GetQueuedBookIds()).ToList();

            return _bookService.GetExistingBooks(targetIds)
                .Where(book => book != null)
                .ToList();
        }

        private HashSet<int> GetQueuedBookIds()
        {
            var ids = new HashSet<int>();

            try
            {
                foreach (var item in _queueService.GetQueue() ?? new List<Queue.Queue>())
                {
                    if (item?.Book?.Id > 0)
                    {
                        ids.Add(item.Book.Id);
                    }

                    foreach (var book in item?.RemoteBook?.Books ?? new List<Book>())
                    {
                        if (book?.Id > 0)
                        {
                            ids.Add(book.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: unable to read the queue before the scheduled missing search");
            }

            return ids;
        }
    }
}
