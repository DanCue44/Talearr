using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.Audiobookshelf;

namespace NzbDrone.Core.Talearr.LinkExisting
{
    public sealed class TalearrLinkCandidateRequest
    {
        public string Title { get; set; }
        public string AuthorName { get; set; }

        // The local author when the book's author is already in Talearr; else found by name.
        public int? AuthorId { get; set; }
        public List<string> Asins { get; set; } = new List<string>();
        public List<string> Isbns { get; set; } = new List<string>();
    }

    public sealed class TalearrLinkCandidate
    {
        public const string SourceAudiobookshelf = "audiobookshelf";
        public const string SourceAuthorFolder = "authorFolder";

        public string Path { get; set; }
        public string Title { get; set; }
        public string Authors { get; set; }
        public string Narrators { get; set; }

        // audiobookshelf | authorFolder
        public string Source { get; set; }

        // The Audiobookshelf connection name, or the author folder.
        public string SourceName { get; set; }
        public double? DurationSeconds { get; set; }
        public int FileCount { get; set; }
        public double Score { get; set; }

        // Preselected in the Add Book modal: the same ASIN, or the author and the whole title match.
        public bool Strong { get; set; }

        // asin | isbn | title
        public string MatchedBy { get; set; }

        // Every audio file of the folder is already in Talearr, on another audiobook or unmapped.
        // Never preselected; linking it moves those files to the new book (database only).
        public bool TrackedElsewhere { get; set; }

        // Where those files are now, e.g. "In Talearr on: Renegade Star (audiobook)".
        public string TrackedOnLabel { get; set; }
        public List<TalearrLinkTrackedOn> TrackedOn { get; set; } = new List<TalearrLinkTrackedOn>();
    }

    public sealed class TalearrLinkTrackedOn
    {
        // 0 for unmapped files.
        public int BookId { get; set; }
        public string Title { get; set; }

        // audiobook | ebook | null for unmapped files
        public string MediaType { get; set; }
        public int FileCount { get; set; }
    }

    public sealed class TalearrLinkResult
    {
        public int BookId { get; set; }

        // Files queued for a Manual Import (untracked files).
        public int FileCount { get; set; }

        // Tracked files moved from another audiobook (or unmapped files) to this book.
        public int MovedCount { get; set; }

        // Tracked files that were not moved, with the reason.
        public int RefusedCount { get; set; }
        public List<string> Refusals { get; set; } = new List<string>();
        public int SkippedCount { get; set; }
        public int? CommandId { get; set; }
    }

    public interface ITalearrLinkExistingService
    {
        // Existing audiobook folders that look like the book being added: Audiobookshelf items
        // (through the connection's library mappings) and untracked folders in the author's
        // audiobook folder. Best first, at most MaxCandidates. Empty when nothing matches.
        List<TalearrLinkCandidate> FindCandidates(TalearrLinkCandidateRequest request);

        // Imports the audio files under the given paths to the book, exactly like a Manual Import
        // with the default Hardlink/Copy mode: files outside a root folder are hardlinked or copied
        // into the library and the originals are left alone; files already inside a root folder are
        // imported where they are.
        // A path whose audio files are all tracked already (on another audiobook, or unmapped) has
        // those files moved to the book instead, in the database only (issue #13).
        TalearrLinkResult Link(int bookId, IEnumerable<string> paths);
    }

    // Talearr: Link Existing Files in the Add Book modal (issue #13).
    public class TalearrLinkExistingService : ITalearrLinkExistingService
    {
        public const int MaxCandidates = 5;
        private const int MaxScannedFolders = 400;

        private readonly ITalearrAbsStore _absStore;
        private readonly ITalearrAbsConnections _absConnections;
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IDiskProvider _diskProvider;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IQueueService _queueService;
        private readonly Logger _logger;

        public TalearrLinkExistingService(ITalearrAbsStore absStore,
                                          ITalearrAbsConnections absConnections,
                                          IAuthorService authorService,
                                          IBookService bookService,
                                          IEditionService editionService,
                                          IMediaFileService mediaFileService,
                                          IDiskProvider diskProvider,
                                          IManageCommandQueue commandQueueManager,
                                          IQueueService queueService,
                                          Logger logger)
        {
            _queueService = queueService;
            _absStore = absStore;
            _absConnections = absConnections;
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _diskProvider = diskProvider;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public List<TalearrLinkCandidate> FindCandidates(TalearrLinkCandidateRequest request)
        {
            if (request == null || request.Title.IsNullOrWhiteSpace())
            {
                return new List<TalearrLinkCandidate>();
            }

            var candidates = new List<TalearrLinkCandidate>();

            try
            {
                candidates.AddRange(FromAudiobookshelf(request));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: Link Existing Files could not read the Audiobookshelf cache");
            }

            try
            {
                candidates.AddRange(FromAuthorFolder(request));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: Link Existing Files could not scan the author folder");
            }

            // One entry per folder (an Audiobookshelf item inside the author folder shows once, with
            // its Audiobookshelf details). A folder with untracked audio files is offered as before;
            // a folder whose files Talearr already tracks on another audiobook (or as unmapped files)
            // is offered with where they are now, never preselected (issue #13: a Publisher's Pack
            // attached to book 1 of the series).
            var distinct = candidates
                .GroupBy(c => NormalizePath(c.Path), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(c => c.Source == TalearrLinkCandidate.SourceAudiobookshelf ? 0 : 1).First())
                .ToList();

            var result = new List<TalearrLinkCandidate>();
            foreach (var candidate in distinct.OrderByDescending(c => c.Strong).ThenByDescending(c => c.Score).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                var files = AudioFilesOf(candidate.Path);
                if (files.Count == 0)
                {
                    continue;
                }

                var known = Known(files);
                var untracked = Untracked(files, known);
                if (untracked.Count > 0)
                {
                    candidate.FileCount = untracked.Count;
                }
                else
                {
                    var movable = WithHomes(known).Where(h => MoveRefusal(h.File, h.Home, 0) == null).ToList();
                    if (movable.Count == 0)
                    {
                        continue;
                    }

                    candidate.TrackedElsewhere = true;
                    candidate.Strong = false;
                    candidate.FileCount = movable.Count;
                    candidate.TrackedOn = movable
                        .GroupBy(h => h.Home?.Id ?? 0)
                        .OrderBy(g => g.Key == 0 ? 1 : 0)
                        .ThenBy(g => g.Key)
                        .Select(g => new TalearrLinkTrackedOn
                        {
                            BookId = g.Key,
                            Title = g.First().Home?.Title,
                            MediaType = g.First().Home == null ? null : MediaTypeName(g.First().Home.MediaType),
                            FileCount = g.Count()
                        })
                        .ToList();
                    candidate.TrackedOnLabel = TrackedOnLabel(candidate.TrackedOn);
                }

                result.Add(candidate);

                if (result.Count >= MaxCandidates)
                {
                    break;
                }
            }

            // Only the best strong untracked match is preselected (tracked files are never strong).
            var preselected = false;
            foreach (var candidate in result)
            {
                candidate.Strong = candidate.Strong && !preselected;
                preselected |= candidate.Strong;
            }

            return result;
        }

        public TalearrLinkResult Link(int bookId, IEnumerable<string> paths)
        {
            var book = _bookService.GetBook(bookId);
            if (book == null)
            {
                throw new ArgumentException($"Book {bookId} was not found");
            }

            if (book.MediaType != BookMediaType.Audiobook)
            {
                throw new ArgumentException("Existing files can only be linked to an audiobook");
            }

            var editions = _editionService.GetEditionsByBook(bookId) ?? new List<Edition>();
            var edition = editions.FirstOrDefault(e => e.Monitored) ?? editions.OrderBy(e => e.Id).FirstOrDefault();
            if (edition == null)
            {
                throw new ArgumentException($"Book {bookId} has no edition to link files to");
            }

            // Per path: untracked files are imported (a folder with some tracked files imports only
            // the new ones, as before); a path whose files are all tracked already has them moved.
            var untracked = new List<string>();
            var toMove = new List<(BookFile File, Book Home)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = 0;

            foreach (var path in (paths ?? Enumerable.Empty<string>()).Where(p => p.IsNotNullOrWhiteSpace()))
            {
                var pathFiles = AudioFilesOf(path).Where(seen.Add).ToList();
                if (pathFiles.Count == 0)
                {
                    continue;
                }

                var known = Known(pathFiles);
                var pathUntracked = Untracked(pathFiles, known);
                if (pathUntracked.Count > 0)
                {
                    untracked.AddRange(pathUntracked);
                    skipped += pathFiles.Count - pathUntracked.Count;
                }
                else
                {
                    toMove.AddRange(WithHomes(known));
                }
            }

            var result = new TalearrLinkResult
            {
                BookId = bookId,
                FileCount = untracked.Count,
                SkippedCount = skipped
            };

            if (toMove.Count > 0)
            {
                MoveTrackedFiles(book, edition, toMove, result);
            }

            if (untracked.Count == 0)
            {
                if (result.MovedCount == 0)
                {
                    _logger.Info("Talearr: Link Existing Files found no new audio files for [{0}]", book);
                }

                return result;
            }

            var importFiles = untracked.Select(path => new ManualImportFile
            {
                Path = path,
                AuthorId = book.AuthorId,
                BookId = book.Id,
                EditionId = edition.Id,
                SelectionSource = ManualImportSelectionSource.Automatic,
                Quality = new QualityModel(MediaFileExtensions.GetQualityForExtension(System.IO.Path.GetExtension(path))),

                // The user picked the book; the import must not move the files to another one.
                DisableReleaseSwitching = true
            }).ToList();

            // ImportMode.Copy is the Manual Import default (Hardlink/Copy Files).
            var command = _commandQueueManager.Push(new ManualImportCommand
            {
                Files = importFiles,
                ImportMode = ImportMode.Copy,
                ReplaceExistingFiles = false
            });

            result.CommandId = command?.Id;
            _logger.Info("Talearr: linking {0} existing file(s) to [{1}] with a Manual Import", importFiles.Count, book);
            return result;
        }

        private IEnumerable<TalearrLinkCandidate> FromAudiobookshelf(TalearrLinkCandidateRequest request)
        {
            var connections = (_absConnections?.GetConnections() ?? new List<TalearrAbsConnection>())
                .ToDictionary(c => c.Id);

            if (connections.Count == 0)
            {
                yield break;
            }

            foreach (var item in _absStore?.GetItems() ?? new List<TalearrAbsItem>())
            {
                if (item == null || !item.HasAudio || item.LocalPath.IsNullOrWhiteSpace() || !connections.TryGetValue(item.ConnectionId, out var connection))
                {
                    continue;
                }

                var match = TalearrLinkMatch.Score(request, item.Title, item.Subtitle, item.Authors, item.Asin, item.Isbn);
                if (match == null)
                {
                    continue;
                }

                yield return new TalearrLinkCandidate
                {
                    Path = item.LocalPath,
                    Title = item.Subtitle.IsNotNullOrWhiteSpace() && item.Title?.IndexOf(item.Subtitle, StringComparison.OrdinalIgnoreCase) < 0
                        ? $"{item.Title}: {item.Subtitle}"
                        : item.Title,
                    Authors = item.Authors,
                    Narrators = item.Narrators,
                    Source = TalearrLinkCandidate.SourceAudiobookshelf,
                    SourceName = connection.Name,
                    DurationSeconds = item.DurationSeconds,
                    Score = match.Score,
                    Strong = match.Strong,
                    MatchedBy = match.MatchedBy
                };
            }
        }

        private IEnumerable<TalearrLinkCandidate> FromAuthorFolder(TalearrLinkCandidateRequest request)
        {
            Author author = null;
            if (request.AuthorId is > 0)
            {
                author = _authorService.GetAuthor(request.AuthorId.Value);
            }
            else if (request.AuthorName.IsNotNullOrWhiteSpace())
            {
                author = _authorService.FindByName(request.AuthorName);
            }

            var authorFolder = author == null
                ? null
                : new[] { author.AudiobookPath, author.Path }.FirstOrDefault(p => p.IsNotNullOrWhiteSpace() && _diskProvider.FolderExists(p));

            if (authorFolder == null)
            {
                yield break;
            }

            // Book folders sit right under the author folder or one level down (Series/Book).
            var folders = new List<string>();
            foreach (var child in _diskProvider.GetDirectories(authorFolder))
            {
                folders.Add(child);
                folders.AddRange(_diskProvider.GetDirectories(child));
                if (folders.Count >= MaxScannedFolders)
                {
                    break;
                }
            }

            // The folder belongs to the author, so only the title has to fit.
            var folderRequest = new TalearrLinkCandidateRequest
            {
                Title = request.Title,
                AuthorName = author.Name,
                Asins = request.Asins,
                Isbns = request.Isbns
            };

            foreach (var folder in folders.Take(MaxScannedFolders))
            {
                var name = System.IO.Path.GetFileName(folder.TrimEnd('/', '\\'));
                var match = TalearrLinkMatch.Score(folderRequest, name, null, author.Name, null, null);
                if (match == null)
                {
                    continue;
                }

                yield return new TalearrLinkCandidate
                {
                    Path = folder,
                    Title = name,
                    Authors = author.Name,
                    Source = TalearrLinkCandidate.SourceAuthorFolder,
                    SourceName = authorFolder,
                    Score = match.Score,
                    Strong = match.Strong,
                    MatchedBy = match.MatchedBy
                };
            }
        }

        // Audio files of a folder (recursively) or the file itself.
        private List<string> AudioFilesOf(string path)
        {
            try
            {
                if (_diskProvider.FileExists(path))
                {
                    return IsAudio(path) ? new List<string> { path } : new List<string>();
                }

                if (_diskProvider.FolderExists(path))
                {
                    return _diskProvider.GetFiles(path, true).Where(IsAudio).OrderBy(f => f, StringComparer.Ordinal).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: Link Existing Files could not read {0}", path);
            }

            return new List<string>();
        }

        // The BookFile rows Talearr already has for these paths.
        private List<BookFile> Known(List<string> files)
        {
            if (files.Count == 0)
            {
                return new List<BookFile>();
            }

            return (_mediaFileService.GetFileWithPath(files) ?? new List<BookFile>())
                .Where(f => f != null && f.Path.IsNotNullOrWhiteSpace())
                .ToList();
        }

        private static List<string> Untracked(List<string> files, List<BookFile> known)
        {
            var paths = known.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            return files.Where(f => !paths.Contains(f)).ToList();
        }

        // Each tracked file with the book it is on (null for an unmapped file).
        private List<(BookFile File, Book Home)> WithHomes(List<BookFile> files)
        {
            var editionIds = files.Where(f => f.EditionId > 0).Select(f => f.EditionId).Distinct().ToList();
            var editions = editionIds.Count == 0
                ? new List<Edition>()
                : _editionService.GetEditions(editionIds) ?? new List<Edition>();
            var bookOfEdition = editions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().BookId);

            var bookIds = bookOfEdition.Values.Where(id => id > 0).Distinct().ToList();
            var books = (bookIds.Count == 0 ? new List<Book>() : _bookService.GetBooks(bookIds) ?? new List<Book>())
                .GroupBy(b => b.Id)
                .ToDictionary(g => g.Key, g => g.First());

            return files.Select(f =>
            {
                Book home = null;
                if (f.EditionId > 0 && bookOfEdition.TryGetValue(f.EditionId, out var homeId))
                {
                    books.TryGetValue(homeId, out home);
                }

                return (f, home);
            }).ToList();
        }

        // Why a tracked file may not move to the audiobook being linked, or null when it may. Only
        // audiobook files on another audiobook (or unmapped files) move; an ebook keeps its files.
        private static string MoveRefusal(BookFile file, Book home, int targetBookId)
        {
            if (string.Equals(file.MediaType, "ebook", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{file.Path}' is an ebook file";
            }

            if (home != null && home.MediaType != BookMediaType.Audiobook)
            {
                return $"'{file.Path}' belongs to the ebook '{home.Title}'";
            }

            if (home != null && targetBookId > 0 && home.Id == targetBookId)
            {
                return $"'{file.Path}' is already on this book";
            }

            return null;
        }

        private void MoveTrackedFiles(Book book, Edition edition, List<(BookFile File, Book Home)> files, TalearrLinkResult result)
        {
            var busy = new BusyCheck(_queueService, _commandQueueManager, _logger);
            var moved = new List<(BookFile File, Book Home)>();

            foreach (var (file, home) in files)
            {
                if (home != null && home.Id == book.Id)
                {
                    result.SkippedCount++;
                    continue;
                }

                var refusal = MoveRefusal(file, home, book.Id) ?? busy.Refusal(file, home, book);
                if (refusal != null)
                {
                    result.RefusedCount++;
                    result.Refusals.Add(refusal);
                    _logger.Info("Talearr: Link Existing Files did not move a file to [{0}]: {1}", book, refusal);
                    continue;
                }

                file.EditionId = edition.Id;
                file.Edition = edition;
                moved.Add((file, home));
            }

            if (moved.Count == 0)
            {
                return;
            }

            _mediaFileService.Update(moved.Select(m => m.File).ToList());
            result.MovedCount = moved.Count;

            foreach (var (file, home) in moved)
            {
                _logger.Info("Talearr: Link Existing Files moved '{0}' from {1} to [{2}] (edition {3}); nothing on disk changed",
                    file.Path,
                    home == null ? "the unmapped files" : $"'{home.Title}' (book row {home.Id})",
                    book,
                    edition.Id);
            }
        }

        private static string MediaTypeName(BookMediaType mediaType)
        {
            return mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
        }

        private static string TrackedOnLabel(List<TalearrLinkTrackedOn> homes)
        {
            var books = homes.Where(h => h.BookId > 0).Select(h => $"{h.Title} ({h.MediaType})").ToList();
            var unmapped = homes.Any(h => h.BookId == 0);

            if (books.Count == 0)
            {
                return "In Talearr as Unmapped Files";
            }

            if (unmapped)
            {
                books.Add("Unmapped Files");
            }

            return $"In Talearr on: {string.Join(", ", books)}";
        }

        // A file with a download or an import in progress is never moved: the queue has an item for
        // its book (or the item's output folder holds it), or a Manual Import / download scan that
        // includes it is queued or running. An unreadable queue counts as busy.
        private sealed class BusyCheck
        {
            private readonly IQueueService _queueService;
            private readonly IManageCommandQueue _commands;
            private readonly Logger _logger;
            private bool _read;
            private List<Queue.Queue> _queue;
            private List<Command> _running;

            public BusyCheck(IQueueService queueService, IManageCommandQueue commands, Logger logger)
            {
                _queueService = queueService;
                _commands = commands;
                _logger = logger;
            }

            public string Refusal(BookFile file, Book home, Book target)
            {
                Read();

                if (_queue == null)
                {
                    return $"'{file.Path}' was not moved: the download queue could not be read";
                }

                var bookIds = new[] { home?.Id ?? 0, target.Id }.Where(id => id > 0).ToHashSet();
                var download = _queue.FirstOrDefault(q =>
                    (q.Book != null && bookIds.Contains(q.Book.Id)) ||
                    (q.TargetBookIds != null && q.TargetBookIds.Any(bookIds.Contains)) ||
                    (q.OutputPath.IsNotNullOrWhiteSpace() && (q.OutputPath.PathEquals(file.Path) || q.OutputPath.IsParentPath(file.Path))));
                if (download != null)
                {
                    return $"'{file.Path}' has a download in progress ({download.Title})";
                }

                if (_running == null)
                {
                    return $"'{file.Path}' was not moved: the running imports could not be read";
                }

                var import = _running.FirstOrDefault(c => c switch
                {
                    ManualImportCommand manual => manual.Files?.Any(f => f?.Path != null && f.Path.PathEquals(file.Path)) == true,
                    DownloadedBooksScanCommand scan => scan.Path.IsNotNullOrWhiteSpace() && (scan.Path.PathEquals(file.Path) || scan.Path.IsParentPath(file.Path)),
                    _ => false
                });

                return import == null ? null : $"'{file.Path}' has an import in progress";
            }

            private void Read()
            {
                if (_read)
                {
                    return;
                }

                _read = true;

                try
                {
                    _queue = _queueService?.GetQueue()?.Where(q => q != null).ToList() ?? new List<Queue.Queue>();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: Link Existing Files could not read the queue");
                    _queue = null;
                }

                try
                {
                    _running = (_commands?.All() ?? new List<CommandModel>())
                        .Where(c => c?.Body != null && (c.Status == CommandStatus.Queued || c.Status == CommandStatus.Started))
                        .Select(c => c.Body)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: Link Existing Files could not read the running commands");
                    _running = null;
                }
            }
        }

        private static bool IsAudio(string path)
        {
            return MediaFileExtensions.AudioExtensions.Contains(System.IO.Path.GetExtension(path ?? string.Empty));
        }

        private static string NormalizePath(string path)
        {
            return (path ?? string.Empty).TrimEnd('/', '\\');
        }
    }
}
