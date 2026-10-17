using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace NzbDrone.Core.Talearr.Seeding
{
    public interface ITalearrSeedingService
    {
        // Rebuilds when the cached report is older than maxAge (default 30 seconds).
        TalearrSeedingReport GetReport(TimeSpan? maxAge = null);
    }

    // TaleArr: what every enabled torrent client is seeding for TaleArr, matched to the grab
    // history (book, indexer), with the private-tracker seeding requirement and a hit-and-run
    // risk per torrent. Read-only: nothing is changed in the clients and MAM is not called (the
    // MAM block comes from the hourly account refresh stored on the indexer).
    public class TalearrSeedingService : ITalearrSeedingService
    {
        public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(30);

        // Grabs older than this cannot be "missing before 72 h" any more.
        private static readonly TimeSpan MissingLookback = TimeSpan.FromDays(4);

        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IHistoryService _historyService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IMyAnonaMouseBonusPurchaseStore _bonusPurchases;
        private readonly ICached<TalearrSeedingReport> _cache;
        private readonly Logger _logger;
        private readonly object _buildLock = new object();

        public TalearrSeedingService(IProvideDownloadClient downloadClientProvider,
                                     IHistoryService historyService,
                                     IBookService bookService,
                                     IAuthorService authorService,
                                     IIndexerFactory indexerFactory,
                                     ICacheManager cacheManager,
                                     Logger logger,
                                     IMyAnonaMouseBonusPurchaseStore bonusPurchases = null)
        {
            _bonusPurchases = bonusPurchases;
            _downloadClientProvider = downloadClientProvider;
            _historyService = historyService;
            _bookService = bookService;
            _authorService = authorService;
            _indexerFactory = indexerFactory;
            _cache = cacheManager.GetCache<TalearrSeedingReport>(GetType(), "report");
            _logger = logger;
        }

        public TalearrSeedingReport GetReport(TimeSpan? maxAge = null)
        {
            var age = maxAge ?? DefaultMaxAge;

            lock (_buildLock)
            {
                var cached = _cache.Find("report");
                if (cached != null && DateTime.UtcNow - cached.GeneratedAt <= age)
                {
                    return cached;
                }

                var report = Build(DateTime.UtcNow);
                _cache.Set("report", report, TimeSpan.FromMinutes(15));

                return report;
            }
        }

        private TalearrSeedingReport Build(DateTime now)
        {
            var indexers = SafeIndexers();
            var clientRows = new List<(TalearrSeedingClientTorrent Torrent, IDownloadClient Client)>();
            var readClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var client in _downloadClientProvider.GetDownloadClients()
                         .Where(c => c.Protocol == DownloadProtocol.Torrent &&
                                     c.Name.IndexOf("Blackhole", StringComparison.OrdinalIgnoreCase) < 0))
            {
                try
                {
                    var torrents = client is ITalearrSeedingSource source
                        ? source.GetSeedingTorrents()
                        : client.GetItems().Select(FromQueueItem).ToList();

                    clientRows.AddRange(torrents.Select(t => (t, client)));
                    readClients.Add(client.Definition.Name);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Seeding report: unable to read torrents from {0}", client.Definition.Name);
                }
            }

            var grabs = LoadGrabs(clientRows.Select(r => r.Torrent.DownloadId), now - MissingLookback);
            var rows = new List<TalearrSeedingTorrent>();

            foreach (var (torrent, client) in clientRows)
            {
                grabs.TryGetValue(torrent.DownloadId, out var grab);

                if (grab == null && torrent.RequireHistoryMatch)
                {
                    continue;
                }

                var indexer = ResolveIndexer(indexers, grab?.IndexerName);
                var row = TalearrSeedingRules.BuildRow(torrent, client.Definition.Name, grab, indexer);

                string trackerError = null;
                if (client is ITalearrSeedingSource source && TalearrSeedingRules.NeedsTrackerCheck(row))
                {
                    try
                    {
                        trackerError = source.GetTrackerError(torrent.DownloadId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Seeding report: unable to read trackers for {0}", torrent.DownloadId);
                    }
                }

                TalearrSeedingRules.ApplyRisk(row, trackerError, torrent.ClientMessage);
                rows.Add(row);
            }

            // MAM grabs the client no longer has while they cannot have seeded 72 h yet.
            var present = new HashSet<string>(clientRows.Select(r => r.Torrent.DownloadId), StringComparer.OrdinalIgnoreCase);
            foreach (var grab in grabs.Values.Where(g => !present.Contains(g.DownloadId)))
            {
                var indexer = ResolveIndexer(indexers, grab.IndexerName);
                var isMam = indexer?.IsMam == true || TalearrSeedingRules.IsMamIndexerName(grab.IndexerName);

                if (!isMam ||
                    grab.DownloadClientName.IsNullOrWhiteSpace() ||
                    !readClients.Contains(grab.DownloadClientName) ||
                    grab.Date.AddSeconds(TalearrSeedingRules.MamRequiredSeedingSeconds) <= now)
                {
                    continue;
                }

                var row = TalearrSeedingRules.BuildMissingRow(grab, indexer);
                TalearrSeedingRules.ApplyRisk(row, null, null);
                rows.Add(row);
            }

            rows = rows
                .OrderByDescending(r => r.AtRisk)
                .ThenByDescending(r => r.AddedAt ?? DateTime.MinValue)
                .ToList();

            var mam = indexers.FirstOrDefault(d => d.Enable && d.Settings is MyAnonaMouseSettings);

            return new TalearrSeedingReport
            {
                GeneratedAt = now,
                Torrents = rows,
                Summary = TalearrSeedingRules.Summarize(rows),
                Mam = WithBonusPurchases(TalearrSeedingRules.BuildMamStatus(mam?.Settings as MyAnonaMouseSettings), mam)
            };
        }

        // TaleArr: the last bonus auto-spend attempts for the MAM summary ("Recent bonus purchases").
        private TalearrSeedingMamStatus WithBonusPurchases(TalearrSeedingMamStatus status, IndexerDefinition mam)
        {
            if (status == null || _bonusPurchases == null || mam == null)
            {
                return status;
            }

            try
            {
                status.BonusPurchases = _bonusPurchases.GetRecent(mam.Id, 10)
                    .Select(r => new TalearrSeedingBonusPurchase
                    {
                        AttemptedAt = DateTime.SpecifyKind(r.AttemptedAt, DateTimeKind.Utc),
                        SpendType = r.SpendType,
                        Parameters = r.Parameters,
                        EstimatedCost = r.EstimatedCost,
                        DryRun = r.DryRun,
                        Success = r.Success,
                        Message = r.Message,
                        Reason = r.Reason
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read MAM bonus purchases for the seeding report");
            }

            return status;
        }

        private static TalearrSeedingClientTorrent FromQueueItem(DownloadClientItem item)
        {
            var completed = item.Status == DownloadItemStatus.Completed;

            return new TalearrSeedingClientTorrent
            {
                DownloadId = item.DownloadId?.ToUpperInvariant(),
                Title = item.Title,
                Category = item.Category,
                State = TalearrSeedingRules.NormalizeItemStatus(item.Status),
                ClientMessage = item.Message,
                Size = item.TotalSize > 0 ? item.TotalSize : null,
                Uploaded = completed && item.SeedRatio.HasValue && item.TotalSize > 0
                    ? (long)(item.SeedRatio.Value * item.TotalSize)
                    : null,
                Ratio = item.SeedRatio
            };
        }

        private List<IndexerDefinition> SafeIndexers()
        {
            try
            {
                return _indexerFactory.All();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Seeding report: unable to read indexers");
                return new List<IndexerDefinition>();
            }
        }

        internal static TalearrSeedingIndexerInfo ResolveIndexer(List<IndexerDefinition> indexers, string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return null;
            }

            var definition = indexers.FirstOrDefault(d => name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
            {
                return new TalearrSeedingIndexerInfo
                {
                    Name = name,
                    IsMam = TalearrSeedingRules.IsMamIndexerName(name)
                };
            }

            var seedMinutes = (definition.Settings as ITorrentIndexerSettings)?.SeedCriteria?.SeedTime;

            return new TalearrSeedingIndexerInfo
            {
                Name = definition.Name,
                IsMam = definition.Settings is MyAnonaMouseSettings || TalearrSeedingRules.IsMamIndexerName(definition.Name),
                SeedTimeSeconds = seedMinutes is > 0 ? seedMinutes.Value * 60L : null
            };
        }

        // Latest TaleArr grab per download id: the torrents the clients hold plus recent grabs
        // (for the missing check).
        private Dictionary<string, TalearrSeedingGrab> LoadGrabs(IEnumerable<string> downloadIds, DateTime recentSince)
        {
            var history = new List<EntityHistory>();

            try
            {
                history.AddRange(_historyService.FindByDownloadIds(downloadIds.Where(id => id.IsNotNullOrWhiteSpace()), EntityHistoryEventType.Grabbed));
                history.AddRange(_historyService.Since(recentSince, EntityHistoryEventType.Grabbed)
                    .Where(h => IsTorrentGrab(h)));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Seeding report: unable to read grab history");
            }

            var latest = history
                .Where(h => h.DownloadId.IsNotNullOrWhiteSpace())
                .GroupBy(h => h.DownloadId.ToUpperInvariant())
                .Select(g => g.OrderByDescending(h => h.Date).First())
                .ToList();

            var books = LoadBooks(latest.Select(h => h.BookId));
            var authors = LoadAuthors(books.Values.Select(b => b.AuthorId).Concat(latest.Select(h => h.AuthorId)));

            return latest.ToDictionary(
                h => h.DownloadId.ToUpperInvariant(),
                h => ToGrab(h, books, authors),
                StringComparer.OrdinalIgnoreCase);
        }

        internal static TalearrSeedingGrab ToGrab(EntityHistory history, Dictionary<int, Book> books, Dictionary<int, Author> authors)
        {
            books.TryGetValue(history.BookId, out var book);
            authors.TryGetValue(book?.AuthorId ?? history.AuthorId, out var author);

            return new TalearrSeedingGrab
            {
                DownloadId = history.DownloadId.ToUpperInvariant(),
                BookId = book?.Id ?? (history.BookId > 0 ? history.BookId : null),
                BookTitle = book?.Title,
                AuthorName = author?.Name,
                MediaType = book == null ? null : (book.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook"),
                IndexerName = ReadData(history, "indexer"),
                DownloadClientName = ReadData(history, "downloadClientName"),
                Date = DateTime.SpecifyKind(history.Date, DateTimeKind.Utc)
            };
        }

        private static bool IsTorrentGrab(EntityHistory history)
        {
            return ReadData(history, "protocol") == ((int)DownloadProtocol.Torrent).ToString();
        }

        private static string ReadData(EntityHistory history, string key)
        {
            if (history.Data == null)
            {
                return null;
            }

            var match = history.Data.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

            return match.Value.IsNullOrWhiteSpace() ? null : match.Value;
        }

        private Dictionary<int, Book> LoadBooks(IEnumerable<int> bookIds)
        {
            var ids = bookIds.Where(id => id > 0).Distinct().ToList();

            try
            {
                return ids.Count == 0
                    ? new Dictionary<int, Book>()
                    : _bookService.GetExistingBooks(ids).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Seeding report: unable to load books");
                return new Dictionary<int, Book>();
            }
        }

        private Dictionary<int, Author> LoadAuthors(IEnumerable<int> authorIds)
        {
            var result = new Dictionary<int, Author>();

            foreach (var id in authorIds.Where(id => id > 0).Distinct())
            {
                try
                {
                    var author = _authorService.GetAuthor(id);
                    if (author != null)
                    {
                        result[id] = author;
                    }
                }
                catch (Exception)
                {
                    // Deleted author: leave the name empty.
                }
            }

            return result;
        }
    }
}
