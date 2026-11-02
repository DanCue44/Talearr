using System;
using System.Linq;
using NLog;
using NzbDrone.Core.History;
using NzbDrone.Core.Queue;

namespace NzbDrone.Core.Talearr.GrabbedReleases
{
    public interface IGrabbedReleaseService
    {
        GrabbedReleaseLookup ForBook(int bookId);
    }

    // Talearr: one history query and one queue read per manual search, never one per release
    public class GrabbedReleaseService : IGrabbedReleaseService
    {
        private readonly IHistoryService _historyService;
        private readonly IQueueService _queueService;
        private readonly Logger _logger;

        public GrabbedReleaseService(IHistoryService historyService, IQueueService queueService, Logger logger)
        {
            _historyService = historyService;
            _queueService = queueService;
            _logger = logger;
        }

        public GrabbedReleaseLookup ForBook(int bookId)
        {
            try
            {
                var history = _historyService.GetByBook(bookId, null);
                var queue = (_queueService.GetQueue() ?? Enumerable.Empty<NzbDrone.Core.Queue.Queue>())
                    .Where(q => q != null && (q.Book?.Id == bookId || (q.TargetBookIds?.Contains(bookId) ?? false)))
                    .Select(q => new GrabbedReleaseLookup.QueueEntry
                    {
                        DownloadId = q.DownloadId,
                        State = q.TrackedDownloadState,
                        DownloadClient = q.DownloadClient,
                        Added = q.Added
                    });

                return GrabbedReleaseLookup.Build(history, queue);
            }
            catch (Exception ex)
            {
                // The marks are a help, never a reason for the search itself to fail
                _logger.Debug(ex, "Could not look up earlier grabs for book {0}", bookId);
                return GrabbedReleaseLookup.Empty;
            }
        }
    }
}
