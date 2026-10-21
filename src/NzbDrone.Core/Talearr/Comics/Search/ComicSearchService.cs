using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    public class ComicSearchResult
    {
        public List<ComicReleaseDecision> Decisions { get; set; } = new List<ComicReleaseDecision>();
        public List<string> Notices { get; set; } = new List<string>();
        public int Grabbed { get; set; }
    }

    public interface IComicSearchService
    {
        // Interactive: every release found for the volume, with why each was or was not taken.
        ComicSearchResult InteractiveSearch(int volumeId);

        // Automatic: searches the given wanted volumes (or every missing monitored volume of the
        // series) and grabs the best releases.
        ComicSearchResult AutomaticSearch(int seriesId, IEnumerable<int> volumeIds = null);
        int SearchAllMissing();

        // Grabs a release from the last interactive search.
        string Grab(string guid, int indexerId);
    }

    // Talearr: comics reuse the user's indexers and download clients. Prowlarr-synced Newznab and
    // Torznab indexers are asked for the comics categories (7030 by default) with a plain text
    // query; the releases are judged by ComicReleaseEvaluator and sent to the same download
    // clients books use (their ebook category). Downloads are tracked in talearr-comics.db.
    public class ComicSearchService : IComicSearchService
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, (ComicReleaseDecision Decision, int SeriesId, DateTime At)> Cache = new ConcurrentDictionary<string, (ComicReleaseDecision, int, DateTime)>();

        private readonly ITalearrComicsStore _store;
        private readonly IComicSettingsService _settingsService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly ISeedConfigProvider _seedConfigProvider;
        private readonly IComicDownloadTracker _tracker;
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public ComicSearchService(ITalearrComicsStore store,
                                  IComicSettingsService settingsService,
                                  IIndexerFactory indexerFactory,
                                  IProvideDownloadClient downloadClientProvider,
                                  ISeedConfigProvider seedConfigProvider,
                                  IComicDownloadTracker tracker,
                                  IHttpClient httpClient,
                                  Logger logger)
        {
            _store = store;
            _settingsService = settingsService;
            _indexerFactory = indexerFactory;
            _downloadClientProvider = downloadClientProvider;
            _seedConfigProvider = seedConfigProvider;
            _tracker = tracker;
            _httpClient = httpClient;
            _logger = logger;
        }

        public ComicSearchResult InteractiveSearch(int volumeId)
        {
            var volume = _store.GetVolume(volumeId) ?? throw new ComicsException("No such volume");
            var series = _store.GetSeries(volume.SeriesId) ?? throw new ComicsException("No such series");
            var context = BuildContext(series, new[] { volume.Id }, onlyMissing: false);

            var result = new ComicSearchResult();
            var releases = Query(_indexerFactory.InteractiveSearchEnabled(), VolumeQueries(series, volume), result.Notices);
            result.Decisions = ComicReleaseEvaluator.Prioritize(releases.Select(r => ComicReleaseEvaluator.Evaluate(r, context)))
                .Concat(Enumerable.Empty<ComicReleaseDecision>())
                .OrderByDescending(d => d.Accepted)
                .ToList();

            PruneCache();
            foreach (var decision in result.Decisions)
            {
                Cache[CacheKey(decision.Release.Guid, decision.Release.IndexerId)] = (decision, series.Id, DateTime.UtcNow);
            }

            return result;
        }

        public ComicSearchResult AutomaticSearch(int seriesId, IEnumerable<int> volumeIds = null)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");
            var result = new ComicSearchResult();

            var wanted = volumeIds?.ToList() ?? MissingVolumes(series).Select(v => v.Id).ToList();
            var context = BuildContext(series, wanted, onlyMissing: true);
            if (context.WantedVolumeIds.Count == 0)
            {
                result.Notices.Add("Nothing to search for: every monitored volume is downloaded or on its way.");
                return result;
            }

            var indexers = _indexerFactory.AutomaticSearchEnabled();
            var releases = Query(indexers, SeriesQueries(series), result.Notices);
            var decisions = releases.Select(r => ComicReleaseEvaluator.Evaluate(r, context)).ToList();

            // Old volumes rarely show up in a broad search, so ask for the still uncovered ones by
            // number (a few at a time, to spare the indexers' daily limits).
            var covered = new HashSet<int>(decisions.Where(d => d.Accepted).SelectMany(d => d.VolumeIds));
            var uncovered = context.Volumes.Where(v => context.WantedVolumeIds.Contains(v.Id) && !covered.Contains(v.Id)).Take(5).ToList();
            foreach (var volume in uncovered)
            {
                var more = Query(indexers, VolumeQueries(series, volume).Take(2), result.Notices);
                decisions.AddRange(more.Where(r => decisions.All(d => d.Release.Guid != r.Guid)).Select(r => ComicReleaseEvaluator.Evaluate(r, context)));
            }

            result.Decisions = decisions;

            foreach (var pick in ComicReleaseEvaluator.PickBest(decisions))
            {
                try
                {
                    GrabDecision(pick, series, interactive: false);
                    result.Grabbed++;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Comics: could not send {0} to the download client", pick.Release.Title);
                    result.Notices.Add($"Could not send {pick.Release.Title}: {ex.Message}");
                }
            }

            return result;
        }

        public int SearchAllMissing()
        {
            var settings = _settingsService.Get();
            if (!settings.AutomaticSearch)
            {
                return 0;
            }

            var grabbed = 0;
            foreach (var series in _store.GetAllSeries().Where(s => s.Monitored))
            {
                if (!MissingVolumes(series).Any())
                {
                    continue;
                }

                try
                {
                    grabbed += AutomaticSearch(series.Id).Grabbed;
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Comics: search for {0} failed", series.Title);
                }
            }

            return grabbed;
        }

        public string Grab(string guid, int indexerId)
        {
            if (!Cache.TryGetValue(CacheKey(guid, indexerId), out var cached))
            {
                throw new ComicsException("That search result has expired. Search again.");
            }

            var series = _store.GetSeries(cached.SeriesId) ?? throw new ComicsException("No such series");
            if (!cached.Decision.VolumeIds.Any())
            {
                // An interactive grab of a release Talearr would not have picked: file it under the
                // volume it was searched for, if the number reads.
                throw new ComicsException("Talearr cannot tell which volume this release holds, so it cannot import it.");
            }

            return GrabDecision(cached.Decision, series, interactive: true);
        }

        private string GrabDecision(ComicReleaseDecision decision, ComicSeries series, bool interactive)
        {
            var release = decision.Release;
            var definition = _indexerFactory.Get(release.IndexerId);
            var indexer = _indexerFactory.GetInstance(definition);
            var client = _downloadClientProvider.GetDownloadClient(release.DownloadProtocol, BookMediaType.Ebook, release.IndexerId, filterBlockedClients: true);

            if (client == null)
            {
                throw new ComicsException("No {0} download client is set up and available.", release.DownloadProtocol);
            }

            var volumes = decision.VolumeIds.Select(id => _store.GetVolume(id)).Where(v => v != null).ToList();

            // Download clients expect a book: a stand-in ebook keeps their category (ebooks) and
            // their "recent" priority working. Nothing here touches the books library.
            var remoteBook = new RemoteBook
            {
                Release = release,
                ParsedBookInfo = new ParsedBookInfo { BookTitle = series.Title, Quality = new QualityModel(Quality.Unknown) },
                Author = new Author { Name = series.Title },
                Books = new List<Book>
                {
                    new Book
                    {
                        Title = series.Title,
                        MediaType = BookMediaType.Ebook,
                        ReleaseDate = volumes.Select(v => v.ReleaseDate).FirstOrDefault(d => d.HasValue) ?? DateTime.UtcNow.AddYears(-1)
                    }
                },
                ReleaseSource = interactive ? ReleaseSourceType.InteractiveSearch : ReleaseSourceType.Search,
                IsComic = true
            };

            try
            {
                remoteBook.SeedConfiguration = _seedConfigProvider.GetSeedConfiguration(remoteBook);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Comics: no seed settings for {0}", release.Title);
            }

            var downloadId = client.Download(remoteBook, indexer).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                throw new ComicsException("{0} took the release but gave no id, so Talearr cannot follow it. Use a client such as SABnzbd, NZBGet or qBittorrent.", client.Definition.Name);
            }

            _store.SaveDownload(new ComicDownload
            {
                DownloadId = downloadId,
                DownloadClientId = client.Definition.Id,
                Protocol = release.DownloadProtocol.ToString().ToLowerInvariant(),
                SeriesId = series.Id,
                VolumeIds = string.Join(",", decision.VolumeIds),
                ReleaseTitle = release.Title,
                Indexer = release.Indexer,
                Score = decision.Score,
                Status = ComicDownloadStatuses.Grabbed,
                GrabbedAt = DateTime.UtcNow
            });

            _tracker.Track(downloadId);

            foreach (var volume in volumes)
            {
                _store.AddHistory(new ComicHistoryEntry
                {
                    SeriesId = series.Id,
                    VolumeId = volume.Id,
                    EventType = ComicHistoryEvents.Grabbed,
                    SourceTitle = release.Title,
                    Message = $"Sent to {client.Definition.Name} from {release.Indexer}"
                });
            }

            _logger.Info("Comics: grabbed {0} for {1} ({2})", release.Title, series.Title, string.Join(", ", decision.VolumeNumbers.Select(n => "v" + ComicNaming.FormatNumber(n, "00"))));
            return downloadId;
        }

        private ComicEvaluationContext BuildContext(ComicSeries series, IEnumerable<int> wantedVolumeIds, bool onlyMissing)
        {
            var volumes = _store.GetVolumes(series.Id);
            var wanted = new HashSet<int>(wantedVolumeIds);

            if (onlyMissing)
            {
                var have = new HashSet<int>(_store.GetFiles(series.Id).Select(f => f.VolumeId));
                var downloading = new HashSet<int>(_store.GetDownloads(series.Id)
                    .Where(d => d.Status == ComicDownloadStatuses.Grabbed || d.Status == ComicDownloadStatuses.Completed || d.Status == ComicDownloadStatuses.Importing)
                    .SelectMany(d => d.GetVolumeIds()));
                wanted.RemoveWhere(id => have.Contains(id) || downloading.Contains(id));
            }

            return new ComicEvaluationContext
            {
                Series = series,
                Volumes = volumes,
                WantedVolumeIds = wanted,
                Settings = _settingsService.Get(),
                BlocklistedTitles = new HashSet<string>(_store.GetBlocklist(series.Id).Select(b => b.SourceTitle), StringComparer.OrdinalIgnoreCase)
            };
        }

        private List<ComicVolume> MissingVolumes(ComicSeries series)
        {
            var have = new HashSet<int>(_store.GetFiles(series.Id).Select(f => f.VolumeId));
            var now = DateTime.UtcNow;
            return _store.GetVolumes(series.Id)
                .Where(v => v.Monitored && !have.Contains(v.Id) && (!v.ReleaseDate.HasValue || v.ReleaseDate <= now))
                .ToList();
        }

        private static IEnumerable<string> SeriesQueries(ComicSeries series)
        {
            return SearchTitles(series).Take(2);
        }

        private static IEnumerable<string> VolumeQueries(ComicSeries series, ComicVolume volume)
        {
            var title = SearchTitles(series).First();
            var padded = ComicNaming.FormatNumber(volume.Number, "00");
            yield return $"{title} v{padded}";
            yield return $"{title} vol {padded}";
            yield return title;
        }

        // Indexers match words, so punctuation is dropped ("Frieren: Beyond Journey's End" is
        // found as "Frieren Beyond Journey's End").
        private static IEnumerable<string> SearchTitles(ComicSeries series)
        {
            return series.AllTitles()
                .Where(t => !string.IsNullOrWhiteSpace(t) && t.All(c => c < 0x2E80))
                .Select(t => Regex.Replace(t.Replace("×", " x "), @"[^\w\s']", " "))
                .Select(t => Regex.Replace(t, @"\s+", " ").Trim())
                .Where(t => t.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .DefaultIfEmpty(series.Title);
        }

        private List<ReleaseInfo> Query(IEnumerable<IIndexer> indexers, IEnumerable<string> queries, List<string> notices)
        {
            var settings = _settingsService.Get();
            var categories = string.Join(",", settings.Categories);
            var releases = new List<ReleaseInfo>();
            var indexerList = indexers.ToList();
            var usable = indexerList.Where(i => i.Definition.Settings is NewznabSettings).ToList();

            if (usable.Count == 0)
            {
                notices.Add(indexerList.Any()
                    ? "Comics search uses Newznab and Torznab indexers (for example from Prowlarr); none are enabled for this kind of search."
                    : "No indexers are enabled for searching.");
                return releases;
            }

            foreach (var query in queries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var indexer in usable)
                {
                    try
                    {
                        releases.AddRange(QueryIndexer(indexer, (NewznabSettings)indexer.Definition.Settings, categories, query));
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Comics: {0} search for '{1}' failed", indexer.Definition.Name, query);
                        var note = $"{indexer.Definition.Name}: {ex.Message}";
                        if (!notices.Contains(note))
                        {
                            notices.Add(note);
                        }
                    }
                }
            }

            return releases
                .GroupBy(r => (r.Guid ?? r.DownloadUrl) + "|" + r.IndexerId)
                .Select(g => g.First())
                .ToList();
        }

        private IEnumerable<ReleaseInfo> QueryIndexer(IIndexer indexer, NewznabSettings settings, string categories, string query)
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}{settings.ApiPath.TrimEnd('/')}?t=search&cat={categories}&extended=1{settings.AdditionalParameters}&limit=100&q={Uri.EscapeDataString(query)}";
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                url += "&apikey=" + settings.ApiKey;
            }

            var request = new IndexerRequest(url, HttpAccept.Rss);
            request.HttpRequest.RequestTimeout = TimeSpan.FromSeconds(60);
            request.HttpRequest.SuppressHttpError = false;

            var response = _httpClient.Execute(request.HttpRequest);
            var parser = settings is TorznabSettings ? (RssParser)new TorznabRssParser() : new NewznabRssParser();
            var releases = parser.ParseResponse(new IndexerResponse(request, response));

            foreach (var release in releases)
            {
                release.IndexerId = indexer.Definition.Id;
                release.Indexer = indexer.Definition.Name;
                release.DownloadProtocol = indexer.Protocol;
                release.IndexerPriority = ((IndexerDefinition)indexer.Definition).Priority;
            }

            // Spread requests out a little; Prowlarr passes them on to the real indexers.
            Thread.Sleep(300);

            return releases;
        }

        private static string CacheKey(string guid, int indexerId)
        {
            return indexerId + "|" + guid;
        }

        private static void PruneCache()
        {
            var cutoff = DateTime.UtcNow - CacheLifetime;
            foreach (var key in Cache.Where(c => c.Value.At < cutoff).Select(c => c.Key).ToList())
            {
                Cache.TryRemove(key, out _);
            }
        }
    }
}
