using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    public interface IComicDownloadTracker
    {
        // True when the download client item belongs to comics. Talearr's book import then leaves
        // it alone; comics follows it here and imports it when it completes.
        bool Claim(IDownloadClient client, DownloadClientItem item);
        void Track(string downloadId);
    }

    // Talearr: comic downloads land in the same download clients and categories as ebooks. The
    // download monitor hands every client item here first; items Talearr grabbed for comics are
    // claimed, so the book import never tries (and fails) to match them to a book.
    public class ComicDownloadTracker : IComicDownloadTracker
    {
        private static readonly TimeSpan KeepClaimed = TimeSpan.FromDays(60);
        private static readonly TimeSpan Reload = TimeSpan.FromMinutes(10);

        private readonly ITalearrComicsDatabase _database;
        private readonly ITalearrComicsStore _store;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private HashSet<string> _ids;
        private DateTime _loadedAt;

        public ComicDownloadTracker(ITalearrComicsDatabase database, ITalearrComicsStore store, IManageCommandQueue commandQueue, Logger logger)
        {
            _database = database;
            _store = store;
            _commandQueue = commandQueue;
            _logger = logger;
        }

        public void Track(string downloadId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                _ids?.Add(downloadId);
            }
        }

        public bool Claim(IDownloadClient client, DownloadClientItem item)
        {
            if (item?.DownloadId == null)
            {
                return false;
            }

            lock (_lock)
            {
                EnsureLoaded();
                if (_ids == null || !_ids.Contains(item.DownloadId))
                {
                    // Something in the comics category that Talearr did not grab (added by hand):
                    // not a book, so the book import leaves it alone too.
                    return InComicsCategory(client?.Definition?.Settings, item);
                }
            }

            try
            {
                Observe(item);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Comics: could not follow download {0}", item.Title);
            }

            return true;
        }

        public static bool InComicsCategory(object clientSettings, DownloadClientItem item)
        {
            if (clientSettings is not IComicsCategorySettings settings || string.IsNullOrWhiteSpace(settings.ComicsCategory) || string.IsNullOrWhiteSpace(item.Category))
            {
                return false;
            }

            var comics = settings.ComicsCategory.Trim();

            // A comics category equal to the ebook category means "no separate category".
            return item.Category.Trim().Equals(comics, StringComparison.OrdinalIgnoreCase) &&
                   !comics.Equals(settings.EbookCategory?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private void Observe(DownloadClientItem item)
        {
            var download = _store.GetDownload(item.DownloadId);
            if (download == null)
            {
                return;
            }

            if (item.Status == DownloadItemStatus.Completed && download.Status == ComicDownloadStatuses.Grabbed && item.OutputPath.FullPath != null)
            {
                download.Status = ComicDownloadStatuses.Completed;
                download.OutputPath = item.OutputPath.FullPath;
                download.Message = null;
                _store.SaveDownload(download);

                _commandQueue.Push(new ImportComicDownloadCommand { DownloadId = download.DownloadId });
                return;
            }

            if (item.Status == DownloadItemStatus.Failed && download.Status == ComicDownloadStatuses.Grabbed)
            {
                download.Status = ComicDownloadStatuses.Failed;
                download.Message = string.IsNullOrWhiteSpace(item.Message) ? "The download client reported a failure" : item.Message;
                _store.SaveDownload(download);

                // A failed release is not tried again; the next search picks another one.
                _store.AddBlocklist(new ComicBlocklistEntry { SeriesId = download.SeriesId, SourceTitle = download.ReleaseTitle, Indexer = download.Indexer, Reason = download.Message });

                foreach (var volumeId in download.GetVolumeIds().DefaultIfEmpty())
                {
                    _store.AddHistory(new ComicHistoryEntry
                    {
                        SeriesId = download.SeriesId,
                        VolumeId = volumeId == 0 ? null : volumeId,
                        EventType = ComicHistoryEvents.DownloadFailed,
                        SourceTitle = download.ReleaseTitle,
                        Message = download.Message
                    });
                }
            }
        }

        private void EnsureLoaded()
        {
            if (_ids != null && DateTime.UtcNow - _loadedAt < Reload)
            {
                return;
            }

            try
            {
                // No comics database yet (nobody used comics): nothing to claim, and no file is created.
                if (_ids == null && !System.IO.File.Exists(_database.DatabasePath))
                {
                    _ids = new HashSet<string>();
                    _loadedAt = DateTime.UtcNow;
                    return;
                }

                _ids = new HashSet<string>(_store.GetTrackedDownloadIds(DateTime.UtcNow - KeepClaimed));
                _loadedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // The comics database is unusable: claim nothing, books carry on.
                _logger.Debug(ex, "Comics: could not read comic downloads");
                _ids ??= new HashSet<string>();
                _loadedAt = DateTime.UtcNow;
            }
        }
    }
}
