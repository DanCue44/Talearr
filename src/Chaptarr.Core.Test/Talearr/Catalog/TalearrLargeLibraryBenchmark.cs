using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Chaptarr.Api.V1.Books;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // Talearr: large-library benchmark (2,000 authors, 40,000 books, 100,000 editions, 12,000
    // files) on the real migrated schema. Explicit: run it by name, it prints timings.
    //   dotnet test ... --filter "FullyQualifiedName~TalearrLargeLibraryBenchmark"
    [TestFixture]
    [Explicit("benchmark")]
    [NonParallelizable]
    public class TalearrLargeLibraryBenchmark
    {
        private TalearrMigratedDatabase _db;
        private AuthorRepository _authors;
        private BookRepository _books;
        private EditionRepository _editions;
        private SeriesBookLinkRepository _seriesLinks;
        private MediaFileRepository _files;
        private AuthorStatisticsService _statistics;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            var seedTimer = Stopwatch.StartNew();
            _db = new TalearrMigratedDatabase("talearr_benchmark");
            TalearrLargeLibrary.Seed(_db, authors: 2000, booksPerAuthor: 20, editionsPerBook: 2);
            TestContext.Progress.WriteLine($"seeded in {seedTimer.ElapsedMilliseconds} ms");

            var events = new TalearrRecordingEventAggregator();
            _authors = new AuthorRepository(_db.MainDatabase, events, LogManager.GetLogger("bench"));
            _books = new BookRepository(_db.MainDatabase, events);
            _editions = new EditionRepository(_db.MainDatabase, events);
            _seriesLinks = new SeriesBookLinkRepository(_db.MainDatabase, events);
            _files = new MediaFileRepository(_db.MainDatabase, events);
            _statistics = new AuthorStatisticsService(new AuthorStatisticsRepository(_db.MainDatabase), new CacheManager());
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _db?.Dispose();
        }

        private static void Measure(string name, Action action)
        {
            action();
            var runs = new List<long>();
            for (var i = 0; i < 3; i++)
            {
                var timer = Stopwatch.StartNew();
                action();
                runs.Add(timer.ElapsedMilliseconds);
            }

            TestContext.Progress.WriteLine($"{name,-60} median {runs.OrderBy(x => x).ElementAt(1),6} ms");
        }

        // A cold statistics cache: what every request pays after an import or edit invalidates it.
        private void ColdStatistics()
        {
            _statistics.InvalidateAuthorCache(0);
        }

        [Test]
        public void library_endpoints()
        {
            TestContext.Progress.WriteLine($"rows: authors {Count("Authors")}, books {Count("Books")}, editions {Count("Editions")}, files {Count("BookFiles")}");

            // GET /api/v1/author (AuthorController.AllAuthors)
            Measure("author index: authors All()", () => _authors.All().ToList());
            Measure("author index: stats audiobook+ebook (cold)", () =>
            {
                ColdStatistics();
                _statistics.AuthorStatistics("audiobook");
                _statistics.AuthorStatistics("ebook");
            });
            Measure("author index: next+last books", () =>
            {
                var ids = _authors.All().Select(a => a.Id).ToList();
                _books.GetNextBooks(ids);
                _books.GetLastBooks(ids);
            });

            var allAuthorIds = _authors.All().Select(a => a.Id).ToList();
            var next = _books.GetNextBooks(allAuthorIds);
            var last = _books.GetLastBooks(allAuthorIds);
            Measure("author index: link next/last (list scan per author)", () =>
            {
                foreach (var id in allAuthorIds)
                {
                    next.FirstOrDefault(x => x.AuthorId == id);
                    last.FirstOrDefault(x => x.AuthorId == id);
                }
            });
            Measure("author index: link next/last (lookup)", () =>
            {
                var nextByAuthor = BookIndexLookup(next);
                var lastByAuthor = BookIndexLookup(last);
                foreach (var id in allAuthorIds)
                {
                    nextByAuthor.TryGetValue(id, out _);
                    lastByAuthor.TryGetValue(id, out _);
                }
            });

            // GET /api/v1/book (BookController.GetBooks, general path)
            Measure("book index: books All()", () => _books.All().ToList());
            var allBookIds = _books.All().Select(b => b.Id).ToList();
            Measure("book index: series links for all books (once)", () => _seriesLinks.GetLinksByBook(allBookIds));
            Measure("book index: all monitored editions", () => _editions.GetAllMonitoredEditions());
            Measure("book index: stats (cold)", () =>
            {
                ColdStatistics();
                _statistics.AuthorStatistics((string)null);
            });

            var books = _books.All().ToList();
            var authorsById = _authors.All().ToDictionary(a => a.Id);
            foreach (var book in books)
            {
                book.Author = authorsById[book.AuthorId];
                book.Editions = new List<Edition>();
                book.BookFiles = new List<BookFile>();
                book.SeriesLinks = new List<SeriesBookLink>();
            }

            Measure("book index: ToResource x40k", () => books.Select(b => b.ToResource(new BookResourceMappingOptions { IncludeAuthor = false })).ToList());

            // Wanted > Missing and the calendar map a page through MapToResource, which builds
            // statistics for the books on the page.
            var page = new PagingSpec<Book> { Page = 1, PageSize = 20, SortKey = "releaseDate", SortDirection = NzbDrone.Core.Datastore.SortDirection.Descending };
            page.FilterExpressions.Add(AuthorExtensions.GetBookMonitoringFilter(null, true));
            Measure("wanted/missing: page query", () => _books.BooksWithoutFiles(page));
            var pageBooks = _books.BooksWithoutFiles(page).Records;
            Measure("wanted/missing: page stats, whole library (cold)", () =>
            {
                ColdStatistics();
                _statistics.AuthorStatistics();
            });
            Measure("wanted/missing: page stats, page authors only (cold)", () =>
            {
                ColdStatistics();
                foreach (var authorId in pageBooks.Select(b => b.AuthorId).Distinct())
                {
                    _statistics.AuthorStatistics(authorId);
                }
            });

            Measure("calendar: 30 days", () => _books.BooksBetweenDates(DateTime.UtcNow.AddDays(-15), DateTime.UtcNow.AddDays(15), false));

            // Author details page (authorId path): books, editions and files of one author.
            Measure("author details: books+editions+files", () =>
            {
                var authorBooks = _books.GetBooksByAuthorId(1000).ToList();
                var ids = authorBooks.Select(b => b.Id).ToList();
                _editions.FindByBook(ids);
                _files.GetFilesByBooks(ids);
            });

            // GET /book?titleSlug=... (Allseerr links): old path loaded the library (books + series
            // links) to find one slug; new path asks for the slug.
            using (var conn = _db.Open())
            {
                Dapper.SqlMapper.Execute(conn, "UPDATE Books SET TitleSlug = 'slug-' || Id");
            }

            Measure("book slug route: old (all books + series links)", () =>
            {
                var all = _books.All().ToList();
                _seriesLinks.GetLinksByBook(all.Select(b => b.Id).ToList());
                all.Where(b => string.Equals(b.TitleSlug, "slug-31337", StringComparison.OrdinalIgnoreCase)).ToList();
            });
            Measure("book slug route: new (FindAllBySlug)", () => _books.FindAllBySlug("SLUG-31337"));

            Measure("duplicates finder", () => new TalearrDuplicateFinder(_db.MainDatabase).Find());
        }

        private static Dictionary<int, Book> BookIndexLookup(List<Book> books)
        {
            return books.GroupBy(b => b.AuthorId).ToDictionary(g => g.Key, g => g.First());
        }

        private int Count(string table)
        {
            using var conn = _db.Open();
            return Dapper.SqlMapper.ExecuteScalar<int>(conn, $"SELECT COUNT(*) FROM \"{table}\"");
        }
    }
}
