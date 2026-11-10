using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: fails a download that sits paused in its client with nothing changing. NZBGet
    // leaves a release paused for good when all that is left are its held-back par2 repair
    // files (a release that holds no book, only an inner .nzb and par2s, ends up that way).
    // The queue showed it as downloading and nothing ever searched again. After StalledFor
    // with the same remaining size the download is failed, which blocklists the release and,
    // through RedownloadFailedDownloadService, searches for the next one (capped by
    // TalearrRetryService). Only items the client itself reports as paused on their own count
    // (DownloadClientItem.ItemPaused): a paused client or "add paused" downloads never do.
    // Explicit user grabs get a queue warning instead.
    public class TalearrStalledDownloadService : IHandle<TrackedDownloadRefreshedEvent>, IHandle<TrackedDownloadsRemovedEvent>
    {
        public static readonly TimeSpan StalledFor = TimeSpan.FromHours(6);

        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, PausedSince> _paused = new Dictionary<string, PausedSince>(StringComparer.OrdinalIgnoreCase);

        public TalearrStalledDownloadService(IFailedDownloadService failedDownloadService,
                                             IHistoryService historyService,
                                             ITalearrGrabPolicyService grabPolicyService,
                                             Logger logger)
            : this(failedDownloadService, historyService, grabPolicyService, logger, () => DateTime.UtcNow)
        {
        }

        internal TalearrStalledDownloadService(IFailedDownloadService failedDownloadService,
                                               IHistoryService historyService,
                                               ITalearrGrabPolicyService grabPolicyService,
                                               Logger logger,
                                               Func<DateTime> clock)
        {
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _grabPolicyService = grabPolicyService;
            _logger = logger;
            _clock = clock;
        }

        public void Handle(TrackedDownloadRefreshedEvent message)
        {
            var now = _clock();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var enabled = TalearrGrabPolicyService.Resolve(_grabPolicyService).FailStalledDownloads;

            foreach (var trackedDownload in message?.TrackedDownloads ?? new List<TrackedDownload>())
            {
                var item = trackedDownload?.DownloadItem;
                if (item == null || item.DownloadId == null ||
                    trackedDownload.State != TrackedDownloadState.Downloading ||
                    item.Status != DownloadItemStatus.Paused || !item.ItemPaused)
                {
                    continue;
                }

                seen.Add(item.DownloadId);

                PausedSince since;
                lock (_paused)
                {
                    if (!_paused.TryGetValue(item.DownloadId, out since) || since.RemainingSize != item.RemainingSize)
                    {
                        since = new PausedSince { Since = now, RemainingSize = item.RemainingSize };
                        _paused[item.DownloadId] = since;
                    }
                }

                if (enabled && now - since.Since >= StalledFor)
                {
                    Fail(trackedDownload);
                }
            }

            lock (_paused)
            {
                foreach (var gone in _paused.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _paused.Remove(gone);
                }
            }
        }

        public void Handle(TrackedDownloadsRemovedEvent message)
        {
            lock (_paused)
            {
                foreach (var trackedDownload in message?.TrackedDownloads ?? new List<TrackedDownload>())
                {
                    if (trackedDownload?.DownloadItem?.DownloadId != null)
                    {
                        _paused.Remove(trackedDownload.DownloadItem.DownloadId);
                    }
                }
            }
        }

        private void Fail(TrackedDownload trackedDownload)
        {
            var hours = (int)StalledFor.TotalHours;
            var client = trackedDownload.DownloadItem.DownloadClientInfo?.Type ?? "the download client";

            if (TalearrImportRejectionRetryService.IsUserGrab(trackedDownload, _historyService, _logger))
            {
                trackedDownload.Warn("Paused in {0} for over {1} hours with nothing changing. Resume it there, or remove it here to blocklist it.", client, hours);
                return;
            }

            var failure = string.Format("Stalled: paused in {0} for over {1} hours with nothing changing", client, hours);
            _logger.Warn("{0} for {1}; marking failed so it can be replaced.", failure, trackedDownload.DownloadItem.Title);

            try
            {
                _failedDownloadService.MarkAsFailed(trackedDownload, failure);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to mark stalled download {0} as failed", trackedDownload.DownloadItem.Title);
            }

            lock (_paused)
            {
                _paused.Remove(trackedDownload.DownloadItem.DownloadId);
            }
        }

        private sealed class PausedSince
        {
            public DateTime Since { get; set; }
            public long RemainingSize { get; set; }
        }
    }
}
