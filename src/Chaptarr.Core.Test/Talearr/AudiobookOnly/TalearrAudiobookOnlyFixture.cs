using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Catalog;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.AudiobookOnly;

namespace Chaptarr.Core.Test.Talearr.AudiobookOnly
{
    // Talearr: Audiobook Only (issue #12) against a database built by the real migrations.
    [TestFixture]
    [NonParallelizable]
    public class TalearrAudiobookOnlyFixture
    {
        // 1: audiobook row with an audio edition (wanted)
        // 2: audiobook row of an ebook-only book (hidden)
        // 3: ebook row of the same book (hidden)
        // 4: audiobook row of an ebook-only book that has a file (shown, not missing)
        // 5: audiobook row with an Audible ASIN on the book and an edition of unknown format (wanted)
        // 6: audiobook row with an Audible-catalog edition (Format "Audiobook", no reading format) (wanted)
        private const int Wanted = 1;
        private const int EbookOnlyAudiobookRow = 2;
        private const int EbookRow = 3;
        private const int WithFile = 4;
        private const int AudibleAsinOnBook = 5;
        private const int AudibleCatalogEdition = 6;

        private TalearrMigratedDatabase _db;
        private StubSettings _settings;

        private sealed class StubSettings : ITalearrAudiobookOnlySettingsService
        {
            public bool Enabled { get; set; }

            public bool IsEnabled() => Enabled;

            public bool Save(bool enabled)
            {
                Enabled = enabled;
                return enabled;
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _db = new TalearrMigratedDatabase("talearr_audiobook_only");
            _settings = new StubSettings();
            Seed();
        }

        [TearDown]
        public void TearDown()
        {
            _db.Dispose();
        }

        private BookRepository Books() => new BookRepository(_db.MainDatabase, new TalearrRecordingEventAggregator(), _settings);

        private TalearrAudiobookOnlyService Service() => new TalearrAudiobookOnlyService(Books(), _settings, LogManager.GetCurrentClassLogger());

        private List<int> MissingIds()
        {
            var spec = new PagingSpec<Book> { Page = 1, PageSize = 100, SortKey = "Books.Id", SortDirection = SortDirection.Ascending };
            return Books().BooksWithoutFiles(spec).Records.Select(b => b.Id).OrderBy(id => id).ToList();
        }

        [Test]
        public void off_by_default_keeps_every_missing_row()
        {
            Assert.That(MissingIds(), Is.EqualTo(new[] { Wanted, EbookOnlyAudiobookRow, EbookRow, AudibleAsinOnBook, AudibleCatalogEdition }));
            Assert.That(Service().HiddenBookIds(Enumerable.Range(1, 6)), Is.Empty);
        }

        [Test]
        public void wanted_missing_and_missing_searches_leave_out_books_with_no_audiobook_edition()
        {
            _settings.Enabled = true;

            Assert.That(MissingIds(), Is.EqualTo(new[] { Wanted, AudibleAsinOnBook, AudibleCatalogEdition }));

            var targets = Books().GetMissingBookSearchTargets(null, null).Select(t => t.BookId).OrderBy(id => id);
            Assert.That(targets, Is.EqualTo(new[] { Wanted, AudibleAsinOnBook, AudibleCatalogEdition }));

            var ebookTargets = Books().GetMissingBookSearchTargets(BookMediaType.Ebook, null);
            Assert.That(ebookTargets, Is.Empty);
        }

        [Test]
        public void lists_and_calendar_hide_rows_without_an_audiobook_edition_but_keep_rows_with_files()
        {
            _settings.Enabled = true;

            Assert.That(Service().HiddenBookIds(Enumerable.Range(1, 6)).OrderBy(id => id), Is.EqualTo(new[] { EbookOnlyAudiobookRow, EbookRow }));

            var ids = Books().GetBookIds(includeUnmonitored: true).OrderBy(id => id);
            Assert.That(ids, Is.EqualTo(new[] { Wanted, WithFile, AudibleAsinOnBook, AudibleCatalogEdition }));

            var buckets = Books().GetBookBuckets("title", "ASC", includeUnmonitored: true);
            Assert.That(buckets.TotalCount, Is.EqualTo(4));

            var calendar = Books().BooksBetweenDates(new DateTime(2020, 1, 1), new DateTime(2030, 1, 1), true).Select(b => b.Id).OrderBy(id => id);
            Assert.That(calendar, Is.EqualTo(new[] { Wanted, WithFile, AudibleAsinOnBook, AudibleCatalogEdition }));

            var visible = Service().Visible(Enumerable.Range(1, 6).Select(id => new Book { Id = id }).ToList()).Select(b => b.Id);
            Assert.That(visible, Is.EqualTo(new[] { Wanted, WithFile, AudibleAsinOnBook, AudibleCatalogEdition }));
        }

        [Test]
        public void author_counts_leave_out_hidden_rows()
        {
            var statistics = new AuthorStatisticsRepository(_db.MainDatabase, _settings);

            var before = statistics.AuthorStatistics(1);
            Assert.That(before.Sum(s => s.BookCount), Is.EqualTo(6));

            _settings.Enabled = true;

            var after = statistics.AuthorStatistics(1);
            Assert.That(after.Select(s => s.BookId).OrderBy(id => id), Is.EqualTo(new[] { Wanted, WithFile, AudibleAsinOnBook, AudibleCatalogEdition }));
            Assert.That(after.Sum(s => s.BookCount), Is.EqualTo(4));
            Assert.That(after.Sum(s => s.BookFileCount), Is.EqualTo(1));
        }

        [Test]
        public void a_new_audiobook_edition_brings_the_book_back()
        {
            _settings.Enabled = true;
            Assert.That(MissingIds(), Does.Not.Contain(EbookOnlyAudiobookRow));

            // What a metadata refresh does when Hardcover or Audible lists an audio edition.
            using (var conn = _db.Open())
            {
                AddEdition(conn, EbookOnlyAudiobookRow, "audio-later", monitored: false, readingFormatId: 2);
            }

            Assert.That(MissingIds(), Does.Contain(EbookOnlyAudiobookRow));
            Assert.That(Service().HiddenBookIds(new[] { EbookOnlyAudiobookRow }), Is.Empty);
        }

        [Test]
        public void turning_it_off_shows_everything_again()
        {
            _settings.Enabled = true;
            Assert.That(MissingIds(), Has.Count.EqualTo(3));

            _settings.Enabled = false;
            Assert.That(MissingIds(), Has.Count.EqualTo(5));
        }

        [Test]
        public void specification_soft_rejects_releases_for_hidden_books_only_while_on()
        {
            var spec = new TalearrAudiobookOnlySpecification(LogManager.GetCurrentClassLogger(), Service());

            RemoteBook Release(params int[] ids) => new RemoteBook
            {
                Release = new ReleaseInfo { Title = "release" },
                Books = ids.Select(id => new Book { Id = id }).ToList()
            };

            Assert.That(spec.IsSatisfiedBy(Release(EbookRow), null).Accepted, Is.True, "off");

            _settings.Enabled = true;

            var rejected = spec.IsSatisfiedBy(Release(EbookOnlyAudiobookRow), null);
            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.Category, Is.EqualTo(TalearrAudiobookOnlySpecification.Category));

            Assert.That(spec.IsSatisfiedBy(Release(EbookRow, EbookOnlyAudiobookRow), null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(Release(Wanted), null).Accepted, Is.True);
            Assert.That(spec.IsSatisfiedBy(Release(EbookOnlyAudiobookRow, Wanted), null).Accepted, Is.True, "a pack with a wanted book");
        }

        [Test]
        public void in_memory_check_matches_the_sql()
        {
            var audio = new Edition { ReadingFormatId = 2 };
            var ebook = new Edition { ReadingFormatId = 3 };
            var catalog = new Edition { Format = "Audiobook" };
            var timed = new Edition { DurationSeconds = 3600 };

            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Audiobook }, new[] { ebook, audio }), Is.True);
            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Audiobook }, new[] { ebook }), Is.False);
            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Audiobook }, new[] { catalog }), Is.True);
            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Audiobook }, new[] { timed }), Is.True);
            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Audiobook, AudibleASIN = "B0TEST" }, new Edition[0]), Is.True);
            Assert.That(TalearrAudiobookOnly.HasAudiobookEdition(new Book { MediaType = BookMediaType.Ebook }, new[] { audio }), Is.False);
        }

        private void Seed()
        {
            using var conn = _db.Open();
            var added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            _db.Insert(conn, "Authors", new
            {
                Id = 1,
                Name = "Test Author",
                CleanName = "testauthor",
                SortName = "test author",
                Path = "/audiobooks/Test Author",
                AudiobookPath = "/audiobooks/Test Author",
                AudiobookRootFolderPath = "/audiobooks",
                Monitored = 1,
                AudiobookMonitored = 1,
                EbookMonitored = 1,
                Added = added,
                Images = "[]",
                Links = "[]",
                Genres = "[]",
                Ratings = "{}",
                Tags = "[]",
                AddOptions = "{}"
            });

            AddBook(conn, Wanted, "The Constable", 0, null);
            AddEdition(conn, Wanted, "e1", monitored: true, readingFormatId: 2);
            AddEdition(conn, Wanted, "e1-ebook", monitored: false, readingFormatId: 3);

            AddBook(conn, EbookOnlyAudiobookRow, "An Ancient Light", 0, null);
            AddEdition(conn, EbookOnlyAudiobookRow, "e2", monitored: true, readingFormatId: 3);

            AddBook(conn, EbookRow, "An Ancient Light", 1, null);
            AddEdition(conn, EbookRow, "e3", monitored: true, readingFormatId: 3);

            AddBook(conn, WithFile, "Escape Velocity", 0, null);
            var editionWithFile = AddEdition(conn, WithFile, "e4", monitored: true, readingFormatId: 3);
            _db.Insert(conn, "BookFiles", new
            {
                EditionId = editionWithFile,
                Path = "/audiobooks/Test Author/Escape Velocity/part1.mp3",
                Size = 1024,
                Modified = added,
                DateAdded = added,
                Quality = "{\"quality\":1}",
                MediaInfo = "{}",
                CalibreId = 0,
                Part = 1
            });

            AddBook(conn, AudibleAsinOnBook, "The Constable 2", 0, "B0AUDIBLE");
            AddEdition(conn, AudibleAsinOnBook, "e5", monitored: true, readingFormatId: null);

            AddBook(conn, AudibleCatalogEdition, "The Constable 3", 0, null);
            AddEdition(conn, AudibleCatalogEdition, "az:B0CATALOG", monitored: true, readingFormatId: null, format: "Audiobook");
        }

        private void AddBook(IDbConnection conn, int id, string title, int mediaType, string audibleAsin)
        {
            _db.Insert(conn, "Books", new
            {
                Id = id,
                AuthorId = 1,
                Title = title,
                CleanTitle = title.ToLowerInvariant(),
                TitleSlug = $"slug-{id}",
                MediaType = mediaType,
                AudiobookMonitored = mediaType == 0 ? 1 : 0,
                EbookMonitored = mediaType == 1 ? 1 : 0,
                AnyEditionOk = 1,
                Added = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ReleaseDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                AudibleASIN = audibleAsin,
                Images = "[]",
                Links = "[]",
                Genres = "[]",
                Ratings = "{}",
                RelatedBooks = "[]",
                AddOptions = "{}"
            });
        }

        private int AddEdition(IDbConnection conn, int bookId, string foreignEditionId, bool monitored, int? readingFormatId, string format = null)
        {
            return _db.Insert(conn, "Editions", new
            {
                BookId = bookId,
                Title = foreignEditionId,
                ForeignEditionId = foreignEditionId,
                Monitored = monitored ? 1 : 0,
                ReadingFormatId = readingFormatId,
                Format = format,
                ReleaseDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                Images = "[]",
                Links = "[]",
                Ratings = "{}"
            });
        }
    }
}
