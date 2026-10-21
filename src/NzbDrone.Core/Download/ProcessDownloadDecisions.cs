using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Talearr.Explain;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Download
{
    public interface IProcessDownloadDecisions
    {
        Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions);
        Task<ProcessedDecisionResult> ProcessDecision(DownloadDecision decision, int? downloadClientId);
    }

    public class ProcessDownloadDecisions : IProcessDownloadDecisions
    {
        private readonly IDownloadService _downloadService;
        private readonly IPrioritizeDownloadDecision _prioritizeDownloadDecision;
        private readonly IPendingReleaseService _pendingReleaseService;
        private readonly Logger _logger;
        private readonly ITalearrGrabRetryService _talearrGrabRetry;
        private readonly ITalearrDecisionRecorder _talearrDecisionRecorder;

        public ProcessDownloadDecisions(IDownloadService downloadService,
                                        IPrioritizeDownloadDecision prioritizeDownloadDecision,
                                        IPendingReleaseService pendingReleaseService,
                                        Logger logger,
                                        ITalearrGrabRetryService talearrGrabRetry = null,
                                        ITalearrDecisionRecorder talearrDecisionRecorder = null)
        {
            _downloadService = downloadService;
            _prioritizeDownloadDecision = prioritizeDownloadDecision;
            _pendingReleaseService = pendingReleaseService;
            _logger = logger;
            _talearrGrabRetry = talearrGrabRetry;
            _talearrDecisionRecorder = talearrDecisionRecorder;
        }

        public async Task<ProcessedDecisions> ProcessDecisions(List<DownloadDecision> decisions)
        {
            var qualifiedReports = GetQualifiedReports(decisions);
            var prioritizedDecisions = _prioritizeDownloadDecision.PrioritizeDecisions(qualifiedReports);

            // Talearr: last-resort bundles wait until every single copy had its turn.
            var bundleFallbacks = prioritizedDecisions.Where(IsBundleFallback).ToList();
            prioritizedDecisions = prioritizedDecisions.Where(d => !IsBundleFallback(d)).ToList();
            var grabbed = new List<DownloadDecision>();
            var pending = new List<DownloadDecision>();
            var rejected = decisions.Where(d => d.Rejected).ToList();

            var pendingAddQueue = new List<Tuple<DownloadDecision, PendingReleaseReason>>();

            var usenetFailed = false;
            var torrentFailed = false;

            // Talearr: grabs that failed for a transient reason (429, 5xx, timeout) in this pass.
            var talearrFailures = new List<TalearrFailedGrab>();

            foreach (var report in prioritizedDecisions)
            {
                var downloadProtocol = report.RemoteBook.Release.DownloadProtocol;

                //Skip if already grabbed
                if (IsBookProcessed(grabbed, report))
                {
                    continue;
                }

                if (report.TemporarilyRejected)
                {
                    PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.Delay);
                    continue;
                }

                if ((downloadProtocol == DownloadProtocol.Usenet && usenetFailed) ||
                    (downloadProtocol == DownloadProtocol.Torrent && torrentFailed))
                {
                    PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.DownloadClientUnavailable);
                    continue;
                }

                // Talearr: an indexer that just answered 429 is not asked again in this pass.
                if (TalearrGrabRetryService.ShouldSkipAfterRateLimit(report, talearrFailures, _logger))
                {
                    continue;
                }

                var result = await ProcessDecisionInternal(report, null, talearrFailures);

                switch (result)
                {
                    case ProcessedDecisionResult.Grabbed:
                        {
                            grabbed.Add(report);
                            break;
                        }

                    case ProcessedDecisionResult.Pending:
                        {
                            PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.Delay);
                            break;
                        }

                    case ProcessedDecisionResult.Rejected:
                        {
                            rejected.Add(report);
                            break;
                        }

                    case ProcessedDecisionResult.Failed:
                        {
                            PreparePending(pendingAddQueue, grabbed, pending, report, PendingReleaseReason.DownloadClientUnavailable);

                            if (downloadProtocol == DownloadProtocol.Usenet)
                            {
                                usenetFailed = true;
                            }
                            else if (downloadProtocol == DownloadProtocol.Torrent)
                            {
                                torrentFailed = true;
                            }

                            break;
                        }

                    case ProcessedDecisionResult.Skipped:
                        {
                            break;
                        }
                }
            }

            // Talearr: a bundle is grabbed only for a book that nothing else covered in this pass.
            if (bundleFallbacks.Any())
            {
                var coveredBookIds = qualifiedReports
                    .Where(d => !IsBundleFallback(d))
                    .SelectMany(d => d.RemoteBook.Books)
                    .Where(b => b != null)
                    .Select(b => b.Id)
                    .ToHashSet();

                foreach (var report in bundleFallbacks)
                {
                    var remoteBook = report.RemoteBook;
                    var downloadProtocol = remoteBook.Release.DownloadProtocol;

                    if (!TalearrBundleFallback.IsAutomaticSearchSource(remoteBook.ReleaseSource))
                    {
                        _logger.Debug("Not grabbing bundle {0} from {1}: bundles are a last resort for searches only", remoteBook.Release.Title, remoteBook.ReleaseSource);
                        continue;
                    }

                    if (report.TemporarilyRejected)
                    {
                        _logger.Debug("Not grabbing bundle {0} yet: {1}", remoteBook.Release.Title, string.Join(", ", report.Rejections));
                        continue;
                    }

                    var grabbedBookIds = grabbed.SelectMany(d => d.RemoteBook.Books).Where(b => b != null).Select(b => b.Id).ToHashSet();
                    // Only books the bundle's title names: an author search maps an unmatched
                    // release to every searched book, and those are not all in the bundle.
                    var openBooks = remoteBook.Books
                        .Where(b => b != null && b.Id > 0 && !grabbedBookIds.Contains(b.Id) && !coveredBookIds.Contains(b.Id))
                        .Where(b => TalearrBundleFallback.BundleNamesBook(remoteBook, b))
                        .ToList();

                    if (!openBooks.Any())
                    {
                        _logger.Debug("Not grabbing bundle {0}: its books are covered by a single copy or not named in it", remoteBook.Release.Title);
                        continue;
                    }

                    if ((downloadProtocol == DownloadProtocol.Usenet && usenetFailed) ||
                        (downloadProtocol == DownloadProtocol.Torrent && torrentFailed))
                    {
                        continue;
                    }

                    if (TalearrGrabRetryService.ShouldSkipAfterRateLimit(report, talearrFailures, _logger))
                    {
                        continue;
                    }

                    // Only the books still open are grab targets, so history and import expect just them.
                    if (openBooks.Count != remoteBook.Books.Count)
                    {
                        remoteBook.Books = openBooks;
                    }

                    _logger.Info("No single copy of {0} found; grabbing bundle {1} as last resort",
                        string.Join(", ", openBooks.Select(b => b.Title)),
                        remoteBook.Release.Title);

                    var result = await ProcessDecisionInternal(report, null, talearrFailures);

                    switch (result)
                    {
                        case ProcessedDecisionResult.Grabbed:
                            grabbed.Add(report);
                            break;
                        case ProcessedDecisionResult.Rejected:
                            rejected.Add(report);
                            break;
                        case ProcessedDecisionResult.Failed:
                            if (downloadProtocol == DownloadProtocol.Usenet)
                            {
                                usenetFailed = true;
                            }
                            else if (downloadProtocol == DownloadProtocol.Torrent)
                            {
                                torrentFailed = true;
                            }

                            break;
                    }
                }
            }

            if (pendingAddQueue.Any())
            {
                _pendingReleaseService.AddMany(pendingAddQueue);
            }

            // Talearr: nothing could be sent for a book because of transient failures: schedule a short re-search.
            if (talearrFailures.Any())
            {
                try
                {
                    _talearrGrabRetry?.ReportSearchOutcome(talearrFailures, grabbed, pending);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to schedule the Talearr re-search after failed grabs");
                }
            }

            // Talearr: the "Why" tab story for each book in this pass (never throws).
            _talearrDecisionRecorder?.RecordSearch(decisions, grabbed, pending);

            return new ProcessedDecisions(grabbed, pending, rejected);
        }

        public async Task<ProcessedDecisionResult> ProcessDecision(DownloadDecision decision, int? downloadClientId)
        {
            if (decision == null)
            {
                return ProcessedDecisionResult.Skipped;
            }

            if (!IsQualifiedReport(decision))
            {
                return ProcessedDecisionResult.Rejected;
            }

            if (decision.TemporarilyRejected)
            {
                _pendingReleaseService.Add(decision, PendingReleaseReason.Delay);

                return ProcessedDecisionResult.Pending;
            }

            var result = await ProcessDecisionInternal(decision, downloadClientId);

            if (result == ProcessedDecisionResult.Pending)
            {
                _pendingReleaseService.Add(decision, PendingReleaseReason.Delay);
            }
            else if (result == ProcessedDecisionResult.Failed)
            {
                _pendingReleaseService.Add(decision, PendingReleaseReason.DownloadClientUnavailable);
            }

            return result;
        }

        internal List<DownloadDecision> GetQualifiedReports(IEnumerable<DownloadDecision> decisions)
        {
            return decisions.Where(IsQualifiedReport).ToList();
        }

        internal bool IsQualifiedReport(DownloadDecision decision)
        {
            // Process both approved and temporarily rejected
            return (decision.Approved || decision.TemporarilyRejected) && decision.RemoteBook.Books.Any();
        }

        private static bool IsBundleFallback(DownloadDecision decision)
        {
            return decision?.RemoteBook?.IsBundleFallback == true;
        }

        private bool IsBookProcessed(List<DownloadDecision> decisions, DownloadDecision report)
        {
            var bookIds = report.RemoteBook.Books.Select(e => e.Id).ToList();

            return decisions.SelectMany(r => r.RemoteBook.Books)
                            .Select(e => e.Id)
                            .ToList()
                            .Intersect(bookIds)
                            .Any();
        }

        private void PreparePending(List<Tuple<DownloadDecision, PendingReleaseReason>> queue, List<DownloadDecision> grabbed, List<DownloadDecision> pending, DownloadDecision report, PendingReleaseReason reason)
        {
            // If a release was already grabbed with matching books we should store it as a fallback
            // and filter it out the next time it is processed.
            // If a higher quality release failed to add to the download client, but a lower quality release
            // was sent to another client we still list it normally so it apparent that it'll grab next time.
            // Delayed is treated the same, but only the first is listed the subsequent items as stored as Fallback.
            if (IsBookProcessed(grabbed, report) ||
                IsBookProcessed(pending, report))
            {
                reason = PendingReleaseReason.Fallback;
            }

            queue.Add(Tuple.Create(report, reason));
            pending.Add(report);
        }

        private async Task<ProcessedDecisionResult> ProcessDecisionInternal(DownloadDecision decision, int? downloadClientId = null, List<TalearrFailedGrab> talearrFailures = null)
        {
            var remoteBook = decision.RemoteBook;

            try
            {
                _logger.Trace("Grabbing from Indexer {0} at priority {1}.", remoteBook.Release.Indexer, remoteBook.Release.IndexerPriority);
                await _downloadService.DownloadReport(remoteBook, downloadClientId);

                return ProcessedDecisionResult.Grabbed;
            }
            catch (MamUnsatisfiedSlotsUnavailableException ex)
            {
                _logger.Debug(ex, "MAM has no safely available unsatisfied-torrent slot; storing release until later. " + remoteBook);

                return ProcessedDecisionResult.Pending;
            }
            catch (ReleaseUnavailableException)
            {
                _logger.Warn("Failed to download release from indexer, no longer available. " + remoteBook);
                return ProcessedDecisionResult.Rejected;
            }
            catch (Exception ex)
            {
                if (ex is DownloadClientUnavailableException || ex is DownloadClientAuthenticationException)
                {
                    _logger.Debug(ex,
                        "Failed to send release to download client, storing until later. " + remoteBook);

                    return ProcessedDecisionResult.Failed;
                }
                else
                {
                    _logger.Warn(ex, "Couldn't add report to download queue. " + remoteBook);

                    // Talearr: remember transient failures (429, 5xx, timeout) for the re-search.
                    TalearrGrabRetryService.RecordFailure(talearrFailures, decision, ex, _logger);

                    return ProcessedDecisionResult.Skipped;
                }
            }
        }
    }
}
