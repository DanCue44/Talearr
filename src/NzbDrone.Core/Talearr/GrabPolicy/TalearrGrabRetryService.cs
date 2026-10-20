using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // One release that could not be sent to the download client for a transient reason.
    public sealed class TalearrFailedGrab
    {
        public TalearrFailedGrab(DownloadDecision decision, TalearrTransientGrabFailure failure)
        {
            Decision = decision;
            Failure = failure;
        }

        public DownloadDecision Decision { get; }
        public TalearrTransientGrabFailure Failure { get; }

        public RemoteBook RemoteBook => Decision?.RemoteBook;
        public int IndexerId => RemoteBook?.Release?.IndexerId ?? 0;
        public string Indexer => RemoteBook?.Release?.Indexer;
    }

    public sealed class TalearrGrabRetryState
    {
        public int BookId { get; set; }
        public string Title { get; set; }

        // The re-search this state is waiting for (1 = first re-search).
        public int Attempt { get; set; }
        public int MaxAttempts { get; set; }
        public DateTime DueUtc { get; set; }
        public CommandTrigger Trigger { get; set; }
        public HashSet<int> IndexerIds { get; set; } = new HashSet<int>();
        public string Indexers { get; set; }
        public string Reason { get; set; }
        public int Postpones { get; set; }

        public bool InFlight { get; set; }
        public DateTime? FiredUtc { get; set; }
        public int? CommandId { get; set; }
        public Command CommandBody { get; set; }

        public TalearrGrabRetryState Clone()
        {
            var clone = (TalearrGrabRetryState)MemberwiseClone();
            clone.IndexerIds = new HashSet<int>(IndexerIds);
            return clone;
        }
    }

    public interface ITalearrGrabRetryService
    {
        // Called after one search's decisions were processed: books that only failed for transient
        // reasons (and were not grabbed or stored as pending) get a short scheduled re-search.
        void ReportSearchOutcome(IReadOnlyCollection<TalearrFailedGrab> failures,
                                 IEnumerable<DownloadDecision> grabbed,
                                 IEnumerable<DownloadDecision> pending);

        // Fires re-searches that are due. Called by the timer every TickInterval.
        void RunDue();

        List<TalearrGrabRetryState> GetScheduled();
    }

    // Talearr: when sending a release to the download client fails for a transient reason (429
    // with or without Retry-After, 5xx, timeout), ProcessDownloadDecisions first tries the next
    // approved release of the same search (skipping other releases of an indexer that just said
    // 429), and when nothing could be sent this service searches the book again after 2, 10 and
    // 30 minutes. The wait honours Retry-After and the indexer back-off upstream recorded
    // (IIndexerStatusService), capped at MaxDelay, and the number of re-searches is the grab
    // policy's automatic retry limit. State is in memory only; nothing is written to chaptarr.db
    // beyond the normal search command.
    public class TalearrGrabRetryService : ITalearrGrabRetryService,
                                           IHandle<BookGrabbedEvent>,
                                           IHandle<CommandExecutedEvent>,
                                           IHandle<ApplicationStartedEvent>,
                                           IHandle<ApplicationShutdownRequested>
    {
        public const string LogPrefix = "Talearr:";

        public static readonly TimeSpan[] Schedule =
        {
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30)
        };

        public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(60);
        public static readonly TimeSpan BackOffMargin = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
        public static readonly TimeSpan QueueFullRetry = TimeSpan.FromMinutes(2);
        public static readonly TimeSpan InFlightTimeout = TimeSpan.FromMinutes(60);
        public const int MaxPostpones = 3;

        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IIndexerStatusService _indexerStatusService;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private readonly Dictionary<int, TalearrGrabRetryState> _states = new Dictionary<int, TalearrGrabRetryState>();
        private Timer _timer;
        private bool _stopped;
        private int _running;

        public TalearrGrabRetryService(IManageCommandQueue commandQueueManager,
                                       IIndexerStatusService indexerStatusService,
                                       ITalearrGrabPolicyService grabPolicyService,
                                       Logger logger)
        {
            _commandQueueManager = commandQueueManager;
            _indexerStatusService = indexerStatusService;
            _grabPolicyService = grabPolicyService;
            _logger = logger;
        }

        // For tests: the clock used for due times.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        // Called from ProcessDownloadDecisions when a grab threw. Returns the failure when it was
        // transient (and records it), null otherwise.
        public static TalearrTransientGrabFailure RecordFailure(List<TalearrFailedGrab> failures, DownloadDecision decision, Exception exception, Logger logger)
        {
            var failure = TalearrTransientGrabFailure.Classify(exception);
            if (failure == null || failures == null || decision?.RemoteBook == null)
            {
                return failure;
            }

            var failed = new TalearrFailedGrab(decision, failure);
            failures.Add(failed);

            logger?.Info("{0} sending '{1}' to the download client failed ({2} from {3}); trying the next approved release of this search",
                LogPrefix,
                decision.RemoteBook.Release?.Title,
                failure.Reason,
                DescribeIndexer(failed.Indexer));

            return failure;
        }

        // After a 429 from an indexer, other releases of the same indexer are not fetched again in
        // the same pass (they would get the same answer and extend the back-off). The books they
        // cover are left to the scheduled re-search.
        public static bool ShouldSkipAfterRateLimit(DownloadDecision report, List<TalearrFailedGrab> failures, Logger logger)
        {
            var indexerId = report?.RemoteBook?.Release?.IndexerId ?? 0;
            if (indexerId <= 0 || failures == null || failures.Count == 0)
            {
                return false;
            }

            var limited = failures.FirstOrDefault(f => f.IndexerId == indexerId && f.Failure.Kind == TalearrTransientGrabKind.RateLimited);
            if (limited == null)
            {
                return false;
            }

            failures.Add(new TalearrFailedGrab(report, limited.Failure));

            logger?.Info("{0} not fetching '{1}' from {2} in this pass because it just answered 429",
                LogPrefix,
                report.RemoteBook.Release?.Title,
                DescribeIndexer(report.RemoteBook.Release?.Indexer));

            return true;
        }

        public void ReportSearchOutcome(IReadOnlyCollection<TalearrFailedGrab> failures,
                                        IEnumerable<DownloadDecision> grabbed,
                                        IEnumerable<DownloadDecision> pending)
        {
            if (failures == null || failures.Count == 0)
            {
                return;
            }

            var eligible = failures
                .Where(f => f?.RemoteBook?.Books != null && IsSearchSource(f.RemoteBook.ReleaseSource))
                .ToList();

            if (eligible.Count == 0)
            {
                return;
            }

            var policy = TalearrGrabPolicyService.Resolve(_grabPolicyService);
            var handled = new HashSet<int>(BookIds(grabbed).Concat(BookIds(pending)));

            var byBook = eligible
                .SelectMany(f => f.RemoteBook.Books.Where(b => b != null && b.Id > 0).Select(b => (Book: b, Failure: f)))
                .Where(x => !handled.Contains(x.Book.Id))
                .GroupBy(x => x.Book.Id)
                .ToList();

            if (byBook.Count == 0)
            {
                return;
            }

            if (!policy.RetryFailedGrabs)
            {
                foreach (var group in byBook)
                {
                    _logger.Info("{0} no release could be sent to the download client for '{1}' ({2}); automatic re-search after failed grabs is off",
                        LogPrefix, group.First().Book.Title, group.First().Failure.Failure.Reason);
                }

                return;
            }

            var maxAttempts = policy.EffectiveMaxAutomaticRetries;
            var now = UtcNow();
            var backOff = GetBackOff();

            lock (_lock)
            {
                foreach (var group in byBook)
                {
                    var bookId = group.Key;
                    var book = group.First().Book;
                    var bookFailures = group.Select(x => x.Failure).ToList();
                    var indexerIds = new HashSet<int>(bookFailures.Select(f => f.IndexerId).Where(id => id > 0));
                    var indexers = string.Join(", ", bookFailures.Select(f => DescribeIndexer(f.Indexer)).Distinct());
                    var reason = bookFailures.First().Failure.Reason;
                    var trigger = bookFailures.Any(f => f.RemoteBook.ReleaseSource == ReleaseSourceType.UserInvokedSearch)
                        ? CommandTrigger.Manual
                        : CommandTrigger.Unspecified;

                    int attempt;
                    if (_states.TryGetValue(bookId, out var existing))
                    {
                        if (!existing.InFlight)
                        {
                            // Already waiting for a re-search; another search hit the same wall.
                            existing.IndexerIds.UnionWith(indexerIds);
                            continue;
                        }

                        attempt = existing.Attempt + 1;
                        trigger = existing.Trigger == CommandTrigger.Manual ? CommandTrigger.Manual : trigger;
                    }
                    else
                    {
                        attempt = 1;
                    }

                    if (attempt > maxAttempts)
                    {
                        _states.Remove(bookId);
                        _logger.Warn("{0} no release could be sent to the download client for '{1}' ({2} from {3}); automatic retry limit reached ({4} re-searches), not searching again. RSS sync and scheduled searches still apply.",
                            LogPrefix, book.Title, reason, indexers, maxAttempts);
                        continue;
                    }

                    var delay = ComputeDelay(attempt, bookFailures, indexerIds, backOff, now);
                    _states[bookId] = new TalearrGrabRetryState
                    {
                        BookId = bookId,
                        Title = book.Title,
                        Attempt = attempt,
                        MaxAttempts = maxAttempts,
                        DueUtc = now + delay,
                        Trigger = trigger,
                        IndexerIds = indexerIds,
                        Indexers = indexers,
                        Reason = reason
                    };

                    _logger.Info("{0} no release could be sent to the download client for '{1}' ({2} from {3}); searching again in {4} (attempt {5} of {6})",
                        LogPrefix, book.Title, reason, indexers, DescribeDelay(delay), attempt, maxAttempts);
                }
            }
        }

        public void RunDue()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
            {
                return;
            }

            try
            {
                RunDueInternal();
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        public List<TalearrGrabRetryState> GetScheduled()
        {
            lock (_lock)
            {
                return _states.Values.Select(s => s.Clone()).OrderBy(s => s.DueUtc).ToList();
            }
        }

        public void Handle(BookGrabbedEvent message)
        {
            var bookIds = message?.Book?.Books?.Where(b => b != null && b.Id > 0).Select(b => b.Id).ToList();
            if (bookIds == null || bookIds.Count == 0)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var bookId in bookIds)
                {
                    if (_states.TryGetValue(bookId, out var state))
                    {
                        _states.Remove(bookId);
                        _logger.Info("{0} '{1}' was grabbed{2}; no further re-search needed",
                            LogPrefix, state.Title, state.InFlight ? string.Format(" on re-search attempt {0}", state.Attempt) : string.Empty);
                    }
                }
            }
        }

        public void Handle(CommandExecutedEvent message)
        {
            var command = message?.Command;
            if (command?.Body is not BookSearchCommand)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var state in _states.Values.Where(s => s.InFlight && IsSameCommand(s, command)).ToList())
                {
                    // The re-search ran, nothing was grabbed and no transient failure re-armed it:
                    // the indexers had nothing (more) to offer. Normal RSS and scheduled searches go on.
                    _states.Remove(state.BookId);
                    _logger.Info("{0} re-search {1} of {2} for '{3}' finished without sending a release; not searching again",
                        LogPrefix, state.Attempt, state.MaxAttempts, state.Title);
                }
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            lock (_lock)
            {
                if (_stopped || _timer != null)
                {
                    return;
                }

                _timer = new Timer(_ => OnTimer(), null, TickInterval, TickInterval);
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            lock (_lock)
            {
                _stopped = true;
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void OnTimer()
        {
            try
            {
                RunDue();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr grab re-search timer failed");
            }
        }

        private void RunDueInternal()
        {
            var now = UtcNow();
            List<TalearrGrabRetryState> due;

            lock (_lock)
            {
                foreach (var stale in _states.Values.Where(s => s.InFlight && s.FiredUtc.HasValue && now - s.FiredUtc.Value > InFlightTimeout).ToList())
                {
                    _states.Remove(stale.BookId);
                }

                due = _states.Values.Where(s => !s.InFlight && s.DueUtc <= now).ToList();
            }

            if (due.Count == 0)
            {
                return;
            }

            var backOff = GetBackOff();

            foreach (var state in due)
            {
                if (TryPostponeForBackOff(state, backOff, now))
                {
                    continue;
                }

                var command = new BookSearchCommand(new List<int> { state.BookId });
                CommandModel model;

                try
                {
                    model = _commandQueueManager.Push(command, CommandPriority.Normal, state.Trigger);
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        state.DueUtc = now + QueueFullRetry;
                    }

                    _logger.Debug(ex, "Unable to queue the Talearr re-search for {0}, trying again in {1}", state.Title, QueueFullRetry);
                    continue;
                }

                lock (_lock)
                {
                    state.InFlight = true;
                    state.FiredUtc = now;
                    state.CommandBody = command;
                    state.CommandId = model?.Id;
                }

                _logger.Info("{0} searching again for '{1}' after a failed grab (attempt {2} of {3})",
                    LogPrefix, state.Title, state.Attempt, state.MaxAttempts);
            }
        }

        // When every indexer that failed is still in upstream's back-off, a re-search now could not
        // reach it. Wait for the back-off to end (a few times, within MaxDelay) instead of spending
        // an attempt, and give up when the indexer is disabled for longer.
        private bool TryPostponeForBackOff(TalearrGrabRetryState state, Dictionary<int, DateTime> backOff, DateTime now)
        {
            if (state.IndexerIds.Count == 0 || !state.IndexerIds.All(backOff.ContainsKey))
            {
                return false;
            }

            var until = state.IndexerIds.Min(id => backOff[id]);
            if (until <= now)
            {
                return false;
            }

            lock (_lock)
            {
                if (state.Postpones < MaxPostpones && until - now <= MaxDelay)
                {
                    state.Postpones++;
                    state.DueUtc = until + BackOffMargin;
                    _logger.Info("{0} {1} is backed off until {2:u}; holding the re-search for '{3}' until then",
                        LogPrefix, state.Indexers, until, state.Title);
                    return true;
                }

                _states.Remove(state.BookId);
            }

            _logger.Warn("{0} {1} is backed off until {2:u}; not re-searching '{3}' (RSS sync and scheduled searches still apply)",
                LogPrefix, state.Indexers, until, state.Title);
            return true;
        }

        internal static TimeSpan ComputeDelay(int attempt, List<TalearrFailedGrab> failures, HashSet<int> indexerIds, Dictionary<int, DateTime> backOff, DateTime now)
        {
            var delay = Schedule[Math.Min(Math.Max(attempt, 1), Schedule.Length) - 1];

            var retryAfter = failures
                .Select(f => f.Failure.RetryAfter)
                .Where(r => r.HasValue)
                .Select(r => r.Value)
                .DefaultIfEmpty(TimeSpan.Zero)
                .Max();

            if (retryAfter > delay)
            {
                delay = retryAfter;
            }

            // Upstream's indexer back-off (DownloadService records the 429 or failure there). When
            // all failing indexers are disabled, wait at least until the first comes back.
            if (indexerIds.Count > 0 && indexerIds.All(backOff.ContainsKey))
            {
                var untilBack = indexerIds.Min(id => backOff[id]) - now + BackOffMargin;
                if (untilBack > delay)
                {
                    delay = untilBack;
                }
            }

            return delay > MaxDelay ? MaxDelay : delay;
        }

        private Dictionary<int, DateTime> GetBackOff()
        {
            try
            {
                return (_indexerStatusService?.GetBlockedProviders() ?? new List<IndexerStatus>())
                    .Where(s => s.DisabledTill.HasValue)
                    .GroupBy(s => s.ProviderId)
                    .ToDictionary(g => g.Key, g => g.Max(s => s.DisabledTill.Value));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read indexer back-off");
                return new Dictionary<int, DateTime>();
            }
        }

        private static bool IsSameCommand(TalearrGrabRetryState state, CommandModel command)
        {
            if (ReferenceEquals(state.CommandBody, command.Body))
            {
                return true;
            }

            return state.CommandId.HasValue && state.CommandId.Value > 0 && state.CommandId.Value == command.Id;
        }

        private static bool IsSearchSource(ReleaseSourceType source)
        {
            // Automatic and user-started searches. RSS sync comes back on its own, and interactive
            // grabs show their error to the user.
            return source == ReleaseSourceType.Search || source == ReleaseSourceType.UserInvokedSearch;
        }

        private static IEnumerable<int> BookIds(IEnumerable<DownloadDecision> decisions)
        {
            return (decisions ?? Enumerable.Empty<DownloadDecision>())
                .SelectMany(d => d?.RemoteBook?.Books ?? new List<Book>())
                .Where(b => b != null && b.Id > 0)
                .Select(b => b.Id);
        }

        private static string DescribeIndexer(string indexer)
        {
            return string.IsNullOrWhiteSpace(indexer) ? "the indexer" : "indexer " + indexer;
        }

        private static string DescribeDelay(TimeSpan delay)
        {
            if (delay.TotalSeconds < 90)
            {
                return string.Format("{0} seconds", (int)Math.Ceiling(delay.TotalSeconds));
            }

            var minutes = (int)Math.Ceiling(delay.TotalMinutes);
            return minutes == 1 ? "1 minute" : string.Format("{0} minutes", minutes);
        }
    }
}
