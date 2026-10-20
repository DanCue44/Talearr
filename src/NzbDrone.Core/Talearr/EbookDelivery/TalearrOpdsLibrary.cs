using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    public sealed class TalearrOpdsFile
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public DateTime Added { get; set; }
    }

    public sealed class TalearrOpdsSeriesRef
    {
        public int SeriesId { get; set; }
        public string Title { get; set; }
        public string Position { get; set; }
    }

    public sealed class TalearrOpdsBook
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string AuthorSort { get; set; }
        public string Overview { get; set; }
        public string Language { get; set; }
        public string Publisher { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public DateTime Added { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<TalearrOpdsFile> Files { get; set; } = new List<TalearrOpdsFile>();
        public List<TalearrOpdsSeriesRef> Series { get; set; } = new List<TalearrOpdsSeriesRef>();
    }

    public sealed class TalearrOpdsGroup
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public string SortKey { get; set; }
        public int BookCount { get; set; }
        public DateTime Updated { get; set; }
    }

    // Everything the feed shows, built in one pass and kept briefly so paging through a large
    // library does not query the database for every page.
    public sealed class TalearrOpdsSnapshot
    {
        public DateTime Built { get; set; }
        public List<TalearrOpdsBook> Books { get; set; } = new List<TalearrOpdsBook>();
        public List<TalearrOpdsGroup> Authors { get; set; } = new List<TalearrOpdsGroup>();
        public List<TalearrOpdsGroup> Series { get; set; } = new List<TalearrOpdsGroup>();

        public DateTime Updated => Books.Count == 0 ? Built : Books.Max(b => b.Added);

        public TalearrOpdsBook FindBook(int bookId)
        {
            return Books.FirstOrDefault(b => b.BookId == bookId);
        }

        public (TalearrOpdsBook Book, TalearrOpdsFile File) FindFile(int fileId)
        {
            foreach (var book in Books)
            {
                var file = book.Files.FirstOrDefault(f => f.Id == fileId);
                if (file != null)
                {
                    return (book, file);
                }
            }

            return (null, null);
        }

        public List<TalearrOpdsBook> Recent()
        {
            return Books.OrderByDescending(b => b.Added).ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public List<TalearrOpdsBook> AllBooks()
        {
            return Books
                .OrderBy(b => b.AuthorSort, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<TalearrOpdsBook> ByAuthor(int authorId)
        {
            return Books
                .Where(b => b.AuthorId == authorId)
                .OrderBy(b => b.Series.FirstOrDefault()?.Title ?? "￿", StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => PositionKey(b.Series.FirstOrDefault()?.Position))
                .ThenBy(b => b.ReleaseDate ?? DateTime.MaxValue)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<TalearrOpdsBook> BySeries(int seriesId)
        {
            return Books
                .Where(b => b.Series.Any(s => s.SeriesId == seriesId))
                .OrderBy(b => PositionKey(b.Series.First(s => s.SeriesId == seriesId).Position))
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<TalearrOpdsBook> Search(string query)
        {
            var terms = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (terms.Length == 0)
            {
                return new List<TalearrOpdsBook>();
            }

            return Books
                .Where(b =>
                {
                    var haystack = Normalize(string.Join(" ", new[] { b.Title, b.Subtitle, b.AuthorName }
                        .Concat(b.Series.Select(s => s.Title))));
                    return terms.All(t => haystack.Contains(t, StringComparison.Ordinal));
                })
                .OrderBy(b => b.AuthorSort, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // "1", "2.5", "1-3" sort numerically by their first number; anything else sorts last.
        public static decimal PositionKey(string position)
        {
            if (string.IsNullOrWhiteSpace(position))
            {
                return decimal.MaxValue;
            }

            var digits = new string(position.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return decimal.TryParse(digits, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : decimal.MaxValue;
        }

        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);

            foreach (var c in text.Normalize(NormalizationForm.FormD))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    public interface ITalearrOpdsLibrary
    {
        TalearrOpdsSnapshot Get();
    }

    // Talearr: reads the ebook side of the library for the OPDS feed through the existing book,
    // author, series and file services. Read only; nothing is written anywhere.
    public class TalearrOpdsLibrary : ITalearrOpdsLibrary,
        IHandle<BookImportedEvent>,
        IHandle<BookFileDeletedEvent>,
        IHandle<BookDeletedEvent>
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);

        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _seriesBookLinkService;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrOpdsSnapshot _snapshot;

        public TalearrOpdsLibrary(IAuthorService authorService,
                                  IBookService bookService,
                                  IMediaFileService mediaFileService,
                                  ISeriesService seriesService,
                                  ISeriesBookLinkService seriesBookLinkService,
                                  Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _seriesService = seriesService;
            _seriesBookLinkService = seriesBookLinkService;
            _logger = logger;
        }

        public TalearrOpdsSnapshot Get()
        {
            lock (_lock)
            {
                if (_snapshot == null || DateTime.UtcNow - _snapshot.Built > MaxAge)
                {
                    _snapshot = Load();
                }

                return _snapshot;
            }
        }

        public void Handle(BookImportedEvent message) => Invalidate();

        public void Handle(BookFileDeletedEvent message) => Invalidate();

        public void Handle(BookDeletedEvent message) => Invalidate();

        private void Invalidate()
        {
            lock (_lock)
            {
                _snapshot = null;
            }
        }

        private TalearrOpdsSnapshot Load()
        {
            var books = (_bookService.GetAllBooks() ?? new List<Book>())
                .Where(b => b != null && b.MediaType == BookMediaType.Ebook)
                .ToList();

            var files = books.Count == 0
                ? new List<BookFile>()
                : _mediaFileService.GetFilesByBooks(books.Select(b => b.Id).ToList()) ?? new List<BookFile>();

            var ebookFiles = files.Where(IsEbookFile).ToList();
            var withFiles = ebookFiles.Select(f => f.Edition.BookId).ToHashSet();

            var links = withFiles.Count == 0
                ? new List<SeriesBookLink>()
                : _seriesBookLinkService.GetLinksByBook(withFiles.ToList()) ?? new List<SeriesBookLink>();

            var seriesIds = links.Select(l => l.SeriesId).ToHashSet();
            var series = seriesIds.Count == 0
                ? new List<Series>()
                : (_seriesService.GetAllSeries() ?? new List<Series>()).Where(s => seriesIds.Contains(s.Id)).ToList();

            var snapshot = Build(books, _authorService.GetAllAuthors() ?? new List<Author>(), ebookFiles, links, series, DateTime.UtcNow);
            _logger.Debug("Talearr: OPDS catalog built with {0} ebooks", snapshot.Books.Count);

            return snapshot;
        }

        public static bool IsEbookFile(BookFile file)
        {
            return file?.Edition != null &&
                   file.EditionId > 0 &&
                   !string.IsNullOrWhiteSpace(file.Path) &&
                   MediaFileExtensions.TextExtensions.Contains(Path.GetExtension(file.Path));
        }

        public static TalearrOpdsSnapshot Build(IEnumerable<Book> books,
                                                IEnumerable<Author> authors,
                                                IEnumerable<BookFile> files,
                                                IEnumerable<SeriesBookLink> links,
                                                IEnumerable<Series> series,
                                                DateTime now)
        {
            var authorsById = (authors ?? Enumerable.Empty<Author>()).Where(a => a != null).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            var seriesById = (series ?? Enumerable.Empty<Series>()).Where(s => s != null).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            var linksByBook = (links ?? Enumerable.Empty<SeriesBookLink>())
                .Where(l => l != null && seriesById.ContainsKey(l.SeriesId))
                .GroupBy(l => l.BookId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.IsPrimary).ThenBy(l => l.SeriesPosition).ToList());
            var filesByBook = (files ?? Enumerable.Empty<BookFile>())
                .Where(IsEbookFile)
                .GroupBy(f => f.Edition.BookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var snapshot = new TalearrOpdsSnapshot { Built = now };

            foreach (var book in (books ?? Enumerable.Empty<Book>()).Where(b => b != null).GroupBy(b => b.Id).Select(g => g.First()))
            {
                if (!filesByBook.TryGetValue(book.Id, out var bookFiles))
                {
                    continue;
                }

                authorsById.TryGetValue(book.AuthorId, out var author);
                var edition = bookFiles.Select(f => f.Edition).FirstOrDefault(e => e != null);

                var entry = new TalearrOpdsBook
                {
                    BookId = book.Id,
                    Title = string.IsNullOrWhiteSpace(book.Title) ? edition?.Title : book.Title,
                    Subtitle = string.IsNullOrWhiteSpace(edition?.Subtitle) ? book.Subtitle : edition.Subtitle,
                    AuthorId = book.AuthorId,
                    AuthorName = author?.Name ?? string.Empty,
                    AuthorSort = author?.SortNameLastFirst ?? author?.SortName ?? author?.Name ?? string.Empty,
                    Overview = string.IsNullOrWhiteSpace(edition?.Overview) ? book.Overview : edition.Overview,
                    Language = edition?.Language,
                    Publisher = edition?.Publisher,
                    ReleaseDate = edition?.ReleaseDate ?? book.ReleaseDate,
                    Genres = (book.Genres ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    Files = bookFiles
                        .GroupBy(f => f.Id)
                        .Select(g => g.First())
                        .Select(f => new TalearrOpdsFile
                        {
                            Id = f.Id,
                            Path = f.Path,
                            Size = f.Size,
                            Added = f.DateAdded == default ? f.Modified : f.DateAdded
                        })
                        .ToList()
                };

                entry.Added = entry.Files.Max(f => f.Added);

                if (linksByBook.TryGetValue(book.Id, out var bookLinks))
                {
                    entry.Series = bookLinks
                        .GroupBy(l => l.SeriesId)
                        .Select(g => g.First())
                        .Select(l => new TalearrOpdsSeriesRef
                        {
                            SeriesId = l.SeriesId,
                            Title = seriesById[l.SeriesId].Title,
                            Position = l.Position
                        })
                        .ToList();
                }

                snapshot.Books.Add(entry);
            }

            snapshot.Authors = snapshot.Books
                .GroupBy(b => b.AuthorId)
                .Select(g => new TalearrOpdsGroup
                {
                    Id = g.Key,
                    Title = g.First().AuthorName,
                    SortKey = g.First().AuthorSort,
                    BookCount = g.Count(),
                    Updated = g.Max(b => b.Added)
                })
                .OrderBy(a => a.SortKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            snapshot.Series = snapshot.Books
                .SelectMany(b => b.Series.Select(s => (Series: s, Book: b)))
                .GroupBy(x => x.Series.SeriesId)
                .Select(g => new TalearrOpdsGroup
                {
                    Id = g.Key,
                    Title = g.First().Series.Title,
                    SortKey = g.First().Series.Title,
                    BookCount = g.Select(x => x.Book.BookId).Distinct().Count(),
                    Updated = g.Max(x => x.Book.Added)
                })
                .OrderBy(s => s.SortKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return snapshot;
        }
    }
}
