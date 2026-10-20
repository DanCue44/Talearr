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
