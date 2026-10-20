using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.Soulseek
{
    public class TalearrSoulseekResult
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string Directory { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public string Format { get; set; }
        public int? BitRate { get; set; }
        public int? DurationSeconds { get; set; }
        public bool HasFreeUploadSlot { get; set; }
        public long QueueLength { get; set; }
        public int UploadSpeed { get; set; }
        public bool HasDiscFolders { get; set; }
        public string Quality { get; set; }
        public bool Approved { get; set; }
        public List<string> Rejections { get; set; } = new List<string>();
        public List<string> RejectionLabels { get; set; } = new List<string>();
    }

    public class TalearrSoulseekSearchResult
    {
        public string Token { get; set; }
        public int BookId { get; set; }
        public string SearchText { get; set; }
        public int PeerCount { get; set; }
        public List<TalearrSoulseekResult> Results { get; set; } = new List<TalearrSoulseekResult>();
    }

    public class TalearrSoulseekTestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public interface ITalearrSoulseekService
    {
        TalearrSoulseekSearchResult Search(int bookId);
        TalearrSoulseekGrab Grab(string token, string key);
        List<TalearrSoulseekGrab> GetGrabs(int? bookId);
        TalearrSoulseekGrab Cancel(string id);
        void Remove(string id);
        void Refresh();
        TalearrSoulseekTestResult Test(TalearrSoulseekSettings settings);
    }

    public class TalearrSoulseekException : Exception
    {
        public TalearrSoulseekException(string message)
            : base(message)
        {
        }
    }

    // Talearr: opt-in Soulseek search and grab through slskd (docs/TALEARR_SOULSEEK.md).
    // Search answers are judged by the normal decision engine; a grab asks slskd to download the
    // whole folder, is tracked in a talearr.soulseek.grab.* Config row, and once every file is
    // there it is imported for the book it was grabbed for through the normal import pipeline.
    public class TalearrSoulseekService : ITalearrSoulseekService, IHandleAsync<TrackedDownloadRefreshedEvent>
    {
        public const string DestinationPrefix = "talearr-";

        private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan MinimumRefreshInterval = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FinishedGrabLifetime = TimeSpan.FromDays(14);
        private static readonly TimeSpan MissingTransferGrace = TimeSpan.FromMinutes(5);
        private static readonly Regex SearchNoise = new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

        private readonly ITalearrSoulseekSettingsService _settingsService;
        private readonly ITalearrSlskdClient _client;
        private readonly ITalearrSoulseekGrabStore _store;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IMakeDownloadDecision _decisionMaker;
        private readonly IPrioritizeDownloadDecision _prioritizer;
        private readonly IDownloadedBooksImportService _importService;
        private readonly IDiskProvider _diskProvider;
        private readonly ICached<SearchState> _searches;
        private readonly Logger _logger;
        private readonly object _refreshLock = new object();
        private DateTime _lastRefresh = DateTime.MinValue;

        public TalearrSoulseekService(ITalearrSoulseekSettingsService settingsService,
                                      ITalearrSlskdClient client,
                                      ITalearrSoulseekGrabStore store,
                                      IBookService bookService,
                                      IAuthorService authorService,
                                      IMakeDownloadDecision decisionMaker,
                                      IPrioritizeDownloadDecision prioritizer,
                                      IDownloadedBooksImportService importService,
                                      IDiskProvider diskProvider,
                                      ICacheManager cacheManager,
                                      Logger logger)
        {
            _settingsService = settingsService;
            _client = client;
            _store = store;
            _bookService = bookService;
            _authorService = authorService;
            _decisionMaker = decisionMaker;
            _prioritizer = prioritizer;
            _importService = importService;
            _diskProvider = diskProvider;
            _searches = cacheManager.GetCache<SearchState>(GetType(), "searches");
            _logger = logger;
        }

        // Sleeps between search polls; tests replace it.
        internal Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;

        public TalearrSoulseekSearchResult Search(int bookId)
        {
            var settings = RequireActive();
            var (book, author) = LoadBook(bookId);
            var criteria = BuildCriteria(book, author);
            var searchText = BuildSearchText(author.Name, criteria.BookQuery);

            if (searchText.IsNullOrWhiteSpace())
            {
                throw new TalearrSoulseekException("This book has no title to search for");
            }

            var responses = RunSearch(settings, searchText);
            var excluded = FailedPeers(book.Id);
            var candidates = TalearrSoulseekCandidates.Build(responses, book.MediaType, settings, excluded);

            var releases = candidates.Select(TalearrSoulseekCandidates.ToRelease).ToList();
            var byGuid = candidates.ToDictionary(c => "slskd-" + c.Key);

            var decisions = releases.Count == 0
                ? new List<DownloadDecision>()
                : _decisionMaker.GetSearchDecision(releases, criteria) ?? new List<DownloadDecision>();

            var approved = _prioritizer.PrioritizeDecisions(decisions.Where(d => d.Approved).ToList()) ?? new List<DownloadDecision>();
            var ordered = approved
                .Concat(decisions.Where(d => !d.Approved).OrderBy(d => d.Rejections.Count()))
                .ToList();

            var state = new SearchState
            {
                Token = Guid.NewGuid().ToString("N"),
                BookId = book.Id,
                Candidates = candidates.ToDictionary(c => c.Key),
                Decisions = new Dictionary<string, DownloadDecision>()
            };

            var result = new TalearrSoulseekSearchResult
            {
                Token = state.Token,
                BookId = book.Id,
                SearchText = searchText,
                PeerCount = responses.Select(r => r?.Username).Where(u => u != null).Distinct().Count()
            };

            foreach (var decision in ordered)
            {
                if (decision?.RemoteBook?.Release?.Guid == null || !byGuid.TryGetValue(decision.RemoteBook.Release.Guid, out var candidate))
                {
                    continue;
                }

                state.Decisions[candidate.Key] = decision;
                result.Results.Add(ToResult(candidate, decision));
            }

            _searches.Set(state.Token, state, SearchCacheLifetime);
            _logger.Info("Talearr Soulseek: '{0}' found {1} candidates from {2} peers for book {3} ({4} acceptable)",
                searchText,
                result.Results.Count,
                result.PeerCount,
                book.Id,
                result.Results.Count(r => r.Approved));

            return result;
        }

        public TalearrSoulseekGrab Grab(string token, string key)
        {
            var settings = RequireActive();
            var state = token.IsNullOrWhiteSpace() ? null : _searches.Find(token);

            if (state == null || key.IsNullOrWhiteSpace() || !state.Candidates.TryGetValue(key, out var candidate))
            {
                throw new TalearrSoulseekException("This search result has expired. Search Soulseek again.");
            }

            var (book, author) = LoadBook(state.BookId);
            var grabs = _store.All();
            var active = grabs.Where(g => g.IsActive).ToList();

            if (active.Any(g => g.BookId == book.Id))
            {
                throw new TalearrSoulseekException("A Soulseek download for this book is already running.");
            }

            if (active.Count >= settings.MaxActiveGrabs)
            {
                throw new TalearrSoulseekException($"{active.Count} Soulseek downloads are already running (limit {settings.MaxActiveGrabs}).");
            }

            if (active.Count(g => string.Equals(g.Username, candidate.Username, StringComparison.OrdinalIgnoreCase)) >= settings.MaxActiveGrabsPerPeer)
            {
                throw new TalearrSoulseekException($"Already downloading from {candidate.Username}; wait for it to finish before asking this peer again.");
            }

            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var files = candidate.Files.Select(f => new SlskdQueueFile { Filename = f.Filename, Size = f.Size }).ToList();
            var batchId = Guid.NewGuid().ToString();

            SlskdEnqueueResult enqueued;
            try
            {
                enqueued = _client.Enqueue(settings, candidate.Username, files, batchId, DestinationPrefix + id);
            }
            catch (HttpException ex)
            {
                throw new TalearrSoulseekException($"slskd refused the download ({(int)ex.Response.StatusCode}): {Truncate(ex.Response.Content)}");
            }

            var grab = new TalearrSoulseekGrab
            {
                Id = id,
                BookId = book.Id,
                AuthorId = author.Id,
                MediaType = book.MediaType,
                Title = candidate.Title,
                Username = candidate.Username,
                Directory = candidate.Directory,
                Files = files,
                Size = candidate.Size,
                Destination = enqueued.UsedBatchDestination ? enqueued.Destination : null,
                State = TalearrSoulseekGrabState.Queued,
                CreatedAt = DateTime.UtcNow
            };

            _store.Save(grab);
            _logger.Info("Talearr Soulseek: grabbed '{0}' ({1} files) from {2} for book {3}", grab.Title, files.Count, grab.Username, book.Id);

            return grab;
        }

        public List<TalearrSoulseekGrab> GetGrabs(int? bookId)
        {
            return _store.All()
                .Where(g => !bookId.HasValue || g.BookId == bookId.Value)
                .ToList();
        }

        public TalearrSoulseekGrab Cancel(string id)
        {
            var grab = _store.Get(id) ?? throw new TalearrSoulseekException("No such Soulseek download.");

            if (grab.IsActive)
            {
                CancelTransfers(_settingsService.Get(), grab, remove: true);
                grab.State = TalearrSoulseekGrabState.Cancelled;
                grab.Message = "Cancelled";
                _store.Save(grab);
            }

            return grab;
        }

        public void Remove(string id)
        {
            var grab = _store.Get(id);
            if (grab == null)
            {
                return;
            }

            if (grab.IsActive)
            {
                Cancel(id);
            }

            _store.Remove(id);
        }

        public void Refresh()
        {
            lock (_refreshLock)
            {
                _lastRefresh = DateTime.UtcNow;
                RefreshCore();
            }
        }

        public void HandleAsync(TrackedDownloadRefreshedEvent message)
        {
            // Runs after upstream's own download refresh (every minute). Nothing happens unless
            // there is an unfinished Soulseek grab.
            if (!Monitor.TryEnter(_refreshLock))
            {
                return;
            }

            try
            {
                if (DateTime.UtcNow - _lastRefresh < MinimumRefreshInterval)
                {
                    return;
                }

                _lastRefresh = DateTime.UtcNow;
                RefreshCore();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr Soulseek: unable to check the Soulseek downloads");
            }
            finally
            {
                Monitor.Exit(_refreshLock);
            }
        }

        public TalearrSoulseekTestResult Test(TalearrSoulseekSettings settings)
        {
            settings = (settings ?? TalearrSoulseekSettings.Default).Clone();
            if (settings.ApiKey == TalearrSoulseekSettings.MaskedApiKey)
            {
                settings.ApiKey = _settingsService.Get().ApiKey;
            }

            settings.Normalize();
            var result = new TalearrSoulseekTestResult();

            if (!settings.IsConfigured)
            {
                result.Message = "Set the slskd URL first.";
                return result;
            }

            string version;
            try
            {
                var app = _client.GetApplication(settings);
                version = app.Version?.Full ?? app.Version?.Current ?? "unknown version";

                if (app.Server != null && !app.Server.IsLoggedIn)
                {
                    result.Warnings.Add("slskd is running but not logged in to the Soulseek network.");
                }
            }
            catch (HttpException ex)
            {
                result.Message = (int)ex.Response.StatusCode == 401 || (int)ex.Response.StatusCode == 403
                    ? "slskd refused the API key."
                    : $"slskd answered {(int)ex.Response.StatusCode}.";
                return result;
            }
            catch (Exception ex)
            {
                result.Message = $"Unable to reach slskd: {ex.Message}";
                return result;
            }

            if (settings.DownloadsPath.IsNullOrWhiteSpace())
            {
                result.Warnings.Add("Downloads folder is not set, so finished downloads cannot be imported.");
            }
            else if (!_diskProvider.FolderExists(settings.DownloadsPath))
            {
                result.Warnings.Add($"Talearr cannot see '{settings.DownloadsPath}'. Map slskd's downloads folder into the Talearr container.");
            }

            result.Success = true;
            result.Message = $"slskd {version} answered.";
            return result;
        }

        internal void RefreshCore()
        {
            var grabs = _store.All();
            PruneFinished(grabs);

            var active = grabs.Where(g => g.IsActive).ToList();
            if (active.Count == 0)
            {
                return;
            }

            var settings = _settingsService.Get();
            if (!settings.IsConfigured)
            {
                return;
            }

            foreach (var byPeer in active.GroupBy(g => g.Username, StringComparer.OrdinalIgnoreCase))
            {
                List<SlskdTransfer> transfers;
                try
                {
                    transfers = _client.GetDownloads(settings, byPeer.Key);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr Soulseek: unable to read slskd transfers for {0}", byPeer.Key);
                    continue;
                }

                foreach (var grab in byPeer)
                {
                    try
                    {
                        Update(settings, grab, transfers);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Talearr Soulseek: unable to update download {0}", grab.Id);
                    }
                }
            }
        }

        private void Update(TalearrSoulseekSettings settings, TalearrSoulseekGrab grab, List<SlskdTransfer> transfers)
        {
            var wanted = new HashSet<string>(grab.Files.Select(f => f.Filename), StringComparer.OrdinalIgnoreCase);

            // The newest transfer per file (slskd keeps finished ones until they are cleared).
            var mine = transfers
                .Where(t => t?.Filename != null && wanted.Contains(t.Filename))
                .GroupBy(t => t.Filename, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(t => t.RequestedAt ?? DateTime.MinValue).First())
                .ToList();

            var progress = TalearrSoulseekProgress.Evaluate(mine, grab.Files.Count);
            grab.BytesTransferred = mine.Sum(t => t.BytesTransferred);

            switch (progress.Status)
            {
                case TalearrSoulseekProgressStatus.Failed:
                    CancelTransfers(settings, grab, remove: false);
                    Finish(grab, TalearrSoulseekGrabState.Failed, progress.Message);
                    return;

                case TalearrSoulseekProgressStatus.Missing:
                    if (DateTime.UtcNow - grab.CreatedAt > MissingTransferGrace)
                    {
                        Finish(grab, TalearrSoulseekGrabState.Failed, "slskd no longer lists these transfers (removed in slskd?)");
                    }

                    return;

                case TalearrSoulseekProgressStatus.Queued:
                    if (DateTime.UtcNow - grab.CreatedAt > TimeSpan.FromHours(settings.QueueTimeoutHours))
                    {
                        CancelTransfers(settings, grab, remove: true);
                        Finish(grab, TalearrSoulseekGrabState.Failed, $"Still queued by {grab.Username} after {settings.QueueTimeoutHours} hours");
                        return;
                    }

                    grab.State = TalearrSoulseekGrabState.Queued;
                    grab.Message = progress.Message;
                    _store.Save(grab);
                    return;

                case TalearrSoulseekProgressStatus.Downloading:
                    grab.State = TalearrSoulseekGrabState.Downloading;
                    grab.Message = progress.Message;
                    _store.Save(grab);
                    return;

                case TalearrSoulseekProgressStatus.Completed:
                    Import(settings, grab);
                    return;
            }
        }

        private void Import(TalearrSoulseekSettings settings, TalearrSoulseekGrab grab)
        {
            var located = LocateFiles(settings, grab);
            if (located == null)
            {
                Finish(grab, TalearrSoulseekGrabState.ImportFailed,
                    settings.DownloadsPath.IsNullOrWhiteSpace()
                        ? "Downloaded, but the slskd downloads folder is not set in Talearr"
                        : $"Downloaded, but Talearr cannot find the files under '{settings.DownloadsPath}'");
                return;
            }

            grab.LocalFolder = located.Value.Folder;

            Book book;
            Author author;
            try
            {
                (book, author) = LoadBook(grab.BookId);
            }
            catch (TalearrSoulseekException ex)
            {
                Finish(grab, TalearrSoulseekGrabState.ImportFailed, ex.Message + "; the files are in " + grab.LocalFolder);
                return;
            }

            var release = new ReleaseInfo
            {
                Guid = "slskd-" + grab.Id,
                Title = grab.Title,
                Size = grab.Size,
                Indexer = TalearrSoulseekCandidates.IndexerName,
                DownloadProtocol = DownloadProtocol.Unknown,
                PublishDate = grab.CreatedAt,
                Categories = new List<int> { grab.MediaType == BookMediaType.Ebook ? 7020 : 3030 }
            };

            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { book },
                Release = release,
                ParsedBookInfo = new ParsedBookInfo
                {
                    AuthorName = author.Name,
                    BookTitle = book.Title,
                    ReleaseTitle = grab.Title,
                    Quality = QualityParser.ParseQuality(grab.Title, null, release.Categories, release.Indexer)
                }
            };

            // In memory only: the import pipeline wants a download item to scope the files and
            // record the source in history (as text). Nothing here is stored as a download client.
            var item = new DownloadClientItem
            {
                DownloadId = "slskd-" + grab.Id,
                Title = grab.Title,
                MediaType = grab.MediaType,
                TotalSize = grab.Size,
                OutputPath = new OsPath(grab.LocalFolder),
                FilePaths = located.Value.Files,
                Status = DownloadItemStatus.Completed,
                CanMoveFiles = true,
                CanBeRemoved = true,
                DownloadClientInfo = new DownloadClientItemClientInfo
                {
                    Protocol = DownloadProtocol.Unknown,
                    Type = "Soulseek",
                    Name = TalearrSoulseekCandidates.IndexerName
                }
            };

            var results = _importService.ProcessPath(grab.LocalFolder, ImportMode.Move, author, item, remoteBook) ?? new List<ImportResult>();
            var imported = results.Count(r => r.Result == ImportResultType.Imported);

            if (imported == 0)
            {
                var errors = results.SelectMany(r => r.Errors ?? new List<string>()).Distinct().Take(3).ToList();
                Finish(grab, TalearrSoulseekGrabState.ImportFailed,
                    "Not imported" + (errors.Count > 0 ? ": " + string.Join("; ", errors) : string.Empty) + ". The files are in " + grab.LocalFolder + " (Wanted > Manual Import).");
                return;
            }

            if (settings.CleanupAfterImport)
            {
                CancelTransfers(settings, grab, remove: true);
                CleanupFolder(grab);
            }

            Finish(grab, TalearrSoulseekGrabState.Imported, $"Imported {imported} file(s)");
        }

        // Where slskd put the files: Talearr's own batch folder, or (older slskd) slskd's default
        // "<downloads>/<remote folder name>", or any folder up to two levels down holding them.
        internal (string Folder, List<string> Files)? LocateFiles(TalearrSoulseekSettings settings, TalearrSoulseekGrab grab)
        {
            if (settings.DownloadsPath.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(settings.DownloadsPath))
            {
                return null;
            }

            var names = grab.Files.Select(f => TalearrSoulseekCandidates.FileName(f.Filename)).ToList();
            var candidates = new List<string>();

            if (grab.Destination.IsNotNullOrWhiteSpace())
            {
                candidates.Add(Path.Combine(settings.DownloadsPath, grab.Destination));
            }

            foreach (var segment in new[] { TalearrSoulseekCandidates.Segments(grab.Directory).LastOrDefault() }
                         .Concat(grab.Files.Select(f => TalearrSoulseekCandidates.Segments(TalearrSoulseekCandidates.DirectoryOf(f.Filename)).LastOrDefault()))
                         .Where(s => s.IsNotNullOrWhiteSpace())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(settings.DownloadsPath, segment));
            }

            candidates.Add(settings.DownloadsPath);

            foreach (var folder in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var found = FindAll(folder, names);
                if (found != null)
                {
                    return (folder, found);
                }
            }

            try
            {
                foreach (var level1 in _diskProvider.GetDirectories(settings.DownloadsPath))
                {
                    var found = FindAll(level1, names);
                    if (found != null)
                    {
                        return (level1, found);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr Soulseek: unable to list {0}", settings.DownloadsPath);
            }

            return null;
        }

        private List<string> FindAll(string folder, List<string> names)
        {
            if (!_diskProvider.FolderExists(folder))
            {
                return null;
            }

            var files = _diskProvider.GetFiles(folder, true).ToList();
            var byName = files
                .GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var found = new List<string>();
            foreach (var name in names)
            {
                if (!byName.TryGetValue(name, out var path))
                {
                    return null;
                }

                found.Add(path);
            }

            return found;
        }

        private void CleanupFolder(TalearrSoulseekGrab grab)
        {
            try
            {
                if (grab.LocalFolder.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(grab.LocalFolder))
                {
                    return;
                }

                // Talearr's own batch folder can go entirely (only cover art and the like are left);
                // a folder slskd chose is removed only when nothing is left in it.
                var ours = grab.Destination.IsNotNullOrWhiteSpace() &&
                           string.Equals(Path.GetFileName(grab.LocalFolder.TrimEnd('/', '\\')), grab.Destination, StringComparison.OrdinalIgnoreCase);

                if (ours)
                {
                    _diskProvider.DeleteFolder(grab.LocalFolder, true);
                }
                else if (!_diskProvider.GetFiles(grab.LocalFolder, true).Any())
                {
                    _diskProvider.DeleteFolder(grab.LocalFolder, true);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr Soulseek: unable to clean up {0}", grab.LocalFolder);
            }
        }

        private void CancelTransfers(TalearrSoulseekSettings settings, TalearrSoulseekGrab grab, bool remove)
        {
            if (settings == null || !settings.IsConfigured)
            {
                return;
            }

            try
            {
                var wanted = new HashSet<string>(grab.Files.Select(f => f.Filename), StringComparer.OrdinalIgnoreCase);
                foreach (var transfer in _client.GetDownloads(settings, grab.Username).Where(t => t?.Id != null && wanted.Contains(t.Filename ?? string.Empty)))
                {
                    var finished = (transfer.State ?? string.Empty).Contains("Completed", StringComparison.OrdinalIgnoreCase);
                    if (finished && !remove)
                    {
                        continue;
                    }

                    _client.CancelDownload(settings, grab.Username, transfer.Id, remove);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr Soulseek: unable to cancel the slskd transfers of {0}", grab.Id);
            }
        }

        private void Finish(TalearrSoulseekGrab grab, TalearrSoulseekGrabState state, string message)
        {
            grab.State = state;
            grab.Message = message;
            _store.Save(grab);

            if (state == TalearrSoulseekGrabState.Imported)
            {
                _logger.Info("Talearr Soulseek: {0} ({1})", message, grab.Title);
            }
            else
            {
                _logger.Warn("Talearr Soulseek: download '{0}' from {1}: {2}", grab.Title, grab.Username, message);
            }
        }

        private void PruneFinished(List<TalearrSoulseekGrab> grabs)
        {
            foreach (var old in grabs.Where(g => !g.IsActive && DateTime.UtcNow - g.UpdatedAt > FinishedGrabLifetime).ToList())
            {
                _store.Remove(old.Id);
                grabs.Remove(old);
            }
        }

        private HashSet<string> FailedPeers(int bookId)
        {
            // A peer whose download of this book failed is not offered for it again.
            return new HashSet<string>(
                _store.All()
                    .Where(g => g.BookId == bookId && g.State == TalearrSoulseekGrabState.Failed)
                    .Select(g => g.Username)
                    .Where(u => u != null),
                StringComparer.OrdinalIgnoreCase);
        }

        private List<SlskdSearchResponse> RunSearch(TalearrSoulseekSettings settings, string searchText)
        {
            var id = Guid.NewGuid().ToString();

            try
            {
                _client.StartSearch(settings, id, searchText);

                var deadline = DateTime.UtcNow.AddSeconds(settings.SearchTimeoutSeconds + 15);
                while (DateTime.UtcNow < deadline)
                {
                    Sleep(TimeSpan.FromSeconds(1));
                    var search = _client.GetSearch(settings, id);
                    if (search == null || search.IsComplete)
                    {
                        break;
                    }
                }

                return _client.GetSearchResponses(settings, id) ?? new List<SlskdSearchResponse>();
            }
            catch (HttpException ex)
            {
                throw new TalearrSoulseekException($"slskd search failed ({(int)ex.Response.StatusCode}): {Truncate(ex.Response.Content)}");
            }
            finally
            {
                _client.DeleteSearch(settings, id);
            }
        }

        private BookSearchCriteria BuildCriteria(Book book, Author author)
        {
            var edition = book.Editions?.Where(e => e != null && e.Monitored).OrderBy(e => e.Id).FirstOrDefault();

            return new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { book },
                AuthorCatalog = _bookService.GetBooksByAuthor(author.Id),
                MonitoredBooksOnly = false,
                UserInvokedSearch = true,
                InteractiveSearch = true,
                BookTitle = ReleaseSearchService.GetSearchBookTitle(book, edition) ?? string.Empty,
                BookYear = book.ReleaseDate?.Year ?? 0
            };
        }

        // Soulseek matches every word, so keep it short: author and main title, no punctuation.
        public static string BuildSearchText(string author, string title)
        {
            var words = new List<string>();
            foreach (var part in new[] { author, title })
            {
                var clean = SearchNoise.Replace(part ?? string.Empty, " ");
                words.AddRange(clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 || char.IsDigit(w[0])));
            }

            return string.Join(" ", words.Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
        }

        private (Book Book, Author Author) LoadBook(int bookId)
        {
            Book book;
            try
            {
                book = _bookService.GetBook(bookId);
            }
            catch (Exception)
            {
                book = null;
            }

            if (book == null)
            {
                throw new TalearrSoulseekException($"Book {bookId} was not found");
            }

            var author = book.Author?.Id == book.AuthorId && book.AuthorId > 0 ? book.Author : _authorService.GetAuthor(book.AuthorId);
            book.Author = author;
            return (book, author);
        }

        private TalearrSoulseekSettings RequireActive()
        {
            var settings = _settingsService.Get();
            if (!settings.IsActive)
            {
                throw new TalearrSoulseekException("Soulseek is off. Turn it on in Settings > Download Clients > Soulseek (slskd).");
            }

            return settings;
        }

        private static TalearrSoulseekResult ToResult(TalearrSoulseekCandidate candidate, DownloadDecision decision)
        {
            var rejections = decision.Rejections?.ToList() ?? new List<Rejection>();

            return new TalearrSoulseekResult
            {
                Key = candidate.Key,
                Title = candidate.Title,
                Username = candidate.Username,
                Directory = candidate.Directory,
                FileCount = candidate.Files.Count,
                Size = candidate.Size,
                Format = candidate.Format,
                BitRate = candidate.BitRate,
                DurationSeconds = candidate.DurationSeconds,
                HasFreeUploadSlot = candidate.HasFreeUploadSlot,
                QueueLength = candidate.QueueLength,
                UploadSpeed = candidate.UploadSpeed,
                HasDiscFolders = candidate.HasDiscFolders,
                Quality = decision.RemoteBook?.ParsedBookInfo?.Quality?.Quality?.Name,
                Approved = decision.Approved,
                Rejections = rejections.Select(r => r.Reason).ToList(),
                RejectionLabels = rejections.Select(TalearrRejectionLabels.GetLabel).Distinct().ToList()
            };
        }

        private static string Truncate(string value)
        {
            value ??= string.Empty;
            return value.Length > 200 ? value.Substring(0, 200) : value;
        }

        internal class SearchState
        {
            public string Token { get; set; }
            public int BookId { get; set; }
            public Dictionary<string, TalearrSoulseekCandidate> Candidates { get; set; }
            public Dictionary<string, DownloadDecision> Decisions { get; set; }
        }
    }
}
