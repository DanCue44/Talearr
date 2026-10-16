using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Catalog
{
    // TaleArr: opt-in cleanup of rows that store the same work twice for one media type. Dry run
    // unless DryRun is explicitly false. Run it through POST /api/v1/command
    // {"name": "TalearrDuplicateBookCleanup", "dryRun": false} or the
    // /api/v1/talearr/duplicates routes.
    public class TalearrDuplicateBookCleanupCommand : Command
    {
        public bool DryRun { get; set; } = true;
        public int? AuthorId { get; set; }

        public override bool SendUpdatesToClient => true;
    }

    public sealed class TalearrDuplicateRowReport
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public bool HasFiles { get; set; }
        public bool Monitored { get; set; }
        public bool Deliberate { get; set; }
    }

    public sealed class TalearrDuplicateGroupReport
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public string WorkIds { get; set; }
        public TalearrDuplicateRowReport Keep { get; set; }
        public List<TalearrDuplicateRowReport> Remove { get; set; } = new List<TalearrDuplicateRowReport>();
        public List<TalearrDuplicateRowReport> AlsoKept { get; set; } = new List<TalearrDuplicateRowReport>();
    }

    public sealed class TalearrDuplicateCleanupReport
    {
        public bool DryRun { get; set; }
        public int AuthorsScanned { get; set; }
        public int BooksScanned { get; set; }
        public int RowsToRemove { get; set; }
        public int RowsRemoved { get; set; }
        public List<TalearrDuplicateGroupReport> Groups { get; set; } = new List<TalearrDuplicateGroupReport>();
    }

    public interface ITalearrDuplicateBookCleanupService
    {
        TalearrDuplicateCleanupReport Run(bool dryRun, int? authorId = null);
    }

    public class TalearrDuplicateBookCleanupService : ITalearrDuplicateBookCleanupService, IExecute<TalearrDuplicateBookCleanupCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;
        private readonly object _runLock = new object();

        public TalearrDuplicateBookCleanupService(IAuthorService authorService,
                                                  IBookService bookService,
                                                  IEditionService editionService,
                                                  IMediaFileService mediaFileService,
                                                  Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public void Execute(TalearrDuplicateBookCleanupCommand message)
        {
            Run(message.DryRun, message.AuthorId);
        }

        public TalearrDuplicateCleanupReport Run(bool dryRun, int? authorId = null)
        {
            lock (_runLock)
            {
                var report = new TalearrDuplicateCleanupReport { DryRun = dryRun };

                var authors = authorId.HasValue
                    ? new[] { _authorService.GetAuthor(authorId.Value) }.Where(a => a != null).ToList()
                    : _authorService.GetAllAuthors();

                foreach (var author in authors)
                {
                    ScanAuthor(author, dryRun, report);
                }

                report.RowsToRemove = report.Groups.Sum(g => g.Remove.Count);

                _logger.Info("TaleArr: duplicate book cleanup {0}: {1} authors, {2} books scanned, {3} duplicate groups, {4} rows {5}",
                    dryRun ? "(dry run)" : "finished",
                    report.AuthorsScanned,
                    report.BooksScanned,
                    report.Groups.Count,
                    dryRun ? report.RowsToRemove : report.RowsRemoved,
                    dryRun ? "would be removed" : "removed");

                return report;
            }
        }

        private void ScanAuthor(Author author, bool dryRun, TalearrDuplicateCleanupReport report)
        {
            var books = _bookService.GetBooksByAuthor(author.Id) ?? new List<Book>();
            report.AuthorsScanned++;
            report.BooksScanned += books.Count;

            foreach (var book in books.Where(b => b != null && b.AuthorId <= 0))
            {
                book.AuthorId = author.Id;
            }

            // Only rows that share a work key with another row need more facts.
            var candidates = books
                .Where(b => b != null && b.Id > 0)
                .GroupBy(TalearrDuplicateBooks.GetWorkKey)
                .Where(g => g.Key != null && g.Count() > 1)
                .SelectMany(g => g)
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var ids = candidates.Select(b => b.Id).ToList();
            var editions = (_editionService.GetEditionsByBook(ids) ?? new List<Edition>())
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var facts = candidates.Select(book => new TalearrBookRowFacts
            {
                Book = book,
                Editions = editions.TryGetValue(book.Id, out var list) ? list : new List<Edition>(),
                HasFiles = HasFiles(book)
            }).ToList();

            foreach (var group in TalearrDuplicateBooks.FindGroups(facts))
            {
                var groupReport = new TalearrDuplicateGroupReport
                {
                    AuthorId = author.Id,
                    AuthorName = author.Name,
                    MediaType = group.MediaType.ToString().ToLowerInvariant(),
                    Title = group.Title,
                    WorkIds = group.WorkIds,
                    Keep = Describe(group.Keep),
                    Remove = group.Remove.Select(Describe).ToList(),
                    AlsoKept = group.Kept.Select(Describe).ToList()
                };

                report.Groups.Add(groupReport);

                foreach (var row in group.Remove)
                {
                    if (dryRun)
                    {
                        _logger.Info("TaleArr: duplicate '{0}' ({1}, {2}) row {3} would be removed; keeping {4}",
                            row.Book.Title, author.Name, groupReport.MediaType, row.Book.Id, group.Keep.Book.Id);
                        continue;
                    }

                    // Re-check right before deleting: files or monitoring may have changed since the scan.
                    var fresh = _bookService.GetBook(row.Book.Id);
                    if (fresh == null)
                    {
                        continue;
                    }

                    var freshFacts = new TalearrBookRowFacts
                    {
                        Book = fresh,
                        Editions = _editionService.GetEditionsByBook(fresh.Id) ?? new List<Edition>(),
                        HasFiles = HasFiles(fresh)
                    };

                    if (!TalearrDuplicateBooks.IsRemovable(freshFacts))
                    {
                        _logger.Info("TaleArr: duplicate '{0}' row {1} changed since the scan (files, monitoring or a manual add); keeping it", fresh.Title, fresh.Id);
                        continue;
                    }

                    _bookService.DeleteBook(fresh.Id, false);
                    report.RowsRemoved++;
                    _logger.Info("TaleArr: removed duplicate '{0}' ({1}, {2}) row {3}; kept {4}",
                        fresh.Title, author.Name, groupReport.MediaType, fresh.Id, group.Keep.Book.Id);
                }
            }
        }

        private bool HasFiles(Book book)
        {
            try
            {
                return (_mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>()).Any();
            }
            catch (Exception ex)
            {
                // Unknown counts as "has files": never remove what could not be checked.
                _logger.Debug(ex, "Unable to read files for book {0}", book.Id);
                return true;
            }
        }

        private static TalearrDuplicateRowReport Describe(TalearrBookRowFacts row)
        {
            return new TalearrDuplicateRowReport
            {
                BookId = row.Book.Id,
                Title = row.Book.Title,
                HasFiles = row.HasFiles,
                Monitored = row.IsMonitored,
                Deliberate = row.IsDeliberateInstance
            };
        }
    }
}
