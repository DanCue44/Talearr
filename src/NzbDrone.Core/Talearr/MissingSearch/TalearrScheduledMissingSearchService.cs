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
    // TaleArr: the scheduled missing-books search. Each run searches up to BooksPerRun monitored
    // missing books, the ones searched longest ago first (Book.LastSearchTime, which every
    // indexer search already records, so no new storage is needed), waiting StaggerSeconds
    // between books so indexers are not hammered.
    //
    // It is never a user-invoked search, even when started from System > Tasks: delay profiles,
    // the bundle last-resort rules and every other grab protection apply, unlike Wanted >
    // Search All Missing, which is user-invoked and skips delay profiles.
    public class TalearrScheduledMissingSearchService : IExecute<ScheduledMissingBookSearchCommand>
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

        internal async Task<int> RunAsync(CommandTrigger trigger)
        {
            var settings = TalearrMissingSearchSettingsService.Resolve(_settingsService);

            if (settings.Schedule == MissingSearchSchedule.Off && trigger == CommandTrigger.Scheduled)
            {
                _logger.Debug("TaleArr: scheduled missing search is off");
                return 0;
            }

            var books = SelectBooks(settings.EffectiveBooksPerRun);
            if (books.Count == 0)
            {
                _logger.ProgressInfo("TaleArr: scheduled missing search found no monitored missing books to search");
                return 0;
            }

            _logger.ProgressInfo("TaleArr: scheduled missing search for {0} book(s), {1}s apart", books.Count, settings.EffectiveStaggerSeconds);

            var grabbed = 0;
            for (var index = 0; index < books.Count; index++)
            {
                if (index > 0 && settings.EffectiveStaggerSeconds > 0)
                {
                    await Delay(TimeSpan.FromSeconds(settings.EffectiveStaggerSeconds));
                }

                var book = books[index];

                try
                {
                    // userInvokedSearch: false, so delay profiles and grab protections apply.
                    var decisions = await _releaseSearchService.BookSearch(book, false, false, false);
                    var processed = await _processDownloadDecisions.ProcessDecisions(decisions);
                    grabbed += processed?.Grabbed?.Count ?? 0;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "TaleArr: scheduled missing search failed for book [{0}]", book);
                }
            }

            _logger.ProgressInfo("TaleArr: scheduled missing search completed for {0} book(s). {1} report(s) downloaded.", books.Count, grabbed);
            return grabbed;
        }

        // Monitored books without files, not already in the download queue, oldest searched first.
        internal List<Book> SelectBooks(int limit)
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
                .OrderBy(book => book.LastSearchTime ?? DateTime.MinValue)
                .ThenBy(book => book.Id)
                .Take(limit)
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
                _logger.Debug(ex, "TaleArr: unable to read the queue before the scheduled missing search");
            }

            return ids;
        }
    }
}
