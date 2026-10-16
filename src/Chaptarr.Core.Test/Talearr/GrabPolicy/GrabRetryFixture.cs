using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    // TaleArr: retry when sending a release to the download client fails for a transient reason.
    // Built around the 2026-10-16 live case: "The Only One Left" (Riley Sager) audiobook on Knaben
    // through Prowlarr, where fetching the .torrent answered 429 and nothing was retried.
    [TestFixture]
    public class GrabRetryFixture
    {
        private const string ProwlarrDownloadUrl = "http://prowlarr:9696/5/download?apikey=abc&link=Qm9vanYxQW9xaWxiZUU0WEVzbGxOUjIv";
        private const string ProwlarrDisabledBody = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<error code=\"429\" description=\"Indexer is disabled till 16/10/2026 12:07:11 due to recent failures.\" />";
        private const string AudiobookTitle = "The Only One Left - Riley Sager - 2023 (miok) [Audiobook] (Horror)";
        private const int KnabenId = 5;
        private const int NzbGeekId = 7;

        private static readonly DateTime Start = new DateTime(2026, 10, 16, 16, 6, 11, DateTimeKind.Utc);

        private DateTime _now;
        private CommandQueueProxy _commands;
        private IndexerStatusProxy _indexerStatus;
        private FakeGrabPolicyService _policy;
        private TalearrGrabRetryService _retry;
        private Book _audiobook;

        [SetUp]
        public void SetUp()
        {
            _now = Start;
            var (queue, commands) = CommandQueueProxy.Create();
            var (status, statusProxy) = IndexerStatusProxy.Create();

            _commands = commands;
            _indexerStatus = statusProxy;
            _policy = new FakeGrabPolicyService();
            _retry = new TalearrGrabRetryService(queue, status, _policy, LogManager.GetCurrentClassLogger())
            {
                UtcNow = () => _now
            };

            _audiobook = new Book { Id = 7501, Title = "The Only One Left", MediaType = BookMediaType.Audiobook };
        }

        // The exact chain from the live log:
        // ReleaseDownloadException: Downloading torrent failed ---> TooManyRequestsException: [429:TooManyRequests]
        private static ReleaseDownloadException LiveChain(string retryAfter = null)
        {
            var request = new HttpRequest(ProwlarrDownloadUrl);
            var headers = new HttpHeader();
            if (retryAfter != null)
            {
                headers.Add("Retry-After", retryAfter);
            }

            var response = new HttpResponse(request, headers, ProwlarrDisabledBody, (HttpStatusCode)429);
            var inner = new TooManyRequestsException(request, response);
            return new ReleaseDownloadException(new ReleaseInfo { Title = AudiobookTitle }, "Downloading torrent failed", inner);
        }

        private static ReleaseDownloadException HttpChain(HttpStatusCode status)
        {
            var request = new HttpRequest(ProwlarrDownloadUrl);
            var response = new HttpResponse(request, new HttpHeader(), "<error code=\"500\" description=\"Download failed\" />", status);
            return new ReleaseDownloadException(new ReleaseInfo { Title = AudiobookTitle }, "Downloading torrent failed", new HttpException(request, response));
        }

        private static DownloadDecision Decision(Book book, string title, int indexerId, string indexer, ReleaseSourceType source = ReleaseSourceType.Search, DownloadProtocol protocol = DownloadProtocol.Torrent)
        {
            return new DownloadDecision(new RemoteBook
            {
                Author = new Author { Id = 16, Name = "Riley Sager" },
                Books = new List<Book> { book },
                ReleaseSource = source,
                Release = new ReleaseInfo
                {
                    Title = title,
                    IndexerId = indexerId,
                    Indexer = indexer,
                    DownloadProtocol = protocol,
                    DownloadUrl = ProwlarrDownloadUrl,
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) }
            });
        }

        private List<TalearrFailedGrab> Failures(params (DownloadDecision Decision, Exception Error)[] items)
        {
            var failures = new List<TalearrFailedGrab>();
            foreach (var (decision, error) in items)
            {
                TalearrGrabRetryService.RecordFailure(failures, decision, error, null);
            }

            return failures;
        }

        private void Report(List<TalearrFailedGrab> failures)
        {
            _retry.ReportSearchOutcome(failures, new List<DownloadDecision>(), new List<DownloadDecision>());
        }

        private ProcessDownloadDecisions Processor(IDownloadService downloadService)
        {
            return new ProcessDownloadDecisions(downloadService,
                new IdentityPrioritizer(),
                new NoopPendingReleaseService(),
                LogManager.GetCurrentClassLogger(),
                _retry);
        }

        // ---- classification ----

        [Test]
        public void live_exception_chain_is_a_rate_limit_without_retry_after()
        {
            var failure = TalearrTransientGrabFailure.Classify(LiveChain());

            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.Kind, Is.EqualTo(TalearrTransientGrabKind.RateLimited));
            Assert.That(failure.StatusCode, Is.EqualTo(429));
            Assert.That(failure.RetryAfter, Is.Null);
            Assert.That(failure.Reason, Is.EqualTo("HTTP 429 Too Many Requests"));
        }

        [Test]
        public void retry_after_header_is_read_from_the_chain()
        {
            var failure = TalearrTransientGrabFailure.Classify(LiveChain("120"));

            Assert.That(failure.Kind, Is.EqualTo(TalearrTransientGrabKind.RateLimited));
            Assert.That(failure.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(120)));
            Assert.That(failure.Reason, Is.EqualTo("HTTP 429 Too Many Requests, Retry-After 120s"));
        }

        [TestCase(HttpStatusCode.InternalServerError, TalearrTransientGrabKind.ServerError)]
        [TestCase(HttpStatusCode.BadGateway, TalearrTransientGrabKind.ServerError)]
        [TestCase(HttpStatusCode.ServiceUnavailable, TalearrTransientGrabKind.ServerError)]
        [TestCase(HttpStatusCode.GatewayTimeout, TalearrTransientGrabKind.Timeout)]
        [TestCase(HttpStatusCode.RequestTimeout, TalearrTransientGrabKind.Timeout)]
        public void server_errors_and_timeouts_are_transient(HttpStatusCode status, TalearrTransientGrabKind kind)
        {
            var failure = TalearrTransientGrabFailure.Classify(HttpChain(status));

            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.Kind, Is.EqualTo(kind));
        }

        [Test]
        public void network_timeouts_are_transient()
        {
            Assert.That(TalearrTransientGrabFailure.Classify(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"))?.Kind, Is.EqualTo(TalearrTransientGrabKind.Timeout));
            Assert.That(TalearrTransientGrabFailure.Classify(new ReleaseDownloadException(new ReleaseInfo(), "Downloading nzb failed", new WebException("timeout", WebExceptionStatus.Timeout)))?.Kind, Is.EqualTo(TalearrTransientGrabKind.Timeout));
            Assert.That(TalearrTransientGrabFailure.Classify(new ReleaseDownloadException(new ReleaseInfo(), "Downloading nzb failed", new WebException("refused", WebExceptionStatus.ConnectFailure)))?.Kind, Is.EqualTo(TalearrTransientGrabKind.Network));
        }

        [Test]
        public void permanent_failures_are_not_transient()
        {
            var request = new HttpRequest(ProwlarrDownloadUrl);
            var notFound = new HttpException(request, new HttpResponse(request, new HttpHeader(), string.Empty, HttpStatusCode.NotFound));

            Assert.That(TalearrTransientGrabFailure.Classify(new ReleaseUnavailableException(new ReleaseInfo(), "Downloading torrent failed", notFound)), Is.Null);
            Assert.That(TalearrTransientGrabFailure.Classify(new ReleaseDownloadException(new ReleaseInfo(), "Downloading torrent failed", notFound)), Is.Null);
            Assert.That(TalearrTransientGrabFailure.Classify(new ReleaseBlockedException(new ReleaseInfo(), "blocked")), Is.Null);
            Assert.That(TalearrTransientGrabFailure.Classify(new DownloadClientUnavailableException("down")), Is.Null);
            Assert.That(TalearrTransientGrabFailure.Classify(new InvalidOperationException("bad torrent")), Is.Null);
            Assert.That(TalearrTransientGrabFailure.Classify(null), Is.Null);
        }

        // ---- ProcessDownloadDecisions: next approved release first ----

        [Test]
        public async Task after_a_429_the_next_release_from_another_indexer_is_grabbed_and_no_re_search_is_scheduled()
        {
            var ebook = new Book { Id = 7486, Title = "The Only One Left", MediaType = BookMediaType.Ebook };
            var knabenEpub = Decision(ebook, "The Only One Left by Riley Sager EPUB", KnabenId, "Knaben (Prowlarr)");
            var knabenOther = Decision(ebook, "Riley Sager - The Only One Left (2023) EPUB", KnabenId, "Knaben (Prowlarr)");
            var nzbGeek = Decision(ebook, "Sager.Riley-The.Only.One.Left", NzbGeekId, "NZBgeek (Prowlarr)", protocol: DownloadProtocol.Usenet);

            var downloads = new ScriptedDownloadService(remoteBook => remoteBook.Release.IndexerId == KnabenId ? LiveChain() : null);

            var result = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { knabenEpub, knabenOther, nzbGeek });

            Assert.That(result.Grabbed.Single(), Is.SameAs(nzbGeek));
            Assert.That(downloads.Attempted.Select(r => r.Release.Title), Is.EqualTo(new[]
            {
                "The Only One Left by Riley Sager EPUB",
                "Sager.Riley-The.Only.One.Left"
            }), "the second Knaben release is not fetched right after Knaben said 429");
            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public async Task after_a_server_error_the_next_release_from_the_same_indexer_is_still_tried()
        {
            var first = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");
            var second = Decision(_audiobook, "Riley Sager - The Only One Left [M4B]", KnabenId, "Knaben (Prowlarr)");
            var downloads = new ScriptedDownloadService(remoteBook => ReferenceEquals(remoteBook, first.RemoteBook) ? HttpChain(HttpStatusCode.InternalServerError) : null);

            var result = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { first, second });

            Assert.That(result.Grabbed.Single(), Is.SameAs(second));
            Assert.That(downloads.Attempted, Has.Count.EqualTo(2));
            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public async Task live_case_single_release_429_schedules_a_re_search_in_two_minutes()
        {
            var only = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");
            var downloads = new ScriptedDownloadService(_ => LiveChain());

            var result = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { only });

            Assert.That(result.Grabbed, Is.Empty);
            var state = _retry.GetScheduled().Single();
            Assert.That(state.BookId, Is.EqualTo(7501));
            Assert.That(state.Attempt, Is.EqualTo(1));
            Assert.That(state.MaxAttempts, Is.EqualTo(3));
            Assert.That(state.DueUtc, Is.EqualTo(Start + TimeSpan.FromMinutes(2)));
            Assert.That(state.Trigger, Is.EqualTo(CommandTrigger.Unspecified));
            Assert.That(state.Reason, Is.EqualTo("HTTP 429 Too Many Requests"));
            Assert.That(_commands.Pushed, Is.Empty);
        }

        [Test]
        public async Task a_book_stored_as_pending_is_not_re_searched()
        {
            var only = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");
            var failures = Failures((only, LiveChain()));

            _retry.ReportSearchOutcome(failures, new List<DownloadDecision>(), new List<DownloadDecision> { only });

            Assert.That(_retry.GetScheduled(), Is.Empty);
            await Task.CompletedTask;
        }

        // ---- scheduling ----

        [Test]
        public void retry_after_longer_than_the_schedule_is_honoured()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain("300"))));

            Assert.That(_retry.GetScheduled().Single().DueUtc, Is.EqualTo(Start + TimeSpan.FromMinutes(5)));
        }

        [Test]
        public void retry_after_is_capped()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain("10800"))));

            Assert.That(_retry.GetScheduled().Single().DueUtc, Is.EqualTo(Start + TalearrGrabRetryService.MaxDelay));
        }

        [Test]
        public void upstream_indexer_back_off_is_waited_out()
        {
            _indexerStatus.Blocked.Add(new IndexerStatus { ProviderId = KnabenId, DisabledTill = Start + TimeSpan.FromMinutes(15) });

            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            Assert.That(_retry.GetScheduled().Single().DueUtc, Is.EqualTo(Start + TimeSpan.FromMinutes(15) + TalearrGrabRetryService.BackOffMargin));
        }

        [Test]
        public void due_re_search_queues_a_book_search_for_that_book()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            _now = Start + TimeSpan.FromMinutes(1);
            _retry.RunDue();
            Assert.That(_commands.Pushed, Is.Empty, "not before it is due");

            _now = Start + TimeSpan.FromMinutes(2);
            _retry.RunDue();

            var pushed = _commands.Pushed.Single();
            Assert.That(pushed.Command, Is.TypeOf<BookSearchCommand>());
            Assert.That(((BookSearchCommand)pushed.Command).BookIds, Is.EqualTo(new[] { 7501 }));
            Assert.That(pushed.Trigger, Is.EqualTo(CommandTrigger.Unspecified));
            Assert.That(_retry.GetScheduled().Single().InFlight, Is.True);

            _retry.RunDue();
            Assert.That(_commands.Pushed, Has.Count.EqualTo(1), "an in-flight re-search is not queued twice");
        }

        [Test]
        public void user_started_search_is_retried_as_a_manual_search()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)", ReleaseSourceType.UserInvokedSearch), LiveChain())));

            _now = Start + TimeSpan.FromMinutes(2);
            _retry.RunDue();

            Assert.That(_commands.Pushed.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
        }

        [Test]
        public void attempts_follow_two_ten_thirty_minutes_then_stop_at_the_retry_limit()
        {
            var decision = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");
            var expected = new[] { 2, 10, 30 };

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var reportedAt = _now;
                Report(Failures((decision, LiveChain())));

                var state = _retry.GetScheduled().Single();
                Assert.That(state.Attempt, Is.EqualTo(attempt));
                Assert.That(state.DueUtc, Is.EqualTo(reportedAt + TimeSpan.FromMinutes(expected[attempt - 1])));

                _now = state.DueUtc;
                _retry.RunDue();
                Assert.That(_commands.Pushed, Has.Count.EqualTo(attempt));
            }

            // The third re-search failed the same way: limit reached, nothing more is scheduled.
            Report(Failures((decision, LiveChain())));
            Assert.That(_retry.GetScheduled(), Is.Empty);

            _now += TimeSpan.FromHours(2);
            _retry.RunDue();
            Assert.That(_commands.Pushed, Has.Count.EqualTo(3));
        }

        [Test]
        public void retry_limit_comes_from_the_grab_policy()
        {
            _policy.Policy.MaxAutomaticRetries = 1;
            var decision = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");

            Report(Failures((decision, LiveChain())));
            Assert.That(_retry.GetScheduled().Single().MaxAttempts, Is.EqualTo(1));

            _now += TimeSpan.FromMinutes(2);
            _retry.RunDue();
            Report(Failures((decision, LiveChain())));

            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public void a_second_failure_while_waiting_does_not_restart_the_clock()
        {
            var decision = Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)");
            Report(Failures((decision, LiveChain())));

            _now = Start + TimeSpan.FromMinutes(1);
            Report(Failures((decision, LiveChain())));

            var state = _retry.GetScheduled().Single();
            Assert.That(state.Attempt, Is.EqualTo(1));
            Assert.That(state.DueUtc, Is.EqualTo(Start + TimeSpan.FromMinutes(2)));
        }

        [Test]
        public void grab_clears_the_schedule()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            _retry.Handle(new BookGrabbedEvent(new RemoteBook { Books = new List<Book> { _audiobook } }));

            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public void finished_re_search_without_a_new_failure_ends_the_chain()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));
            _now = Start + TimeSpan.FromMinutes(2);
            _retry.RunDue();

            var command = (BookSearchCommand)_commands.Pushed.Single().Command;
            _retry.Handle(new CommandExecutedEvent(new CommandModel { Id = 0, Body = command, Name = command.Name }));

            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public void other_book_searches_finishing_do_not_end_the_chain()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));
            _now = Start + TimeSpan.FromMinutes(2);
            _retry.RunDue();

            var other = new BookSearchCommand(new List<int> { 7501 });
            _retry.Handle(new CommandExecutedEvent(new CommandModel { Id = 0, Body = other, Name = other.Name }));

            Assert.That(_retry.GetScheduled(), Has.Count.EqualTo(1));
        }

        [Test]
        public void due_re_search_waits_while_the_indexer_is_still_backed_off()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            _now = Start + TimeSpan.FromMinutes(2);
            _indexerStatus.Blocked.Add(new IndexerStatus { ProviderId = KnabenId, DisabledTill = _now + TimeSpan.FromMinutes(5) });
            _retry.RunDue();

            Assert.That(_commands.Pushed, Is.Empty);
            var state = _retry.GetScheduled().Single();
            Assert.That(state.DueUtc, Is.EqualTo(_now + TimeSpan.FromMinutes(5) + TalearrGrabRetryService.BackOffMargin));
            Assert.That(state.Attempt, Is.EqualTo(1), "waiting out the back-off does not spend an attempt");

            _indexerStatus.Blocked.Clear();
            _now = state.DueUtc;
            _retry.RunDue();
            Assert.That(_commands.Pushed, Has.Count.EqualTo(1));
        }

        [Test]
        public void indexer_disabled_for_hours_ends_the_chain()
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            _now = Start + TimeSpan.FromMinutes(2);
            _indexerStatus.Blocked.Add(new IndexerStatus { ProviderId = KnabenId, DisabledTill = _now + TimeSpan.FromHours(6) });
            _retry.RunDue();

            Assert.That(_commands.Pushed, Is.Empty);
            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public void setting_off_schedules_nothing()
        {
            _policy.Policy.RetryFailedGrabs = false;

            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), LiveChain())));

            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [TestCase(ReleaseSourceType.Rss)]
        [TestCase(ReleaseSourceType.InteractiveSearch)]
        [TestCase(ReleaseSourceType.ReleasePush)]
        public void only_searches_are_re_searched(ReleaseSourceType source)
        {
            Report(Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)", source), LiveChain())));

            Assert.That(_retry.GetScheduled(), Is.Empty);
        }

        [Test]
        public void permanent_failures_are_not_re_searched()
        {
            var failures = Failures((Decision(_audiobook, AudiobookTitle, KnabenId, "Knaben (Prowlarr)"), new InvalidOperationException("Invalid torrent file")));

            Assert.That(failures, Is.Empty);
        }

        [Test]
        public void retry_setting_round_trips_through_talearr_config_rows()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var service = new TalearrGrabPolicyService(repository, LogManager.GetCurrentClassLogger());

            Assert.That(service.Get().RetryFailedGrabs, Is.True, "on by default");

            var policy = service.Get();
            policy.RetryFailedGrabs = false;
            service.Save(policy);

            Assert.That(proxy.Values[TalearrGrabPolicyService.RetryFailedGrabsKey], Is.EqualTo("False"));
            Assert.That(TalearrGrabPolicyService.RetryFailedGrabsKey, Does.StartWith("talearr."));
            Assert.That(new TalearrGrabPolicyService(repository, LogManager.GetCurrentClassLogger()).Get().RetryFailedGrabs, Is.False);
        }

        private sealed class ScriptedDownloadService : IDownloadService
        {
            private readonly Func<RemoteBook, Exception> _failure;

            public ScriptedDownloadService(Func<RemoteBook, Exception> failure)
            {
                _failure = failure;
            }

            public List<RemoteBook> Attempted { get; } = new();

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Attempted.Add(remoteBook);
                var failure = _failure(remoteBook);
                if (failure != null)
                {
                    throw failure;
                }

                return Task.CompletedTask;
            }
        }

        private sealed class IdentityPrioritizer : IPrioritizeDownloadDecision
        {
            public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
            {
                return decisions;
            }
        }

        private sealed class NoopPendingReleaseService : IPendingReleaseService
        {
            public void Add(DownloadDecision decision, PendingReleaseReason reason) { }
            public void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions) { }
            public List<ReleaseInfo> GetPending() => new();
            public List<RemoteBook> GetPendingRemoteBooks(int authorId) => new();
            public List<NzbDrone.Core.Queue.Queue> GetPendingQueue() => new();
            public NzbDrone.Core.Queue.Queue FindPendingQueueItem(int queueId) => null;
            public void RemovePendingQueueItems(int queueId) { }
            public RemoteBook OldestPendingRelease(int authorId, int[] bookIds) => null;
        }

        internal class IndexerStatusProxy : DispatchProxy
        {
            public List<IndexerStatus> Blocked { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IIndexerStatusService.GetBlockedProviders))
                {
                    return Blocked.ToList();
                }

                throw new NotImplementedException($"Test proxy does not implement IIndexerStatusService.{targetMethod?.Name}");
            }

            public static (IIndexerStatusService Service, IndexerStatusProxy Proxy) Create()
            {
                var service = DispatchProxy.Create<IIndexerStatusService, IndexerStatusProxy>();
                return (service, (IndexerStatusProxy)(object)service);
            }
        }
    }
}
