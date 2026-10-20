using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Wishlists;

namespace NzbDrone.Core.Talearr.MamFiller
{
    // Talearr: "Filler Import Preferred Picks". Once a preferred filler torrent has finished
    // downloading, the book is found in the metadata and added the way Talearr adds one wanted
    // book (author added with only that book monitored for that format, no search; an author
    // already in the library just gets that book monitored), then the finished download is
    // imported with ImportMode.Copy, which copies or hardlinks (Use Hardlinks setting) and never
    // moves. The torrent stays in the filler category, so it keeps seeding, the Seeding page keeps
    // it as a filler row and the slot math is unchanged.
    public class TalearrMamFillerLibraryService : ITalearrMamFillerLibrary
    {
        // Passes (the filler task runs hourly) to wait for the queued author add.
        public const int MaxAddWaits = 48;
        public const int MaxImportAttempts = 3;

        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly ISearchForNewBook _bookSearch;
        private readonly IMediaFileService _mediaFileService;
        private readonly IImportListExclusionService _exclusionService;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IConfigService _configService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IDownloadedBooksImportService _importService;
        private readonly Logger _logger;

        public TalearrMamFillerLibraryService(IAuthorService authorService,
                                              IBookService bookService,
                                              ISearchForNewBook bookSearch,
                                              IMediaFileService mediaFileService,
                                              IImportListExclusionService exclusionService,
                                              IPendingAuthorImportService pendingAuthorImportService,
                                              IRootFolderService rootFolderService,
                                              IRootFolderSettingsResolver rootFolderSettingsResolver,
                                              IConfigService configService,
                                              IManageCommandQueue commandQueueManager,
                                              IDownloadedBooksImportService importService,
                                              Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _bookSearch = bookSearch;
            _mediaFileService = mediaFileService;
            _exclusionService = exclusionService;
            _pendingAuthorImportService = pendingAuthorImportService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _configService = configService;
            _commandQueueManager = commandQueueManager;
            _importService = importService;
            _logger = logger;
        }

        public List<string> LibraryAuthorNames()
        {
            return _authorService.GetAllAuthors()
                .SelectMany(a => new[] { a.Name }.Concat(a.Aliases ?? new List<string>()))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public bool Advance(TalearrFillerPick pick, TalearrFillerDownload download, DateTime now)
        {
            if (pick == null || !pick.IsOpen)
            {
                return false;
            }

            if (download == null)
            {
                return Close(pick, TalearrFillerPick.StateGone, "The torrent is no longer in the download client", now);
            }

            if (!download.Completed || string.IsNullOrWhiteSpace(download.OutputPath))
            {
                return false;
            }

            try
            {
                return pick.LibraryState == TalearrFillerPick.StateWaiting
                    ? AddToLibrary(pick, download, now)
                    : Import(pick, download, now);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "MAM freeleech filler: library step failed for '{0}'", pick.Title);
                pick.Attempts++;
                pick.LibraryMessage = ex.Message;
                pick.UpdatedAt = now;
                if (pick.Attempts >= MaxImportAttempts)
                {
                    pick.LibraryState = TalearrFillerPick.StateFailed;
                }

                return true;
            }
        }

        private bool AddToLibrary(TalearrFillerPick pick, TalearrFillerDownload download, DateTime now)
        {
            var mediaType = MediaType(pick);

            if (string.IsNullOrWhiteSpace(pick.BookProviderId))
            {
                var row = new TalearrWishlistRow { Title = pick.Title, Author = pick.Author };
                var match = TalearrWishlistService.Pick(_bookSearch.SearchForNewBook(pick.Title, pick.Author, false), row, trustIdentifier: false);
                pick.BookProviderId = TalearrWishlistService.BookProviderId(match);
                pick.AuthorProviderId = TalearrWishlistService.AuthorProviderId(match?.Author);

                if (pick.BookProviderId == null)
                {
                    return Close(pick, TalearrFillerPick.StateNoMatch, "No matching book in the metadata; seed-only", now);
                }
            }

            if (IsExcluded(pick))
            {
                return Close(pick, TalearrFillerPick.StateExcluded, "On the import list exclusions; seed-only", now);
            }

            var book = FindLocal(pick, mediaType);
            if (book != null)
            {
                pick.BookId = book.Id;
                if (HasFile(book, mediaType))
                {
                    return Close(pick, TalearrFillerPick.StateInLibrary, "Already in the library; seed-only", now);
                }

                if (!book.IsMonitoredForMediaType(mediaType))
                {
                    _bookService.SetMonitoredForMediaType(new[] { book.Id }, Format(mediaType), true);
                    EnsureAuthorFormat(book.AuthorId, mediaType);
                }

                pick.LibraryState = TalearrFillerPick.StateAdding;
                pick.Attempts = 0;
                return Import(pick, download, now);
            }

            if (string.IsNullOrWhiteSpace(pick.AuthorProviderId))
            {
                return Close(pick, TalearrFillerPick.StateNoMatch, "The match has no author id to add; seed-only", now);
            }

            var config = BuildConfig(pick, mediaType, out var problem);
            if (config == null)
            {
                return Close(pick, TalearrFillerPick.StateFailed, problem, now);
            }

            var pendingId = _pendingAuthorImportService.EnqueueAsync(pick.AuthorProviderId, config, config.RequestedBy).GetAwaiter().GetResult();
            if (pendingId <= 0)
            {
                return Close(pick, TalearrFillerPick.StateFailed, "The author could not be queued for import", now);
            }

            _commandQueueManager.Push(new ProcessPendingImportsCommand { BatchSize = 10, ContinueUntilEmpty = true }, CommandPriority.Normal, CommandTrigger.Scheduled);

            pick.LibraryState = TalearrFillerPick.StateAdding;
            pick.LibraryMessage = "Adding the book to the library";
            pick.Attempts = 0;
            pick.UpdatedAt = now;
            _logger.Info("MAM freeleech filler: adding '{0}' by {1} to the library ({2})", pick.Title, pick.Author ?? "unknown author", pick.PreferredReason);
            return true;
        }

        private bool Import(TalearrFillerPick pick, TalearrFillerDownload download, DateTime now)
        {
            var mediaType = MediaType(pick);
            var book = FindLocal(pick, mediaType);
            if (book == null)
            {
                pick.Attempts++;
                pick.UpdatedAt = now;
                if (pick.Attempts >= MaxAddWaits)
                {
                    return Close(pick, TalearrFillerPick.StateFailed, "The book was not added to the library", now);
                }

                pick.LibraryMessage = "Waiting for the book to be added to the library";
                return true;
            }

            pick.BookId = book.Id;
            if (HasFile(book, mediaType))
            {
                return Close(pick, TalearrFillerPick.StateImported, "In the library; the torrent keeps seeding", now);
            }

            var author = _authorService.GetAuthor(book.AuthorId);
            var remoteBook = new RemoteBook { Author = author, Books = new List<Book> { book } };

            // Copy never moves or deletes the source; the torrent keeps seeding from it.
            var results = _importService.ProcessPath(download.OutputPath, ImportMode.Copy, author, null, remoteBook) ?? new List<ImportResult>();

            if (results.Any(r => r.Result == ImportResultType.Imported))
            {
                _logger.Info("MAM freeleech filler: imported '{0}' into the library; the torrent keeps seeding", pick.Title);
                return Close(pick, TalearrFillerPick.StateImported, "Imported into the library; the torrent keeps seeding", now);
            }

            pick.Attempts++;
            pick.UpdatedAt = now;
            pick.LibraryMessage = "Import failed: " + (ImportProblem(results) ?? "no files were imported");
            if (pick.Attempts >= MaxImportAttempts)
            {
                pick.LibraryState = TalearrFillerPick.StateFailed;
            }

            return true;
        }

        internal static string ImportProblem(List<ImportResult> results)
        {
            foreach (var result in results ?? new List<ImportResult>())
            {
                var error = result.Errors?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ??
                            result.ImportDecision?.Rejections?.Select(r => r.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }

        private static bool Close(TalearrFillerPick pick, string state, string message, DateTime now)
        {
            pick.LibraryState = state;
            pick.LibraryMessage = message;
            pick.UpdatedAt = now;
            return true;
        }

        private static BookMediaType MediaType(TalearrFillerPick pick) => pick.IsAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;

        private static string Format(BookMediaType mediaType) => mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";

        private Book FindLocal(TalearrFillerPick pick, BookMediaType mediaType)
        {
            var id = pick.BookProviderId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var prefix = id.Contains(':') ? id.Substring(0, id.IndexOf(':')) : "hc";
            return _bookService.FindByProviderId(prefix, id, mediaType);
        }

        private bool HasFile(Book book, BookMediaType mediaType)
        {
            var format = Format(mediaType);
            return (_mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>())
                .Any(f => string.Equals(f.MediaType ?? "audiobook", format, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsExcluded(TalearrFillerPick pick)
        {
            var exclusions = _exclusionService.All() ?? new List<ImportListExclusion>();
            return exclusions.Any(e =>
                ImportListExclusionBookMatcher.AppliesToProviderId(e, pick.BookProviderId, null) ||
                (!string.IsNullOrWhiteSpace(pick.AuthorProviderId) && string.Equals(e.ForeignId, pick.AuthorProviderId, StringComparison.OrdinalIgnoreCase)));
        }

        // Only the author's format switch, as the wishlist sync does; nothing else is monitored.
        private void EnsureAuthorFormat(int authorId, BookMediaType mediaType)
        {
            var author = _authorService.GetAuthor(authorId);
            if (author != null && !author.IsMonitoredForMediaType(mediaType == BookMediaType.Audiobook) &&
                author.ApplyMediaTypeMonitoringSettings(mediaType, true, null))
            {
                _authorService.UpdateAuthor(author);
            }
        }

        // Same shape as the wishlist's "add one book": that book monitored for this format only,
        // nothing new monitored, and no search (the filler torrent is the source).
        private MonitoringConfig BuildConfig(TalearrFillerPick pick, BookMediaType mediaType, out string problem)
        {
            problem = null;
            var root = Root(mediaType);
            if (root.Path == null)
            {
                problem = $"No root folder is set up for {(mediaType == BookMediaType.Audiobook ? "audiobooks" : "ebooks")}";
                return null;
            }

            var books = new List<string> { pick.BookProviderId };
            var tags = new HashSet<int>(root.Settings.Tags ?? new List<int>());
            var config = new MonitoringConfig
            {
                CreateAudiobook = mediaType == BookMediaType.Audiobook,
                CreateEbook = mediaType == BookMediaType.Ebook,
                QueueIfUnavailable = true,
                RequestedBy = "Talearr MAM freeleech filler",
                AuthorName = pick.Author,
                SearchForMissingBooks = false,
                AudiobookMonitorExistingMode = MonitorTypes.None,
                EbookMonitorExistingMode = MonitorTypes.None,
                MonitorMode = MonitorTypes.SpecificBook,
                SpecificBookProviderIds = new HashSet<string>(books, StringComparer.OrdinalIgnoreCase),
                Tags = tags,

                // A new author keeps only this book (docs/TALEARR_TRACKED_AUTHORS.md).
                TalearrTrackedCatalog = true
            };

            if (mediaType == BookMediaType.Audiobook)
            {
                config.AudiobookRootFolderPath = root.Path;
                config.AudiobookQualityProfileId = root.Settings.QualityProfileId;
                config.AudiobookMetadataProfileId = root.Settings.MetadataProfileId;
                config.AudiobookMonitored = true;
                config.AudiobookMonitorNewItems = NewItemMonitorTypes.None;
                config.AudiobookMonitorExistingMode = MonitorTypes.SpecificBook;
                config.AudiobookBooksToMonitor = books;
                config.AudiobookTags = tags;
            }
            else
            {
                config.EbookRootFolderPath = root.Path;
                config.EbookQualityProfileId = root.Settings.QualityProfileId;
                config.EbookMetadataProfileId = root.Settings.MetadataProfileId;
                config.EbookMonitored = true;
                config.EbookMonitorNewItems = NewItemMonitorTypes.None;
                config.EbookMonitorExistingMode = MonitorTypes.SpecificBook;
                config.EbookBooksToMonitor = books;
                config.EbookTags = tags;
            }

            return config;
        }

        // The default root folder for the media type, else the first one set up for it.
        private (string Path, ResolvedRootFolderSettings Settings) Root(BookMediaType mediaType)
        {
            var folders = _rootFolderService.All() ?? new List<RootFolder>();
            var preferred = mediaType == BookMediaType.Audiobook
                ? _configService.DefaultAudiobookRootFolderPath
                : _configService.DefaultEbookRootFolderPath;

            var ordered = folders
                .OrderBy(f => string.Equals(f.Path?.TrimEnd('/'), preferred?.TrimEnd('/'), StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(f => f.Id);

            foreach (var folder in ordered)
            {
                var settings = _rootFolderSettingsResolver.ResolveSettings(folder, mediaType);
                if (settings?.IsConfigured == true && settings.QualityProfileId > 0 && settings.MetadataProfileId > 0)
                {
                    return (folder.Path, settings);
                }
            }

            return (null, null);
        }
    }
}
