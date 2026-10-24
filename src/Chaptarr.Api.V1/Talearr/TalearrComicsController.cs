using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Import;
using NzbDrone.Core.Talearr.Comics.Search;

namespace Chaptarr.Api.V1.Talearr
{
    public class ComicVolumeResource
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public decimal Number { get; set; }
        public string Title { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string CoverUrl { get; set; }
        public bool Monitored { get; set; }

        // downloaded, downloading, missing, unreleased or unmonitored.
        public string State { get; set; }
        public ComicFile File { get; set; }
    }

    public class ComicSeriesResource
    {
        public ComicSeries Series { get; set; }
        public string EffectiveAgeRating { get; set; }
        public string RootFolderPath { get; set; }
        public bool InKidsFolder { get; set; }
        public int VolumeCount { get; set; }
        public int DownloadedCount { get; set; }
        public int MissingCount { get; set; }
        public int DownloadingCount { get; set; }
        public long SizeOnDisk { get; set; }
        public List<ComicVolumeResource> Volumes { get; set; }
        public List<ComicDownload> Downloads { get; set; }
        public List<ComicHistoryEntry> History { get; set; }
    }

    public class ComicWantedResource
    {
        public ComicVolumeResource Volume { get; set; }
        public int SeriesId { get; set; }
        public string SeriesTitle { get; set; }
        public string Kind { get; set; }
        public string EffectiveAgeRating { get; set; }
        public bool InKidsFolder { get; set; }
    }

    public class ComicVolumeMonitorBody
    {
        public List<int> VolumeIds { get; set; }
        public bool Monitored { get; set; }
    }

    public class ComicVolumeRangeBody
    {
        public decimal From { get; set; }
        public decimal To { get; set; }
    }

    public class ComicGrabBody
    {
        public string Guid { get; set; }
        public int IndexerId { get; set; }
    }

    public class ComicReleaseResource
    {
        public string Guid { get; set; }
        public int IndexerId { get; set; }
        public string Indexer { get; set; }
        public string Title { get; set; }
        public long Size { get; set; }
        public int AgeDays { get; set; }
        public string Protocol { get; set; }
        public int? Seeders { get; set; }
        public string Source { get; set; }
        public string Format { get; set; }
        public string Group { get; set; }
        public List<decimal> Volumes { get; set; }
        public int Score { get; set; }
        public bool Accepted { get; set; }
        public List<string> Rejections { get; set; }
    }

    public class ComicSearchResource
    {
        public List<ComicReleaseResource> Releases { get; set; } = new List<ComicReleaseResource>();
        public List<string> Notices { get; set; } = new List<string>();
        public int Grabbed { get; set; }
    }

    public class ComicsStatusResource
    {
        public bool Available { get; set; }
        public string Reason { get; set; }
        public List<string> AgeRatings { get; set; }
    }

    // Talearr: comics and manga under /api/v1/talearr/comics (notes: TALEARR_COMICS_PLAN.md).
    // New routes only; the books API is unchanged.
    [V1ApiController("talearr/comics")]
    public class TalearrComicsController : Controller
    {
        private readonly ITalearrComicsDatabase _database;
        private readonly ITalearrComicsStore _store;
        private readonly IComicSeriesService _seriesService;
        private readonly IComicDiscoverService _discoverService;
        private readonly IComicPreviewService _previewService;
        private readonly IComicSettingsService _settingsService;
        private readonly IComicSearchService _searchService;
        private readonly IComicImportService _importService;
        private readonly IManageCommandQueue _commandQueue;
        private readonly IDiskProvider _diskProvider;

        public TalearrComicsController(ITalearrComicsDatabase database,
                                       ITalearrComicsStore store,
                                       IComicSeriesService seriesService,
                                       IComicDiscoverService discoverService,
                                       IComicPreviewService previewService,
                                       IComicSettingsService settingsService,
                                       IComicSearchService searchService,
                                       IComicImportService importService,
                                       IManageCommandQueue commandQueue,
                                       IDiskProvider diskProvider)
        {
            _database = database;
            _store = store;
            _seriesService = seriesService;
            _discoverService = discoverService;
            _previewService = previewService;
            _settingsService = settingsService;
            _searchService = searchService;
            _importService = importService;
            _commandQueue = commandQueue;
            _diskProvider = diskProvider;
        }

        [HttpGet("status")]
        public ComicsStatusResource Status()
        {
            var resource = new ComicsStatusResource { AgeRatings = ComicAgeRatings.Ordered.ToList() };
            try
            {
                _database.OpenConnection().Dispose();
                resource.Available = true;
            }
            catch (Exception ex)
            {
                resource.Reason = ex.Message;
            }

            return resource;
        }

        // Series

        [HttpGet("series")]
        public ActionResult<List<ComicSeriesResource>> GetSeries()
        {
            return Run(() =>
            {
                var volumes = _store.GetAllVolumes().ToLookup(v => v.SeriesId);
                var files = _store.GetAllFiles().ToLookup(f => f.SeriesId);
                var downloads = _store.GetDownloads().ToLookup(d => d.SeriesId);
                var folders = _store.GetRootFolders();
                var roots = folders.ToDictionary(r => r.Id, r => r.Path);
                var kids = new HashSet<int>(folders.Where(r => r.IsKids).Select(r => r.Id));

                return _store.GetAllSeries()
                    .Select(s => ToResource(s, volumes[s.Id].ToList(), files[s.Id].ToList(), downloads[s.Id].ToList(), roots, kids, includeVolumes: false))
                    .ToList();
            });
        }

        [HttpGet("series/{id:int}")]
        public ActionResult<ComicSeriesResource> GetSeries(int id)
        {
            return Run(() =>
            {
                var series = _store.GetSeries(id) ?? throw new ComicsException("No such series");
                var folders = _store.GetRootFolders();
                var roots = folders.ToDictionary(r => r.Id, r => r.Path);
                var kids = new HashSet<int>(folders.Where(r => r.IsKids).Select(r => r.Id));
                var resource = ToResource(series, _store.GetVolumes(id), _store.GetFiles(id), _store.GetDownloads(id), roots, kids, includeVolumes: true);
                resource.History = _store.GetHistory(id, 100);
                return resource;
            });
        }

        [HttpPost("series")]
        public ActionResult<ComicSeries> AddSeries([FromBody] ComicAddRequest request)
        {
            return Run(() =>
            {
                var series = _seriesService.Add(request);
                if (request.Monitor == "all" && _settingsService.Get().AutomaticSearch)
                {
                    _commandQueue.Push(new SearchMissingComicsCommand { SeriesId = series.Id });
                }

                return series;
            });
        }

        [HttpPut("series/{id:int}")]
        public ActionResult<ComicSeries> UpdateSeries(int id, [FromBody] ComicSeriesUpdate update)
        {
            return Run(() => _seriesService.Update(id, update));
        }

        [HttpDelete("series/{id:int}")]
        public ActionResult DeleteSeries(int id, [FromQuery] bool deleteFiles = false)
        {
            try
            {
                _seriesService.Delete(id, deleteFiles);
                return Ok();
            }
            catch (ComicsException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpPost("series/{id:int}/refresh")]
        public ActionResult<ComicSeries> RefreshSeries(int id)
        {
            return Run(() => _seriesService.Refresh(id));
        }

        [HttpPost("series/{id:int}/search")]
        public ActionResult<ComicSearchResource> SearchSeries(int id)
        {
            return Run(() => ToResource(_searchService.AutomaticSearch(id)));
        }

        [HttpPost("series/{id:int}/rescan")]
        public ActionResult<ComicImportResult> RescanSeries(int id)
        {
            return Run(() => _importService.RescanSeries(id));
        }

        [HttpPost("series/{id:int}/rename")]
        public ActionResult<ComicImportResult> RenameSeries(int id)
        {
            return Run(() => _importService.RenameSeries(id));
        }

        [HttpPost("series/{id:int}/volumes")]
        public ActionResult<List<ComicVolume>> AddVolumes(int id, [FromBody] ComicVolumeRangeBody body)
        {
            return Run(() => _seriesService.AddVolumes(id, body.From, body.To));
        }

        [HttpGet("lookup")]
        public ActionResult<ComicLookupResponse> Lookup([FromQuery] string term, [FromQuery] string kind)
        {
            return Run(() => _seriesService.Lookup(term, kind));
        }

        // Browse lists for request apps: trending, popular, releasing (AniList manga) and comics
        // (curated Western series). Same shape as lookup, 20 per page.
        [HttpGet("discover")]
        public ActionResult<ComicLookupResponse> Discover([FromQuery] string list, [FromQuery] int page = 1)
        {
            return Run(() => _discoverService.Discover(list, page));
        }

        // One series' detail page for request apps, by AniList id or Hardcover series id: facts,
        // genres, banner and the Hardcover volume list. 404 when the source has no such series.
        [HttpGet("preview")]
        public ActionResult<ComicPreview> Preview([FromQuery] int? aniListId, [FromQuery] int? hardcoverSeriesId, [FromQuery] string kind)
        {
            if (!aniListId.HasValue && !hardcoverSeriesId.HasValue)
            {
                return BadRequest(new { message = "Pass aniListId or hardcoverSeriesId." });
            }

            try
            {
                return _previewService.Preview(aniListId, hardcoverSeriesId, kind);
            }
            catch (ComicPreviewNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (ComicPreviewUnavailableException ex)
            {
                return StatusCode(503, new { message = ex.Message });
            }
            catch (Exception ex) when (ex is ComicsException || ex is ArgumentException)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Volumes

        [HttpPut("volumes/monitor")]
        public ActionResult MonitorVolumes([FromBody] ComicVolumeMonitorBody body)
        {
            _seriesService.SetVolumesMonitored(body.VolumeIds ?? new List<int>(), body.Monitored);
            return Ok();
        }

        [HttpPost("volumes/{id:int}/search")]
        public ActionResult<ComicSearchResource> SearchVolume(int id)
        {
            return Run(() =>
            {
                var volume = _store.GetVolume(id) ?? throw new ComicsException("No such volume");
                return ToResource(_searchService.AutomaticSearch(volume.SeriesId, new[] { id }));
            });
        }

        [HttpGet("volumes/{id:int}/releases")]
        public ActionResult<ComicSearchResource> InteractiveSearch(int id)
        {
            return Run(() => ToResource(_searchService.InteractiveSearch(id)));
        }

        [HttpPost("releases/grab")]
        public ActionResult<string> Grab([FromBody] ComicGrabBody body)
        {
            return Run(() => _searchService.Grab(body.Guid, body.IndexerId));
        }

        [HttpDelete("files/{id:int}")]
        public ActionResult DeleteFile(int id, [FromQuery] int seriesId)
        {
            var file = _store.GetFiles(seriesId).FirstOrDefault(f => f.Id == id);
            if (file == null)
            {
                return NotFound();
            }

            if (_diskProvider.FileExists(file.Path))
            {
                _diskProvider.DeleteFile(file.Path);
            }

            _store.DeleteFile(id);
            _store.AddHistory(new ComicHistoryEntry { SeriesId = seriesId, VolumeId = file.VolumeId, EventType = ComicHistoryEvents.Deleted, Message = Path.GetFileName(file.Path) });
            return Ok();
        }

        [HttpGet("wanted")]
        public ActionResult<List<ComicWantedResource>> Wanted()
        {
            return Run(() =>
            {
                var series = _store.GetAllSeries().ToDictionary(s => s.Id);
                var kidsFolders = new HashSet<int>(_store.GetRootFolders().Where(r => r.IsKids).Select(r => r.Id));
                var have = new HashSet<int>(_store.GetAllFiles().Select(f => f.VolumeId));
                var downloading = ActiveDownloadVolumes(_store.GetDownloads());
                var now = DateTime.UtcNow;

                return _store.GetAllVolumes()
                    .Where(v => v.Monitored && !have.Contains(v.Id) && series.TryGetValue(v.SeriesId, out var s) && s.Monitored)
                    .Where(v => !v.ReleaseDate.HasValue || v.ReleaseDate <= now)
                    .Select(v => new ComicWantedResource
                    {
                        Volume = ToResource(v, null, downloading.Contains(v.Id), now),
                        SeriesId = v.SeriesId,
                        SeriesTitle = series[v.SeriesId].Title,
                        Kind = series[v.SeriesId].Kind,
                        EffectiveAgeRating = series[v.SeriesId].EffectiveAgeRating,
                        InKidsFolder = kidsFolders.Contains(series[v.SeriesId].RootFolderId)
                    })
                    .OrderBy(w => w.SeriesTitle, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(w => w.Volume.Number)
                    .ToList();
            });
        }

        [HttpPost("search/missing")]
        public ActionResult SearchMissing()
        {
            _commandQueue.Push(new SearchMissingComicsCommand());
            return Ok();
        }

        [HttpGet("history")]
        public ActionResult<List<ComicHistoryEntry>> History([FromQuery] int? seriesId, [FromQuery] int limit = 200)
        {
            return Run(() => _store.GetHistory(seriesId, Math.Clamp(limit, 1, 1000)));
        }

        [HttpGet("blocklist")]
        public ActionResult<List<ComicBlocklistEntry>> Blocklist([FromQuery] int? seriesId)
        {
            return Run(() => _store.GetBlocklist(seriesId));
        }

        [HttpDelete("blocklist/{id:int}")]
        public ActionResult DeleteBlocklist(int id)
        {
            _store.DeleteBlocklist(id);
            return Ok();
        }

        // Root folders and settings

        [HttpGet("rootfolders")]
        public ActionResult<List<ComicRootFolder>> RootFolders()
        {
            return Run(() => _store.GetRootFolders());
        }

        [HttpPost("rootfolders")]
        public ActionResult<ComicRootFolder> AddRootFolder([FromBody] ComicRootFolder folder)
        {
            return Run(() =>
            {
                if (string.IsNullOrWhiteSpace(folder.Path) || !Path.IsPathRooted(folder.Path))
                {
                    throw new ComicsException("Give the full path of the folder, for example /manga");
                }

                if (!_diskProvider.FolderExists(folder.Path))
                {
                    throw new ComicsException("{0} does not exist (as Talearr sees it)", folder.Path);
                }

                folder.Kind = folder.Kind == "any" ? "any" : ComicKinds.Normalize(folder.Kind);
                folder.MaxAgeRating = ComicAgeRatings.IsKnown(folder.MaxAgeRating) ? ComicAgeRatings.Normalize(folder.MaxAgeRating) : null;
                folder.Path = folder.Path.TrimEnd('/', '\\');
                return _store.AddRootFolder(folder);
            });
        }

        [HttpPut("rootfolders/{id:int}")]
        public ActionResult<ComicRootFolder> UpdateRootFolder(int id, [FromBody] ComicRootFolder folder)
        {
            return Run(() =>
            {
                var existing = _store.GetRootFolder(id) ?? throw new ComicsException("No such root folder");
                existing.Kind = folder.Kind == "any" ? "any" : ComicKinds.Normalize(folder.Kind);
                existing.IsKids = folder.IsKids;
                existing.MaxAgeRating = ComicAgeRatings.IsKnown(folder.MaxAgeRating) ? ComicAgeRatings.Normalize(folder.MaxAgeRating) : null;
                _store.UpdateRootFolder(existing);
                return existing;
            });
        }

        [HttpDelete("rootfolders/{id:int}")]
        public ActionResult DeleteRootFolder(int id)
        {
            if (_store.GetAllSeries().Any(s => s.RootFolderId == id))
            {
                return BadRequest(new { message = "Series are filed in this folder. Remove them first." });
            }

            _store.DeleteRootFolder(id);
            return Ok();
        }

        [HttpGet("settings")]
        public ActionResult<ComicSettings> GetSettings()
        {
            return Run(() => _settingsService.Get());
        }

        [HttpPut("settings")]
        public ActionResult<ComicSettings> SaveSettings([FromBody] ComicSettings settings)
        {
            return Run(() => _settingsService.Save(settings));
        }

        private ActionResult<T> Run<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (ComicAgeRatingRequiredException ex)
            {
                return BadRequest(new { message = ex.Message, needsAgeRating = true });
            }
            catch (Exception ex) when (ex is ComicsException || ex is ArgumentException || ex is TalearrComicsDatabaseUnavailableException || ex is ComicArchiveException)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private static HashSet<int> ActiveDownloadVolumes(IEnumerable<ComicDownload> downloads)
        {
            return new HashSet<int>(downloads
                .Where(d => d.Status == ComicDownloadStatuses.Grabbed || d.Status == ComicDownloadStatuses.Completed || d.Status == ComicDownloadStatuses.Importing)
                .SelectMany(d => d.GetVolumeIds()));
        }

        private static ComicVolumeResource ToResource(ComicVolume volume, ComicFile file, bool downloading, DateTime now)
        {
            string state;
            if (file != null)
            {
                state = "downloaded";
            }
            else if (downloading)
            {
                state = "downloading";
            }
            else if (volume.ReleaseDate.HasValue && volume.ReleaseDate > now)
            {
                state = "unreleased";
            }
            else
            {
                state = volume.Monitored ? "missing" : "unmonitored";
            }

            return new ComicVolumeResource
            {
                Id = volume.Id,
                SeriesId = volume.SeriesId,
                Number = volume.Number,
                Title = volume.Title,
                ReleaseDate = volume.ReleaseDate,
                CoverUrl = volume.CoverUrl,
                Monitored = volume.Monitored,
                State = state,
                File = file
            };
        }

        private static ComicSeriesResource ToResource(ComicSeries series, List<ComicVolume> volumes, List<ComicFile> files, List<ComicDownload> downloads, Dictionary<int, string> roots, HashSet<int> kids, bool includeVolumes)
        {
            var now = DateTime.UtcNow;
            var fileByVolume = files.GroupBy(f => f.VolumeId).ToDictionary(g => g.Key, g => g.First());
            var downloading = ActiveDownloadVolumes(downloads);
            var volumeResources = volumes.Select(v => ToResource(v, fileByVolume.GetValueOrDefault(v.Id), downloading.Contains(v.Id), now)).ToList();

            return new ComicSeriesResource
            {
                Series = series,
                EffectiveAgeRating = series.EffectiveAgeRating,
                RootFolderPath = roots.GetValueOrDefault(series.RootFolderId),
                InKidsFolder = kids.Contains(series.RootFolderId),
                VolumeCount = volumes.Count,
                DownloadedCount = volumeResources.Count(v => v.State == "downloaded"),
                MissingCount = volumeResources.Count(v => v.State == "missing"),
                DownloadingCount = volumeResources.Count(v => v.State == "downloading"),
                SizeOnDisk = files.Sum(f => f.Size),
                Volumes = includeVolumes ? volumeResources : null,
                Downloads = includeVolumes ? downloads.Take(50).ToList() : null
            };
        }

        private static ComicSearchResource ToResource(ComicSearchResult result)
        {
            var now = DateTime.UtcNow;
            return new ComicSearchResource
            {
                Notices = result.Notices,
                Grabbed = result.Grabbed,
                Releases = result.Decisions.Select(d => new ComicReleaseResource
                {
                    Guid = d.Release.Guid,
                    IndexerId = d.Release.IndexerId,
                    Indexer = d.Release.Indexer,
                    Title = d.Release.Title,
                    Size = d.Release.Size,
                    AgeDays = (int)Math.Max(0, (now - d.Release.PublishDate).TotalDays),
                    Protocol = d.Release.DownloadProtocol.ToString().ToLowerInvariant(),
                    Seeders = (d.Release as NzbDrone.Core.Parser.Model.TorrentInfo)?.Seeders,
                    Source = d.Parsed.Source,
                    Format = d.Parsed.Format,
                    Group = d.Parsed.Group,
                    Volumes = d.VolumeNumbers,
                    Score = d.Score,
                    Accepted = d.Accepted,
                    Rejections = d.Rejections
                }).ToList()
            };
        }
    }
}
