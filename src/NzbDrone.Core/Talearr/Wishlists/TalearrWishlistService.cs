using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public sealed class TalearrWishlistRequest
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
        public string Url { get; set; }

        // An uploaded CSV export; kept in <config>/talearr-wishlists/<id>.csv.
        public string Csv { get; set; }

        public bool? Audiobook { get; set; }
        public bool? Ebook { get; set; }
        public bool? SearchOnAdd { get; set; }
        public bool? ToReadOnly { get; set; }
        public bool? Enabled { get; set; }
    }

    public interface ITalearrWishlistService
    {
        List<TalearrWishlist> All();
        TalearrWishlist Get(string id);
        TalearrWishlist Create(TalearrWishlistRequest request);
        TalearrWishlist Update(string id, TalearrWishlistRequest request);
        void Delete(string id);
        TalearrWishlistPreview Preview(string id);
        TalearrWishlistPreview PreviewUpload(TalearrWishlistRequest request);
        TalearrWishlistSyncResult Sync(string id);
    }

    // Talearr: wishlists (docs/TALEARR_WISHLISTS.md). A wishlist is a CSV export (Goodreads,
    // StoryGraph, an Audible wishlist exported with audible-cli, or any title/author/ISBN/ASIN
    // list) or a Hardcover list. Each new row is matched to a book and added the way the add-book
    // modal's "Only this book" adds it: only that book is monitored, in the wishlist's formats,
    // future releases of the author stay unmonitored, and the book is searched when Search when
    // added is on. A row is acted on once, so unmonitoring the book later sticks.
    public class TalearrWishlistService : ITalearrWishlistService, IExecute<SyncWishlistsCommand>
    {
        public const string UploadFolderName = "talearr-wishlists";

        // Metadata lookups per sync and preview; the rest waits for the next sync.
        public const int MaxLookupsPerRun = 60;
        public static readonly TimeSpan NoMatchRetry = TimeSpan.FromDays(7);

        private readonly ITalearrWishlistStore _store;
        private readonly ITalearrHardcoverListClient _hardcover;
        private readonly ISearchForNewBook _bookSearch;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IImportListExclusionService _exclusionService;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IConfigService _configService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;
        private readonly object _syncLock = new object();

        public TalearrWishlistService(ITalearrWishlistStore store,
                                      ITalearrHardcoverListClient hardcover,
                                      ISearchForNewBook bookSearch,
                                      IBookService bookService,
                                      IAuthorService authorService,
                                      IImportListExclusionService exclusionService,
                                      IPendingAuthorImportService pendingAuthorImportService,
                                      IRootFolderService rootFolderService,
                                      IRootFolderSettingsResolver rootFolderSettingsResolver,
                                      IConfigService configService,
                                      IManageCommandQueue commandQueueManager,
                                      IAppFolderInfo appFolderInfo,
                                      Logger logger)
        {
            _store = store;
            _hardcover = hardcover;
            _bookSearch = bookSearch;
            _bookService = bookService;
            _authorService = authorService;
            _exclusionService = exclusionService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _configService = configService;
            _commandQueueManager = commandQueueManager;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public List<TalearrWishlist> All() => _store.All();

        public TalearrWishlist Get(string id) => _store.Get(id);

        public TalearrWishlist Create(TalearrWishlistRequest request)
        {
            request ??= new TalearrWishlistRequest();
            var wishlist = new TalearrWishlist
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                AddedAt = DateTime.UtcNow
            };

            Apply(wishlist, request);
            Validate(wishlist);
            _store.Save(wishlist);
            _logger.Info("Talearr: wishlist '{0}' added ({1})", wishlist.Name, wishlist.Kind);
            return wishlist;
        }

        public TalearrWishlist Update(string id, TalearrWishlistRequest request)
        {
            var wishlist = _store.Get(id);
            if (wishlist == null)
            {
                return null;
            }

            Apply(wishlist, request ?? new TalearrWishlistRequest());
            Validate(wishlist);
            _store.Save(wishlist);
            return wishlist;
        }

        public void Delete(string id)
        {
            var wishlist = _store.Get(id);
            if (wishlist == null)
            {
                return;
            }

            _store.Remove(id);
            var upload = UploadPath(id);
            if (string.Equals(wishlist.Path, upload, StringComparison.Ordinal) && File.Exists(upload))
            {
                File.Delete(upload);
            }

            _logger.Info("Talearr: wishlist '{0}' removed; books it added stay as they are", wishlist.Name);
        }

        public TalearrWishlistPreview Preview(string id)
        {
            var wishlist = _store.Get(id);
            return wishlist == null ? null : PreviewCore(wishlist);
        }

        public TalearrWishlistPreview PreviewUpload(TalearrWishlistRequest request)
        {
            var wishlist = new TalearrWishlist { Id = "preview", Name = request?.Name ?? "Preview" };
            Apply(wishlist, request ?? new TalearrWishlistRequest(), saveUpload: false);
            return PreviewCore(wishlist, request?.Csv);
        }

        public TalearrWishlistSyncResult Sync(string id)
        {
            var wishlist = _store.Get(id);
            return wishlist == null ? null : SyncCore(wishlist);
        }

        public void Execute(SyncWishlistsCommand message)
        {
            var wishlists = string.IsNullOrWhiteSpace(message.WishlistId)
                ? _store.All().Where(w => w.Enabled).ToList()
                : _store.All().Where(w => w.Id == message.WishlistId).ToList();

            foreach (var wishlist in wishlists)
            {
                var result = SyncCore(wishlist);
                if (result.Added + result.Queued + result.Monitored > 0)
                {
                    _logger.ProgressInfo("Wishlist '{0}': {1}", wishlist.Name, result.Describe());
                }
            }
        }

        internal TalearrWishlistSyncResult SyncCore(TalearrWishlist wishlist)
        {
            lock (_syncLock)
            {
                var result = new TalearrWishlistSyncResult { WishlistId = wishlist.Id };

                try
                {
                    var (format, rows) = ReadRows(wishlist, null);
                    wishlist.Format = format;
                    result.Rows = rows.Count;

                    var context = new ActContext(this, wishlist);
                    var lookups = 0;

                    foreach (var row in rows)
                    {
                        if (IsHandled(wishlist, row.Key))
                        {
                            continue;
                        }

                        var needsLookup = string.IsNullOrWhiteSpace(row.BookProviderId);
                        if (needsLookup && lookups >= MaxLookupsPerRun)
                        {
                            result.Deferred++;
                            continue;
                        }

                        if (needsLookup)
                        {
                            lookups++;
                        }

                        var item = Resolve(row);
                        if (item.State == TalearrWishlistStates.New)
                        {
                            context.Act(item);
                        }

                        Count(result, item);
                        result.Items.Add(item);

                        if (item.State != TalearrWishlistStates.Failed)
                        {
                            wishlist.Handled[row.Key] = item.State == TalearrWishlistStates.NoMatch
                                ? TalearrWishlistStates.NoMatch + "@" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                : item.State;
                        }
                    }

                    context.Finish();
                    wishlist.LastError = null;
                }
                catch (Exception ex) when (ex is TalearrWishlistException || ex is IOException || ex is FormatException || ex is UnauthorizedAccessException)
                {
                    wishlist.LastError = ex.Message;
                    _logger.Warn("Talearr: wishlist '{0}' could not be read: {1}", wishlist.Name, ex.Message);
                }

                wishlist.LastSyncAt = DateTime.UtcNow;
                wishlist.LastResult = wishlist.LastError == null ? result.Describe() : null;
                _store.Save(wishlist);

                return result;
            }
        }

        private TalearrWishlistPreview PreviewCore(TalearrWishlist wishlist, string csv = null)
        {
            var (format, rows) = ReadRows(wishlist, csv);
            var preview = new TalearrWishlistPreview { Format = format, RowCount = rows.Count };
            var lookups = 0;

            foreach (var row in rows)
            {
                if (IsHandled(wishlist, row.Key) && wishlist.Handled.TryGetValue(row.Key, out var state))
                {
                    preview.Items.Add(new TalearrWishlistItem { Row = row, State = state.Split('@')[0], Message = "Done on an earlier sync" });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(row.BookProviderId) && lookups++ >= MaxLookupsPerRun)
                {
                    preview.Items.Add(new TalearrWishlistItem { Row = row, State = TalearrWishlistStates.New, Message = "Not looked up in the preview (only the first " + MaxLookupsPerRun + " are)" });
                    continue;
                }

                var item = Resolve(row);
                if (item.State == TalearrWishlistStates.New)
                {
                    new ActContext(this, wishlist).Describe(item);
                }

                preview.Items.Add(item);
            }

            return preview;
        }

        private (string Format, List<TalearrWishlistRow> Rows) ReadRows(TalearrWishlist wishlist, string csv)
        {
            if (wishlist.Kind == TalearrWishlistKinds.HardcoverList)
            {
                var (name, rows) = _hardcover.GetList(wishlist.Url);
                if (string.IsNullOrWhiteSpace(wishlist.Name) && !string.IsNullOrWhiteSpace(name))
                {
                    wishlist.Name = name;
                }

                return (TalearrWishlistFormats.Hardcover, rows);
            }

            if (csv == null)
            {
                if (string.IsNullOrWhiteSpace(wishlist.Path) || !File.Exists(wishlist.Path))
                {
                    throw new TalearrWishlistException($"The file '{wishlist.Path}' does not exist.");
                }

                csv = File.ReadAllText(wishlist.Path);
            }

            return TalearrWishlistCsv.Parse(csv, wishlist.ToReadOnly);
        }

        private static bool IsHandled(TalearrWishlist wishlist, string key)
        {
            if (!wishlist.Handled.TryGetValue(key, out var state))
            {
                return false;
            }

            // Books not found are looked up again after a week (the metadata may know them by then).
            if (state.StartsWith(TalearrWishlistStates.NoMatch + "@", StringComparison.Ordinal) &&
                DateTime.TryParseExact(state.Substring(TalearrWishlistStates.NoMatch.Length + 1), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                return DateTime.UtcNow - when < NoMatchRetry;
            }

            return true;
        }

        private static void Count(TalearrWishlistSyncResult result, TalearrWishlistItem item)
        {
            switch (item.State)
            {
                case TalearrWishlistStates.Added: result.Added++; break;
                case TalearrWishlistStates.Queued: result.Queued++; break;
                case TalearrWishlistStates.Monitored: result.Monitored++; break;
                case TalearrWishlistStates.InLibrary: result.AlreadyThere++; break;
                case TalearrWishlistStates.Excluded: result.Excluded++; break;
                case TalearrWishlistStates.NoMatch: result.NoMatch++; break;
                case TalearrWishlistStates.Failed: result.Failed++; break;
            }
        }

        // Matches a row to a metadata book: known provider ids first (Hardcover lists), then ASIN,
        // ISBN, then title and author.
        internal TalearrWishlistItem Resolve(TalearrWishlistRow row)
        {
            var item = new TalearrWishlistItem { Row = row, BookTitle = row.Title, AuthorName = row.Author };

            if (!string.IsNullOrWhiteSpace(row.BookProviderId))
            {
                item.BookProviderId = row.BookProviderId;
                item.AuthorProviderId = row.AuthorProviderId;
                item.State = TalearrWishlistStates.New;
                return item;
            }

            try
            {
                Book match = null;
                if (!string.IsNullOrWhiteSpace(row.Asin))
                {
                    match = Pick(_bookSearch.SearchByAsin(row.Asin), row, trustIdentifier: true);
                }

                if (match == null && !string.IsNullOrWhiteSpace(row.Isbn))
                {
                    match = Pick(_bookSearch.SearchByIsbn(row.Isbn), row, trustIdentifier: true);
                }

                if (match == null)
                {
                    match = Pick(_bookSearch.SearchForNewBook(row.Title, row.Author, false), row, trustIdentifier: false);
                }

                if (match == null)
                {
                    item.State = TalearrWishlistStates.NoMatch;
                    item.Message = "No matching book in the metadata";
                    return item;
                }

                item.BookTitle = match.Title;
                item.AuthorName = match.Author?.Name ?? row.Author;
                item.BookProviderId = BookProviderId(match);
                item.AuthorProviderId = AuthorProviderId(match.Author);
                item.State = item.BookProviderId == null ? TalearrWishlistStates.NoMatch : TalearrWishlistStates.New;
                if (item.BookProviderId == null)
                {
                    item.Message = "The match has no provider id";
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: wishlist lookup failed for '{0}'", row.Title);
                item.State = TalearrWishlistStates.Failed;
                item.Message = "The metadata lookup failed; tried again on the next sync";
            }

            return item;
        }

        internal static Book Pick(List<Book> candidates, TalearrWishlistRow row, bool trustIdentifier)
        {
            var list = (candidates ?? new List<Book>()).Where(b => b != null).ToList();
            if (!list.Any())
            {
                return null;
            }

            var wantedTitle = TalearrWishlistCsv.Simplify(row.Title);
            var wantedAuthor = TalearrWishlistCsv.Simplify(row.Author);
            var authorLast = wantedAuthor.Split(' ').LastOrDefault() ?? string.Empty;

            bool AuthorFits(Book b) => string.IsNullOrEmpty(authorLast) || TalearrWishlistCsv.Simplify(b.Author?.Name).Contains(authorLast);
            bool TitleFits(Book b)
            {
                var title = TalearrWishlistCsv.Simplify(b.Title);
                return title.Length > 0 && wantedTitle.Length > 0 && (title.StartsWith(wantedTitle) || wantedTitle.StartsWith(title));
            }

            return list.FirstOrDefault(b => TitleFits(b) && AuthorFits(b)) ??
                   (trustIdentifier ? list.FirstOrDefault(AuthorFits) : null);
        }

        internal static string BookProviderId(Book book)
        {
            if (!string.IsNullOrWhiteSpace(book?.HardcoverBookId))
            {
                return ProviderIdHelper.Canonicalize(book.HardcoverBookId, "hc");
            }

            return string.IsNullOrWhiteSpace(book?.GoodreadsWorkId) ? null : ProviderIdHelper.Canonicalize(book.GoodreadsWorkId, "gr");
        }

        internal static string AuthorProviderId(Author author)
        {
            if (!string.IsNullOrWhiteSpace(author?.HardcoverAuthorId))
            {
                return ProviderIdHelper.Canonicalize(author.HardcoverAuthorId, "hc");
            }

            return string.IsNullOrWhiteSpace(author?.GoodreadsAuthorId) ? null : ProviderIdHelper.Canonicalize(author.GoodreadsAuthorId, "gr");
        }

        private void Apply(TalearrWishlist wishlist, TalearrWishlistRequest request, bool saveUpload = true)
        {
            if (!string.IsNullOrWhiteSpace(request.Kind))
            {
                wishlist.Kind = request.Kind == TalearrWishlistKinds.HardcoverList ? TalearrWishlistKinds.HardcoverList : TalearrWishlistKinds.File;
            }

            wishlist.Name = string.IsNullOrWhiteSpace(request.Name) ? wishlist.Name : request.Name.Trim();
            wishlist.Url = request.Url?.Trim() ?? wishlist.Url;
            wishlist.Audiobook = request.Audiobook ?? wishlist.Audiobook;
            wishlist.Ebook = request.Ebook ?? wishlist.Ebook;
            wishlist.SearchOnAdd = request.SearchOnAdd ?? wishlist.SearchOnAdd;
            wishlist.ToReadOnly = request.ToReadOnly ?? wishlist.ToReadOnly;
            wishlist.Enabled = request.Enabled ?? wishlist.Enabled;

            if (!string.IsNullOrWhiteSpace(request.Path))
            {
                wishlist.Path = request.Path.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.Csv))
            {
                // Validate before keeping it.
                var (format, _) = TalearrWishlistCsv.Parse(request.Csv, wishlist.ToReadOnly);
                wishlist.Format = format;

                if (saveUpload)
                {
                    var path = UploadPath(wishlist.Id);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, request.Csv);
                    wishlist.Path = path;
                }
            }

            if (string.IsNullOrWhiteSpace(wishlist.Name))
            {
                wishlist.Name = wishlist.Kind == TalearrWishlistKinds.HardcoverList ? "Hardcover list" : "Wishlist";
            }
        }

        private static void Validate(TalearrWishlist wishlist)
        {
            if (!wishlist.Audiobook && !wishlist.Ebook)
            {
                throw new TalearrWishlistException("Choose audiobooks, ebooks or both.");
            }

            if (wishlist.Kind == TalearrWishlistKinds.HardcoverList && TalearrHardcoverListClient.ParseUrl(wishlist.Url).User == null)
            {
                throw new TalearrWishlistException("Not a Hardcover list URL. Use the list's address, like https://hardcover.app/@you/lists/my-list.");
            }

            if (wishlist.Kind == TalearrWishlistKinds.File && string.IsNullOrWhiteSpace(wishlist.Path))
            {
                throw new TalearrWishlistException("Upload a CSV export or give the path of one on the server.");
            }
        }

        private string UploadPath(string id)
        {
            return Path.Combine(_appFolderInfo.AppDataFolder, UploadFolderName, id + ".csv");
        }

        // Acts on matched rows for one wishlist run: monitors books already in the library and
        // queues the others through upstream's pending author import, as an import list does.
        private sealed class ActContext
        {
            private readonly TalearrWishlistService _service;
            private readonly TalearrWishlist _wishlist;
            private readonly List<ImportListExclusion> _exclusions;
            private readonly List<int> _toSearch = new List<int>();
            private readonly Dictionary<BookMediaType, (string Path, ResolvedRootFolderSettings Settings)> _roots = new Dictionary<BookMediaType, (string, ResolvedRootFolderSettings)>();
            private bool _queued;

            public ActContext(TalearrWishlistService service, TalearrWishlist wishlist)
            {
                _service = service;
                _wishlist = wishlist;
                _exclusions = Safe(() => service._exclusionService.All()) ?? new List<ImportListExclusion>();
            }

            private IEnumerable<BookMediaType> Wanted()
            {
                if (_wishlist.Audiobook)
                {
                    yield return BookMediaType.Audiobook;
                }

                if (_wishlist.Ebook)
                {
                    yield return BookMediaType.Ebook;
                }
            }

            // Preview: what a sync would do, without doing it.
            public void Describe(TalearrWishlistItem item)
            {
                if (IsExcluded(item))
                {
                    item.State = TalearrWishlistStates.Excluded;
                    item.Message = "On the import list exclusions";
                    return;
                }

                var notWanted = new List<string>();
                foreach (var mediaType in Wanted())
                {
                    var row = FindLocal(item, mediaType);
                    Remember(item, mediaType, row);
                    if (row == null || !row.IsMonitoredForMediaType(mediaType))
                    {
                        notWanted.Add(mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook");
                    }
                }

                if (!notWanted.Any())
                {
                    item.State = TalearrWishlistStates.InLibrary;
                    item.Message = "Already monitored";
                    return;
                }

                item.Message = "Will monitor the " + string.Join(" and ", notWanted);
            }

            public void Act(TalearrWishlistItem item)
            {
                if (IsExcluded(item))
                {
                    item.State = TalearrWishlistStates.Excluded;
                    item.Message = "On the import list exclusions";
                    return;
                }

                var missing = new List<BookMediaType>();
                var monitored = 0;
                var already = 0;

                foreach (var mediaType in Wanted())
                {
                    var row = FindLocal(item, mediaType);
                    Remember(item, mediaType, row);
                    if (row == null)
                    {
                        missing.Add(mediaType);
                        continue;
                    }

                    var format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                    if (row.IsMonitoredForMediaType(mediaType))
                    {
                        already++;
                        continue;
                    }

                    _service._bookService.SetMonitoredForMediaType(new[] { row.Id }, format, true);
                    EnsureAuthorFormat(row.AuthorId, mediaType);
                    monitored++;

                    if (_wishlist.SearchOnAdd)
                    {
                        _toSearch.Add(row.Id);
                    }
                }

                if (missing.Any())
                {
                    if (string.IsNullOrWhiteSpace(item.AuthorProviderId))
                    {
                        item.State = TalearrWishlistStates.NoMatch;
                        item.Message = "The match has no author id to add";
                        return;
                    }

                    var config = BuildConfig(item, missing, out var problem);
                    if (config == null)
                    {
                        item.State = TalearrWishlistStates.Failed;
                        item.Message = problem;
                        return;
                    }

                    var pendingId = _service._pendingAuthorImportService.EnqueueAsync(item.AuthorProviderId, config, config.RequestedBy).GetAwaiter().GetResult();
                    if (pendingId > 0)
                    {
                        _queued = true;
                        item.State = TalearrWishlistStates.Queued;
                        item.Message = "Adding " + string.Join(" and ", missing.Select(m => m == BookMediaType.Audiobook ? "the audiobook" : "the ebook"));
                        return;
                    }

                    item.State = TalearrWishlistStates.Failed;
                    item.Message = "The author could not be queued for import";
                    return;
                }

                item.State = monitored > 0 ? TalearrWishlistStates.Monitored : TalearrWishlistStates.InLibrary;
                item.Message = monitored > 0 ? "Monitored" : "Already monitored";
            }

            public void Finish()
            {
                if (_toSearch.Any())
                {
                    _service._commandQueueManager.Push(new BookSearchCommand(_toSearch.Distinct().ToList()), CommandPriority.Normal, CommandTrigger.Scheduled);
                }

                if (_queued)
                {
                    _service._commandQueueManager.Push(new ProcessPendingImportsCommand { BatchSize = 10, ContinueUntilEmpty = true }, CommandPriority.Normal, CommandTrigger.Scheduled);
                }
            }

            private static void Remember(TalearrWishlistItem item, BookMediaType mediaType, Book row)
            {
                if (mediaType == BookMediaType.Audiobook)
                {
                    item.AudiobookBookId = row?.Id;
                }
                else
                {
                    item.EbookBookId = row?.Id;
                }
            }

            private bool IsExcluded(TalearrWishlistItem item)
            {
                return _exclusions.Any(e =>
                    ImportListExclusionBookMatcher.AppliesToProviderId(e, item.BookProviderId, null) ||
                    (!string.IsNullOrWhiteSpace(item.AuthorProviderId) && string.Equals(e.ForeignId, item.AuthorProviderId, StringComparison.OrdinalIgnoreCase)));
            }

            private Book FindLocal(TalearrWishlistItem item, BookMediaType mediaType)
            {
                var id = item.BookProviderId;
                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                var prefix = id.Contains(':') ? id.Substring(0, id.IndexOf(':')) : "hc";
                return Safe(() => _service._bookService.FindByProviderId(prefix, id, mediaType));
            }

            // Only the author's format switch, as the series page does; "monitor new items" is
            // left alone, so nothing else of the author is monitored.
            private void EnsureAuthorFormat(int authorId, BookMediaType mediaType)
            {
                var author = Safe(() => _service._authorService.GetAuthor(authorId));
                if (author != null && !author.IsMonitoredForMediaType(mediaType == BookMediaType.Audiobook) &&
                    author.ApplyMediaTypeMonitoringSettings(mediaType, true, null))
                {
                    _service._authorService.UpdateAuthor(author);
                }
            }

            private MonitoringConfig BuildConfig(TalearrWishlistItem item, List<BookMediaType> missing, out string problem)
            {
                problem = null;
                var config = new MonitoringConfig
                {
                    CreateAudiobook = false,
                    CreateEbook = false,
                    QueueIfUnavailable = true,
                    RequestedBy = "Talearr wishlist: " + _wishlist.Name,
                    AuthorName = item.AuthorName,
                    SearchForMissingBooks = _wishlist.SearchOnAdd,
                    AudiobookMonitorExistingMode = MonitorTypes.None,
                    EbookMonitorExistingMode = MonitorTypes.None
                };

                foreach (var mediaType in missing)
                {
                    var root = Root(mediaType);
                    if (root.Path == null)
                    {
                        problem = $"No root folder is set up for {(mediaType == BookMediaType.Audiobook ? "audiobooks" : "ebooks")}";
                        return null;
                    }

                    var books = new List<string> { item.BookProviderId };
                    var tags = new HashSet<int>(root.Settings.Tags ?? new List<int>());
                    if (mediaType == BookMediaType.Audiobook)
                    {
                        config.CreateAudiobook = true;
                        config.AudiobookRootFolderPath = root.Path;
                        config.AudiobookQualityProfileId = root.Settings.QualityProfileId;
                        config.AudiobookMetadataProfileId = root.Settings.MetadataProfileId;
                        config.AudiobookMonitored = true;
                        config.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                        config.AudiobookMonitorExistingMode = MonitorTypes.SpecificBook;
                        config.AudiobookBooksToMonitor = books;
                        config.AudiobookBooksToSearch = _wishlist.SearchOnAdd ? books.ToList() : null;
                        config.AudiobookTags = tags;
                    }
                    else
                    {
                        config.CreateEbook = true;
                        config.EbookRootFolderPath = root.Path;
                        config.EbookQualityProfileId = root.Settings.QualityProfileId;
                        config.EbookMetadataProfileId = root.Settings.MetadataProfileId;
                        config.EbookMonitored = true;
                        config.EbookMonitorNewItems = NewItemMonitorTypes.None;
                        config.EbookMonitorExistingMode = MonitorTypes.SpecificBook;
                        config.EbookBooksToMonitor = books;
                        config.EbookBooksToSearch = _wishlist.SearchOnAdd ? books.ToList() : null;
                        config.EbookTags = tags;
                    }
                }

                config.MonitorMode = MonitorTypes.SpecificBook;
                config.SpecificBookProviderIds = new HashSet<string>(new[] { item.BookProviderId }, StringComparer.OrdinalIgnoreCase);
                config.Tags = new HashSet<int>((config.AudiobookTags ?? new HashSet<int>()).Concat(config.EbookTags ?? new HashSet<int>()));
                return config;
            }

            // The default root folder for the media type (Settings > Media Management), else the
            // first root folder set up for it.
            private (string Path, ResolvedRootFolderSettings Settings) Root(BookMediaType mediaType)
            {
                if (_roots.TryGetValue(mediaType, out var cached))
                {
                    return cached;
                }

                var folders = Safe(() => _service._rootFolderService.All()) ?? new List<RootFolder>();
                var preferred = mediaType == BookMediaType.Audiobook
                    ? Safe(() => _service._configService.DefaultAudiobookRootFolderPath)
                    : Safe(() => _service._configService.DefaultEbookRootFolderPath);

                var ordered = folders
                    .OrderBy(f => string.Equals(f.Path?.TrimEnd('/'), preferred?.TrimEnd('/'), StringComparison.Ordinal) ? 0 : 1)
                    .ThenBy(f => f.Id);

                foreach (var folder in ordered)
                {
                    var settings = Safe(() => _service._rootFolderSettingsResolver.ResolveSettings(folder, mediaType));
                    if (settings?.IsConfigured == true && settings.QualityProfileId > 0 && settings.MetadataProfileId > 0)
                    {
                        _roots[mediaType] = (folder.Path, settings);
                        return _roots[mediaType];
                    }
                }

                _roots[mediaType] = (null, null);
                return _roots[mediaType];
            }

            private T Safe<T>(Func<T> read)
            {
                try
                {
                    return read();
                }
                catch (Exception ex)
                {
                    _service._logger.Debug(ex, "Talearr: wishlist read failed");
                    return default;
                }
            }
        }
    }
}
