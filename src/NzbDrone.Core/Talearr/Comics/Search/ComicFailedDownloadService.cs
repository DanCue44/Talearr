using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    public interface IComicFailedDownloadService
    {
        // The release behind this download is no good (the client failed it, or it finished with
        // nothing to import): mark it failed, blocklist it and search again for its volumes.
        void Failed(ComicDownload download, string eventType, string message);
    }

    // Talearr: comics' "redownload failed", as Sonarr and Readarr do it. A failed release is
    // blocklisted for its series, so no search grabs it again, and a search for the volumes it
    // was grabbed for is queued right away instead of waiting for the scheduled one.
    public class ComicFailedDownloadService : IComicFailedDownloadService
    {
        private readonly ITalearrComicsStore _store;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;

        public ComicFailedDownloadService(ITalearrComicsStore store, IManageCommandQueue commandQueue, Logger logger)
        {
            _store = store;
            _commandQueue = commandQueue;
            _logger = logger;
        }

        public void Failed(ComicDownload download, string eventType, string message)
        {
            download.Status = ComicDownloadStatuses.Failed;
            download.Message = message;
            _store.SaveDownload(download);

            if (!string.IsNullOrWhiteSpace(download.ReleaseTitle) &&
                !_store.GetBlocklist(download.SeriesId).Any(b => string.Equals(b.SourceTitle, download.ReleaseTitle, StringComparison.OrdinalIgnoreCase)))
            {
                _store.AddBlocklist(new ComicBlocklistEntry { SeriesId = download.SeriesId, SourceTitle = download.ReleaseTitle, Indexer = download.Indexer, Reason = message });
            }

            foreach (var volumeId in download.GetVolumeIds().DefaultIfEmpty())
            {
                _store.AddHistory(new ComicHistoryEntry
                {
                    SeriesId = download.SeriesId,
                    VolumeId = volumeId == 0 ? null : volumeId,
                    EventType = eventType,
                    SourceTitle = download.ReleaseTitle,
                    Message = message
                });
            }

            _logger.Warn("Comics: {0} failed and is blocklisted: {1}", download.ReleaseTitle, message);

            var volumeIds = VolumesToSearch(download);
            if (volumeIds.Any())
            {
                _commandQueue.Push(new SearchMissingComicsCommand { SeriesId = download.SeriesId, VolumeIds = volumeIds });
            }
        }

        // The grabbed volumes still monitored and without a file; the search itself skips any
        // that another download is already bringing.
        private List<int> VolumesToSearch(ComicDownload download)
        {
            var series = _store.GetSeries(download.SeriesId);
            if (series == null)
            {
                return new List<int>();
            }

            var have = new HashSet<int>(_store.GetFiles(series.Id).Select(f => f.VolumeId));
            return download.GetVolumeIds()
                .Select(id => _store.GetVolume(id))
                .Where(v => v != null && v.Monitored && !have.Contains(v.Id))
                .Select(v => v.Id)
                .ToList();
        }
    }
}
