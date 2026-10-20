using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: when an import is rejected for a reason that retrying can never fix (the
    // release is the wrong book, is in the wrong language, has unusable archives, or holds no
    // audio or ebook files at all), fail the download.
    // Failing it blocklists the release and, through RedownloadFailedDownloadService, searches
    // every indexer again for the next-best release (capped by TalearrRetryService).
    // Explicit user grabs are left alone, the same rule upstream uses for payload mismatches.
    public class TalearrImportRejectionRetryService : IHandle<BookImportIncompleteEvent>
    {
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly Logger _logger;

        public TalearrImportRejectionRetryService(IFailedDownloadService failedDownloadService,
                                                  IHistoryService historyService,
                                                  ITalearrGrabPolicyService grabPolicyService,
                                                  Logger logger)
        {
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _grabPolicyService = grabPolicyService;
            _logger = logger;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookImportIncompleteEvent message)
        {
            var trackedDownload = message?.TrackedDownload;
            if (trackedDownload?.DownloadItem == null ||
                trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            if (!TalearrGrabPolicyService.Resolve(_grabPolicyService).FailRejectedImports)
            {
                return;
            }

            if (IsUserGrab(trackedDownload, _historyService, _logger))
            {
                _logger.Debug("Import of {0} was rejected but it was an explicit user grab; leaving it in the queue", trackedDownload.DownloadItem.Title);
                return;
            }

            var reason = ImportRejectionClassifier.Classify(trackedDownload.StatusMessages);
            if (reason == null)
            {
                return;
            }

            var failure = "Import rejected: " + reason;
            _logger.Warn("{0} for {1}; marking failed so it can be replaced.", failure, trackedDownload.DownloadItem.Title);

            try
            {
                _failedDownloadService.MarkAsFailed(trackedDownload, failure);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to mark {0} as failed after a rejected import", trackedDownload.DownloadItem.Title);
            }
        }

        // Also used by TalearrUpgradeCooldownService.
        internal static bool IsUserGrab(TrackedDownload trackedDownload, IHistoryService historyService, Logger logger)
        {
            if (trackedDownload.DownloadItem.DownloadForced)
            {
                return true;
            }

            try
            {
                var grab = historyService
                    .Find(trackedDownload.DownloadItem.DownloadId, EntityHistoryEventType.Grabbed)
                    .OrderByDescending(h => h.Date)
                    .FirstOrDefault();

                var source = grab?.Data?.FirstOrDefault(kv => string.Equals(kv.Key, EntityHistory.RELEASE_SOURCE, StringComparison.OrdinalIgnoreCase)).Value;
                var forced = grab?.Data?.FirstOrDefault(kv => string.Equals(kv.Key, "DownloadForced", StringComparison.OrdinalIgnoreCase)).Value;

                return string.Equals(source, "InteractiveSearch", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(forced, bool.TrueString, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                logger.Debug(ex, "Unable to read grab history for {0}", trackedDownload.DownloadItem.Title);
                return true;
            }
        }
    }
}
