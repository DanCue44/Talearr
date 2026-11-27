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
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
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
    }

    public sealed class TalearrLinkResult
    {
        public int BookId { get; set; }
        public int FileCount { get; set; }
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
        private readonly Logger _logger;

        public TalearrLinkExistingService(ITalearrAbsStore absStore,
                                          ITalearrAbsConnections absConnections,
                                          IAuthorService authorService,
                                          IBookService bookService,
                                          IEditionService editionService,
                                          IMediaFileService mediaFileService,
                                          IDiskProvider diskProvider,
                                          IManageCommandQueue commandQueueManager,
                                          Logger logger)
        {
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
            // its Audiobookshelf details), then drop folders Talearr already tracks.
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

                var untracked = Untracked(files);
                if (untracked.Count == 0)
                {
                    continue;
                }

                candidate.FileCount = untracked.Count;
                result.Add(candidate);

                if (result.Count >= MaxCandidates)
                {
                    break;
                }
            }

            // Only the best strong match is preselected.
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

            var files = (paths ?? Enumerable.Empty<string>())
                .Where(p => p.IsNotNullOrWhiteSpace())
                .SelectMany(AudioFilesOf)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var untracked = Untracked(files);
            var result = new TalearrLinkResult
            {
                BookId = bookId,
                FileCount = untracked.Count,
                SkippedCount = files.Count - untracked.Count
            };

            if (untracked.Count == 0)
            {
                _logger.Info("Talearr: Link Existing Files found no new audio files for [{0}]", book);
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

        private List<string> Untracked(List<string> files)
        {
            if (files.Count == 0)
            {
                return files;
            }

            var known = (_mediaFileService.GetFileWithPath(files) ?? new List<BookFile>())
                .Select(f => f.Path)
                .Where(p => p.IsNotNullOrWhiteSpace())
                .ToHashSet(StringComparer.Ordinal);

            return files.Where(f => !known.Contains(f)).ToList();
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
