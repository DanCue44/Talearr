using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Talearr.Ebook.Epub
{
    public class TalearrEpubException : NzbDroneException
    {
        public TalearrEpubException(string message)
            : base(message)
        {
        }
    }

    // Talearr: library side of the Kindle to EPUB conversion. Runs the TalearrConvertToEpub
    // command (the optional after-import conversion) and the library-wide one. The EPUB is added
    // to the same edition next to the Kindle file; the Kindle file is kept unless the settings say
    // to replace it, and then it is removed (to the recycle bin when one is set) only after the
    // EPUB has been checked and registered.
    public class TalearrEpubService : IExecute<TalearrConvertToEpubCommand>, IExecute<TalearrConvertLibraryToEpubCommand>, IHandle<BookImportedEvent>
    {
        // When an edition has several Kindle files, the one KindleUnpack rebuilds best goes first.
        private static readonly string[] SourcePreference = { ".azw3", ".azw", ".mobi", ".prc" };

        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly ITalearrEpubSettingsService _settingsService;
        private readonly ITalearrEpubConverter _converter;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public TalearrEpubService(IBookService bookService,
                                  IAuthorService authorService,
                                  IEditionService editionService,
                                  IMediaFileService mediaFileService,
                                  IRecycleBinProvider recycleBinProvider,
                                  ITalearrEpubSettingsService settingsService,
                                  ITalearrEpubConverter converter,
                                  IManageCommandQueue commandQueueManager,
                                  Logger logger,
                                  IEventAggregator eventAggregator = null)
        {
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _recycleBinProvider = recycleBinProvider;
            _settingsService = settingsService;
            _converter = converter;
            _commandQueueManager = commandQueueManager;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Handle(BookImportedEvent message)
        {
            try
            {
                if (message?.Book == null || !_settingsService.Get().ConvertAfterImport)
                {
                    return;
                }

                var kindle = (message.ImportedBooks ?? new List<BookFile>()).Any(IsKindleEbook);
                if (!kindle)
                {
                    return;
                }

                _logger.Info("Talearr: queueing EPUB conversion of '{0}' after import", message.Book.Title);
                _commandQueueManager.Push(new TalearrConvertToEpubCommand { BookId = message.Book.Id, AfterImport = true });
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not queue the EPUB conversion after import");
            }
        }

        public void Execute(TalearrConvertToEpubCommand message)
        {
            var book = _bookService.GetBook(message.BookId);
            var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);
            var result = ConvertBook(book, author, string.Empty);

            if (result.Errors.Any())
            {
                throw new TalearrEpubException("EPUB conversion failed, the original file was kept. " + string.Join(" ", result.Errors));
            }
        }

        // Talearr: the existing library. Every ebook edition with a Kindle file and no EPUB gets
        // one, one book after another; a failed book keeps its file and the run goes on. Needs
        // KindleUnpack up front, so nothing is half done when it is missing.
        public void Execute(TalearrConvertLibraryToEpubCommand message)
        {
            if (!_converter.IsAvailable())
            {
                throw new TalearrEpubException("KindleUnpack and Python 3 are needed to convert to EPUB and were not found. Nothing was changed.");
            }

            _logger.ProgressInfo("Looking for Kindle ebooks to convert to EPUB");

            var candidates = FindLibraryCandidates(message.AuthorId);
            if (candidates.Count == 0)
            {
                _logger.ProgressInfo("No Kindle ebooks without an EPUB");
                return;
            }

            var converted = 0;
            var failed = new List<string>();

            for (var i = 0; i < candidates.Count; i++)
            {
                var (book, author) = candidates[i];
                var prefix = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] ", i + 1, candidates.Count);

                try
                {
                    var result = ConvertBook(book, author, prefix);
                    converted += result.Converted;
                    failed.AddRange(result.Errors);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Talearr: EPUB conversion of '{0}' failed, original file kept", book.Title);
                    failed.Add($"{book.Title}: {ex.Message}");
                }
            }

            var summary = string.Format(CultureInfo.InvariantCulture, "Converted {0} of {1} ebooks to EPUB", converted, candidates.Count);
            if (failed.Any())
            {
                throw new TalearrEpubException($"{summary}; {failed.Count} failed and kept their original files. " + string.Join(" ", failed));
            }

            _logger.ProgressInfo(summary);
        }

        public static bool IsKindleEbook(BookFile file)
        {
            return file?.Path != null &&
                   !string.Equals(file.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase) &&
                   TalearrEpubConverter.ConvertibleExtensions.Contains(Path.GetExtension(file.Path));
        }

        public static bool IsEpub(BookFile file)
        {
            return file?.Path != null && string.Equals(Path.GetExtension(file.Path), ".epub", StringComparison.OrdinalIgnoreCase);
        }

        // One Kindle file per edition that has no EPUB yet, the most convertible first.
        public static List<BookFile> PlanSources(IEnumerable<BookFile> files)
        {
            return (files ?? Enumerable.Empty<BookFile>())
                .Where(f => f != null)
                .GroupBy(f => f.EditionId)
                .Where(g => !g.Any(IsEpub))
                .Select(g => g.Where(IsKindleEbook)
                    .OrderBy(f => Array.IndexOf(SourcePreference, Path.GetExtension(f.Path).ToLowerInvariant()))
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault())
                .Where(f => f != null)
                .ToList();
        }

        internal List<(Book Book, Author Author)> FindLibraryCandidates(int authorId)
        {
            var authors = authorId > 0
                ? new List<Author> { _authorService.GetAuthor(authorId) }
                : _authorService.GetAllAuthors() ?? new List<Author>();

            var candidates = new List<(Book, Author)>();
            foreach (var author in authors.Where(a => a != null).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var books = (_bookService.GetBooksByAuthor(author.Id) ?? new List<Book>())
                    .Where(b => b != null && b.MediaType == BookMediaType.Ebook)
                    .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase);

                foreach (var book in books)
                {
                    if (PlanSources(_mediaFileService.GetFilesByBook(book.Id)).Any())
                    {
                        book.Author ??= author;
                        candidates.Add((book, author));
                    }
                }
            }

            return candidates;
        }

        private (int Converted, List<string> Errors) ConvertBook(Book book, Author author, string prefix)
        {
            var sources = PlanSources(_mediaFileService.GetFilesByBook(book.Id));
            var errors = new List<string>();

            if (sources.Count == 0)
            {
                _logger.ProgressInfo("{0}Nothing to convert for '{1}': no Kindle ebook without an EPUB", prefix, book.Title);
                return (0, errors);
            }

            if (!_converter.IsAvailable())
            {
                throw new TalearrEpubException("KindleUnpack and Python 3 are needed to convert to EPUB and were not found.");
            }

            var keepOriginal = _settingsService.Get().KeepOriginal;
            var converted = 0;

            foreach (var source in sources)
            {
                var edition = GetEdition(source.EditionId);
                var title = edition?.Title ?? book.Title;
                var outputPath = Path.ChangeExtension(source.Path, ".epub");

                if (File.Exists(outputPath))
                {
                    errors.Add($"{title}: {Path.GetFileName(outputPath)} is already in the folder. Rescan the author to add it.");
                    continue;
                }

                _logger.ProgressInfo("{0}Converting '{1}' to EPUB", prefix, title);

                var outcome = _converter.Convert(source.Path, outputPath);
                if (!outcome.Success)
                {
                    _logger.Warn("Talearr: EPUB conversion of '{0}' failed, original file kept: {1}", title, outcome.Error);
                    errors.Add($"{title}: {outcome.Error}");
                    continue;
                }

                var epub = AddEpub(source, outcome, author, edition);
                var removed = keepOriginal ? new List<BookFile>() : RemoveOriginal(source);
                converted++;

                _logger.ProgressInfo("{0}Converted '{1}' to EPUB{2}", prefix, title, keepOriginal ? ", the Kindle file was kept" : string.Empty);

                PublishImported(author, book, epub, removed);
            }

            return (converted, errors);
        }

        private BookFile AddEpub(BookFile source, EpubConversionOutcome outcome, Author author, Edition edition)
        {
            var epub = new BookFile
            {
                Path = outcome.OutputPath,
                Size = outcome.OutputSize,
                Modified = File.GetLastWriteTimeUtc(outcome.OutputPath),
                DateAdded = DateTime.UtcNow,
                OriginalFilePath = source.OriginalFilePath,
                SceneName = source.SceneName,
                ReleaseGroup = source.ReleaseGroup,
                Quality = new QualityModel(Quality.EPUB, source.Quality?.Revision ?? new Revision()),
                IndexerFlags = source.IndexerFlags,
                EditionId = source.EditionId,
                Part = 1,
                PartCount = 1,
                MediaType = "ebook",
                MatchProvenance = source.MatchProvenance,
                Author = author,
                Edition = edition
            };

            try
            {
                return _mediaFileService.Add(epub);
            }
            catch (Exception)
            {
                // The Kindle file is still the library's file; drop the unregistered EPUB.
                TryDelete(outcome.OutputPath);
                throw;
            }
        }

        private List<BookFile> RemoveOriginal(BookFile source)
        {
            _mediaFileService.DeleteMany(new List<BookFile> { source }, DeleteMediaFileReason.Upgrade);

            try
            {
                _recycleBinProvider.DeleteFile(source.Path);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: the EPUB was added but the Kindle file could not be removed: {0}", source.Path);
            }

            return new List<BookFile> { source };
        }

        // Talearr: tell the rest of Talearr and the notifications (media server rescans) about the
        // new file, like an import.
        private void PublishImported(Author author, Book book, BookFile epub, List<BookFile> oldFiles)
        {
            if (_eventAggregator == null)
            {
                return;
            }

            try
            {
                _eventAggregator.PublishEvent(new BookImportedEvent(author, book, new List<BookFile> { epub }, oldFiles, true, null));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: the EPUB was added but the import notifications failed for '{0}'", book.Title);
            }
        }

        private Edition GetEdition(int editionId)
        {
            try
            {
                return editionId > 0 ? _editionService.GetEdition(editionId) : null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Edition {0} not found", editionId);
                return null;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not delete {0}", path);
            }
        }
    }
}
