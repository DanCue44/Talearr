using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Comics.Import
{
    // Talearr: a finished download that failed to import before this version of Talearr was built
    // (an archive it could not open yet, say) is tried again once at startup while its files are
    // still on disk and its volumes are still missing. A retry that fails again is newer than the
    // build, so it waits for the next update.
    public class ComicImportRetryService : IHandle<ApplicationStartedEvent>
    {
        private readonly ITalearrComicsDatabase _database;
        private readonly ITalearrComicsStore _store;
        private readonly IManageCommandQueue _commandQueue;
        private readonly Logger _logger;

        public ComicImportRetryService(ITalearrComicsDatabase database, ITalearrComicsStore store, IManageCommandQueue commandQueue, Logger logger)
        {
            _database = database;
            _store = store;
            _commandQueue = commandQueue;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            // No comics database means nobody has used comics: nothing to retry, nothing created.
            if (!File.Exists(_database.DatabasePath))
            {
                return;
            }

            try
            {
                foreach (var download in Candidates(_store.GetDownloads(), BuildInfo.BuildDateTime, Directory.Exists, MissingVolumes))
                {
                    _logger.Info("Comics: trying the import of {0} again", download.ReleaseTitle);
                    _commandQueue.Push(new ImportComicDownloadCommand { DownloadId = download.DownloadId });
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Comics: could not look for imports to try again");
            }
        }

        // Failed after reaching the disk (it has an output folder), before this build, folder still
        // there, and some of its volumes still monitored and missing. One per folder.
        public static List<ComicDownload> Candidates(IEnumerable<ComicDownload> downloads,
                                                     DateTime builtAt,
                                                     Func<string, bool> folderExists,
                                                     Func<ComicDownload, bool> stillMissing)
        {
            return downloads
                .Where(d => d.Status == ComicDownloadStatuses.Failed &&
                            !string.IsNullOrWhiteSpace(d.OutputPath) &&
                            d.UpdatedAt < builtAt)
                .GroupBy(d => d.OutputPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(d => d.GrabbedAt).First())
                .Where(d => folderExists(d.OutputPath) && stillMissing(d))
                .ToList();
        }

        private bool MissingVolumes(ComicDownload download)
        {
            var series = _store.GetSeries(download.SeriesId);
            if (series == null)
            {
                return false;
            }

            var have = new HashSet<int>(_store.GetFiles(series.Id).Select(f => f.VolumeId));
            return download.GetVolumeIds()
                .Select(id => _store.GetVolume(id))
                .Any(v => v != null && v.Monitored && !have.Contains(v.Id));
        }
    }
}
