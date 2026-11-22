using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Repositories;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.MissingSearch;

namespace Chaptarr.Core.Test.Talearr.MissingSearch
{
    // Talearr: scheduled missing-books search (off by default, never user-invoked).
    [TestFixture]
    public class ScheduledMissingSearchFixture
    {
        private Dictionary<string, string> _config;
        private List<object> _events;
        private TalearrMissingSearchSettingsService _settings;
        private List<Book> _books;
        private HashSet<int> _queued;
        private RecordingSearch _search;
        private List<TimeSpan> _delays;
        private TalearrScheduledMissingSearchService _subject;

        [SetUp]
        public void SetUp()
        {
            _config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _events = new List<object>();

            var (repository, repositoryFake) = InterfaceFake<IConfigRepository>.Create();
            repositoryFake
                .On(nameof(IConfigRepository.Get), args => _config.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _config[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            var (events, eventsFake) = InterfaceFake<IEventAggregator>.Create();
            eventsFake.On(nameof(IEventAggregator.PublishEvent), args =>
            {
                _events.Add(args[0]);
                return null;
            });

            _settings = new TalearrMissingSearchSettingsService(repository, events, LogManager.GetCurrentClassLogger());

            var now = DateTime.UtcNow;
            _books = Enumerable.Range(1, 30)
                .Select(id => new Book
                {
                    Id = id,
                    AuthorId = 1,
                    Title = "Book " + id,
                    LastSearchTime = id <= 5 ? null : now.AddDays(-id)
                })
                .ToList();
            _queued = new HashSet<int>();

            var (bookService, bookFake) = InterfaceFake<IBookService>.Create();
            bookFake
                .On(nameof(IBookService.GetMissingBookSearchTargets), args => _books.Select(b => new BookSearchTarget { BookId = b.Id, AuthorId = b.AuthorId }).ToList())
                .On(nameof(IBookService.GetExistingBooks), args => ((IEnumerable<int>)args[0]).Select(id => _books.Single(b => b.Id == id)).ToList());

            var (queue, queueFake) = InterfaceFake<IQueueService>.Create();
            queueFake.On(nameof(IQueueService.GetQueue), args => _queued.Select(id => new NzbDrone.Core.Queue.Queue { Book = new Book { Id = id } }).ToList());

            _search = new RecordingSearch();
            _delays = new List<TimeSpan>();

            _subject = new TalearrScheduledMissingSearchService(_settings, bookService, _search, new NoGrabs(), queue, LogManager.GetCurrentClassLogger())
            {
                Delay = span =>
                {
                    _delays.Add(span);
                    return Task.CompletedTask;
                }
            };
        }

        [Test]
        public void should_be_off_by_default_with_safe_defaults()
        {
            var settings = _settings.Get();

            Assert.That(settings.Schedule, Is.EqualTo(MissingSearchSchedule.Off));
            Assert.That(settings.BooksPerRun, Is.EqualTo(20));
            Assert.That(settings.StaggerSeconds, Is.EqualTo(30));
            Assert.That(settings.IntervalMinutes, Is.EqualTo(0));
        }

        [Test]
        public void scheduled_trigger_should_do_nothing_while_off()
        {
            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            Assert.That(_search.Books, Is.Empty);
        }

        [Test]
        public void should_search_never_searched_then_oldest_searched_books_first_up_to_the_limit()
        {
            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly, BooksPerRun = 8, StaggerSeconds = 30 });

            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            // 1-5 were never searched; then 30, 29, 28 were searched longest ago.
            Assert.That(_search.Books.Select(b => b.Id), Is.EqualTo(new[] { 1, 2, 3, 4, 5, 30, 29, 28 }));
        }

        [Test]
        public void should_never_be_a_user_invoked_search_so_delay_profiles_apply()
        {
            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Daily, BooksPerRun = 3 });

            _subject.RunAsync(CommandTrigger.Manual).GetAwaiter().GetResult();

            Assert.That(_search.UserInvoked, Is.All.False);
            Assert.That(_search.Interactive, Is.All.False);
            Assert.That(_search.Books.Count, Is.EqualTo(3));
        }

        [Test]
        public void should_wait_between_books_but_not_before_the_first()
        {
            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Daily, BooksPerRun = 4, StaggerSeconds = 45 });

            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            Assert.That(_delays, Is.EqualTo(Enumerable.Repeat(TimeSpan.FromSeconds(45), 3).ToList()));
        }

        [Test]
        public void should_skip_books_already_in_the_queue()
        {
            _queued.UnionWith(new[] { 1, 2 });
            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly, BooksPerRun = 3 });

            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            Assert.That(_search.Books.Select(b => b.Id), Is.EqualTo(new[] { 3, 4, 5 }));
        }

        [Test]
        public void should_keep_going_when_one_search_fails()
        {
            _search.FailFor = 2;
            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly, BooksPerRun = 3 });

            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            Assert.That(_search.Books.Select(b => b.Id), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        [Test]
        public void should_search_books_not_searched_since_release_first_newest_release_first()
        {
            var now = DateTime.UtcNow;

            // 10 and 12 were searched before their release (pre-order search on add), 20 was never searched.
            _books.Single(b => b.Id == 10).ReleaseDate = now.AddDays(-30);
            _books.Single(b => b.Id == 10).LastSearchTime = now.AddDays(-60);
            _books.Single(b => b.Id == 12).ReleaseDate = now.AddDays(-2);
            _books.Single(b => b.Id == 12).LastSearchTime = now.AddDays(-40);
            _books.Single(b => b.Id == 20).ReleaseDate = now.AddDays(-1);
            _books.Single(b => b.Id == 20).LastSearchTime = null;

            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly, BooksPerRun = 10 });

            _subject.RunAsync(CommandTrigger.Scheduled).GetAwaiter().GetResult();

            Assert.That(_search.Books.Select(b => b.Id), Is.EqualTo(new[] { 20, 12, 10, 1, 2, 3, 4, 5, 30, 29 }));
        }

        [Test]
        public void release_day_search_should_be_off_by_default()
        {
            _books[0].ReleaseDate = DateTime.UtcNow.AddHours(-1);

            Assert.That(_settings.Get().SearchOnReleaseDay, Is.False);
            Assert.That(_settings.Get().ReleaseDayIntervalMinutesOrOff, Is.EqualTo(0));

            _subject.RunReleaseDayAsync(CommandTrigger.Scheduled, DateTime.UtcNow).GetAwaiter().GetResult();

            Assert.That(_search.Books, Is.Empty);
        }

        [Test]
        public void release_day_search_should_search_new_releases_once_newest_first()
        {
            var now = DateTime.UtcNow;

            // 7: released today, searched before release (on add) -> search.
            _books.Single(b => b.Id == 7).ReleaseDate = now.Date;
            _books.Single(b => b.Id == 7).LastSearchTime = now.AddDays(-7);

            // 8: released 3 days ago, never searched -> search.
            _books.Single(b => b.Id == 8).ReleaseDate = now.AddDays(-3);
            _books.Single(b => b.Id == 8).LastSearchTime = null;

            // 9: released yesterday, already searched since -> skip.
            _books.Single(b => b.Id == 9).ReleaseDate = now.AddDays(-1);
            _books.Single(b => b.Id == 9).LastSearchTime = now.AddHours(-2);

            // 11: released 30 days ago, never searched since -> outside the window, the scheduled search covers it.
            _books.Single(b => b.Id == 11).ReleaseDate = now.AddDays(-30);
            _books.Single(b => b.Id == 11).LastSearchTime = now.AddDays(-40);

            _settings.Save(new TalearrMissingSearchSettings { SearchOnReleaseDay = true, StaggerSeconds = 0 });

            _subject.RunReleaseDayAsync(CommandTrigger.Scheduled, now).GetAwaiter().GetResult();

            Assert.That(_search.Books.Select(b => b.Id), Is.EqualTo(new[] { 7, 8 }));
            Assert.That(_search.UserInvoked, Is.All.False);

            // The search records LastSearchTime, so the next hourly run does not search them again.
            foreach (var book in _search.Books)
            {
                book.LastSearchTime = now.AddMinutes(1);
            }

            _search.Books.Clear();
            _subject.RunReleaseDayAsync(CommandTrigger.Scheduled, now.AddHours(1)).GetAwaiter().GetResult();

            Assert.That(_search.Books, Is.Empty);
        }

        [Test]
        public void release_day_setting_should_be_stored_as_a_talearr_config_row()
        {
            var saved = _settings.Save(new TalearrMissingSearchSettings { SearchOnReleaseDay = true });

            Assert.That(saved.SearchOnReleaseDay, Is.True);
            Assert.That(saved.ReleaseDayIntervalMinutesOrOff, Is.EqualTo(TalearrMissingSearchSettings.ReleaseDayIntervalMinutes));
            Assert.That(_config[TalearrMissingSearchSettingsService.ReleaseDayKey], Is.EqualTo("true"));
        }

        [Test]
        public void should_clamp_values_and_store_them_as_talearr_config_rows()
        {
            var saved = _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Daily, BooksPerRun = 5000, StaggerSeconds = -1 });

            Assert.That(saved.EffectiveBooksPerRun, Is.EqualTo(TalearrMissingSearchSettings.MaxBooksPerRun));
            Assert.That(saved.EffectiveStaggerSeconds, Is.EqualTo(TalearrMissingSearchSettings.DefaultStaggerSeconds));
            Assert.That(_config.Keys, Is.All.StartWith("talearr.missingsearch."));
            Assert.That(_config[TalearrMissingSearchSettingsService.ScheduleKey], Is.EqualTo("daily"));
        }

        [TestCase(MissingSearchSchedule.Off, 0)]
        [TestCase(MissingSearchSchedule.Weekly, 7 * 24 * 60)]
        [TestCase(MissingSearchSchedule.Daily, 24 * 60)]
        public void schedule_should_map_to_a_task_interval(MissingSearchSchedule schedule, int minutes)
        {
            Assert.That(new TalearrMissingSearchSettings { Schedule = schedule }.IntervalMinutes, Is.EqualTo(minutes));
        }

        [Test]
        public void task_manager_should_register_the_task_disabled_and_enable_it_when_saved()
        {
            var tasks = new List<ScheduledTask>();
            var (repository, repositoryFake) = InterfaceFake<IScheduledTaskRepository>.Create();
            repositoryFake
                .On(nameof(IScheduledTaskRepository.All), args => tasks.ToList())
                .On(nameof(IScheduledTaskRepository.Upsert), args =>
                {
                    var task = (ScheduledTask)args[0];
                    tasks.RemoveAll(t => t.TypeName == task.TypeName);
                    tasks.Add(task);
                    return task;
                })
                .On(nameof(IScheduledTaskRepository.Update), args => args[0]);

            var (configService, configFake) = InterfaceFake<IConfigService>.Create();
            configFake
                .On("get_" + nameof(IConfigService.RssSyncInterval), args => 15)
                .On("get_" + nameof(IConfigService.BackupInterval), args => 7);

            var manager = new TaskManager(repository, configService, new CacheManager(), LogManager.GetCurrentClassLogger(), _settings);
            manager.Handle(new ApplicationStartedEvent());

            var registered = manager.GetAll().Single(t => t.TypeName == typeof(ScheduledMissingBookSearchCommand).FullName);
            Assert.That(registered.Interval, Is.EqualTo(0));
            Assert.That(manager.GetPending().Any(t => t.TypeName == registered.TypeName), Is.False);

            registered.LastExecution = DateTime.UtcNow.AddDays(-30);
            var before = DateTime.UtcNow;
            var saved = _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly });
            manager.Handle(_events.OfType<TalearrMissingSearchSettingsSavedEvent>().Last());

            Assert.That(registered.Interval, Is.EqualTo(saved.IntervalMinutes));
            Assert.That(manager.GetNextExecution(typeof(ScheduledMissingBookSearchCommand)), Is.GreaterThanOrEqualTo(before.AddDays(7)));

            var releaseDay = manager.GetAll().Single(t => t.TypeName == typeof(ReleaseDayBookSearchCommand).FullName);
            Assert.That(releaseDay.Interval, Is.EqualTo(0));

            _settings.Save(new TalearrMissingSearchSettings { Schedule = MissingSearchSchedule.Weekly, SearchOnReleaseDay = true });
            manager.Handle(_events.OfType<TalearrMissingSearchSettingsSavedEvent>().Last());

            Assert.That(releaseDay.Interval, Is.EqualTo(TalearrMissingSearchSettings.ReleaseDayIntervalMinutes));
        }

        private sealed class RecordingSearch : ISearchForReleases
        {
            public List<Book> Books { get; } = new List<Book>();
            public List<bool> UserInvoked { get; } = new List<bool>();
            public List<bool> Interactive { get; } = new List<bool>();
            public int FailFor { get; set; }

            public Task<List<DownloadDecision>> BookSearch(int bookId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                throw new NotImplementedException();
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                Books.Add(book);
                UserInvoked.Add(userInvokedSearch);
                Interactive.Add(interactiveSearch);

                if (book.Id == FailFor)
                {
                    throw new InvalidOperationException("indexer down");
                }

                return Task.FromResult(new List<DownloadDecision>());
            }

            public Task<List<DownloadDecision>> BookSearch(Book book, List<Book> authorCatalog, bool missingOnly, bool userInvokedSearch, bool interactiveSearch, bool allowUnmonitored = false)
            {
                return BookSearch(book, missingOnly, userInvokedSearch, interactiveSearch, allowUnmonitored);
            }

            public Task<List<DownloadDecision>> AuthorSearch(int authorId, bool missingOnly, bool userInvokedSearch, bool interactiveSearch)
            {
                throw new NotImplementedException();
            }
        }

        private sealed class NoGrabs : IProcessDownloadDecisions
        {
            public Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions)
            {
                return Task.FromResult(new ProcessedDecisions(new List<DownloadDecision>(), new List<DownloadDecision>(), new List<DownloadDecision>()));
            }

            public Task<ProcessedDecisionResult> ProcessDecision(DownloadDecision decision, int? downloadClientId)
            {
                return Task.FromResult(ProcessedDecisionResult.Skipped);
            }
        }
    }
}
