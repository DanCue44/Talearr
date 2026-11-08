using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using Chaptarr.Core.Test.Talearr.Catalog;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Ebook.Epub;

namespace Chaptarr.Core.Test.Talearr.Ebook
{
    // Talearr: the EPUB conversion against a real migrated chaptarr.db, so the BookFiles NOT NULL
    // columns (MediaInfo among them) are enforced like on a live install.
    [TestFixture]
    [NonParallelizable]
    public class TalearrEpubDatabaseFixture
    {
        private sealed class FakeConverter : ITalearrEpubConverter
        {
            public bool IsAvailable() => true;

            public EpubConversionOutcome Convert(string inputPath, string outputPath)
            {
                File.WriteAllText(outputPath, "epub");
                return new EpubConversionOutcome { Success = true, OutputPath = outputPath, OutputSize = 4 };
            }
        }

        private sealed class FakeSettings : ITalearrEpubSettingsService
        {
            public TalearrEpubSettings Settings { get; set; } = new TalearrEpubSettings();
            public TalearrEpubSettings Get() => Settings.Clone();
            public TalearrEpubSettings Save(TalearrEpubSettings settings) => Settings = settings;
        }

        private TalearrMigratedDatabase _db;
        private MediaFileRepository _repository;
        private MediaFileService _mediaFiles;
        private FakeSettings _settings;
        private Book _book;
        private Edition _edition;
        private BookFile _source;
        private string _folder;
        private List<string> _recycled;

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
            _db = new TalearrMigratedDatabase("talearr_epub");
            var events = new TalearrRecordingEventAggregator();
            var (ingest, _) = Fake<IIngestQueueRepository>.Create();
            _repository = new MediaFileRepository(_db.MainDatabase, events);
            _mediaFiles = new MediaFileService(_repository, events, ingest, LogManager.GetLogger("TalearrEpubDatabaseFixture"));
            _settings = new FakeSettings();
            _recycled = new List<string>();

            var author = new Author { Id = 21, Name = "Amy Tan" };
            _book = new Book { Id = 6001, Title = "The Joy Luck Club", Author = author, AuthorId = 21, MediaType = BookMediaType.Ebook };
            _edition = new Edition { Id = 88, Title = "The Joy Luck Club", Book = _book };

            _folder = Path.Combine(_db.Folder, "Amy Tan", "The Joy Luck Club");
            Directory.CreateDirectory(_folder);
            var sourcePath = Path.Combine(_folder, "The Joy Luck Club.azw3");
            File.WriteAllText(sourcePath, "kindle");

            _source = _repository.Insert(new BookFile
            {
                Path = sourcePath,
                Size = 6,
                Modified = DateTime.UtcNow,
                DateAdded = DateTime.UtcNow,
                Quality = new QualityModel(Quality.AZW3),
                MediaInfo = new MediaInfoModel(),
                EditionId = 88,
                Part = 1,
                MediaType = "ebook"
            });
        }

        [TearDown]
        public void TearDown()
        {
            _db.Dispose();
        }

        private TalearrEpubService Service()
        {
            var (books, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBook), _ => _book)
                .On(nameof(IBookService.GetBooksByAuthor), _ => new List<Book> { _book });

            var (authors, authorFake) = Fake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAllAuthors), _ => new List<Author> { _book.Author });

            var (editions, editionFake) = Fake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEdition), _ => _edition);

            // The real service and repository write the rows; only the book lookup is narrowed to
            // this test's rows, as the seeded database has no Books or Editions to join.
            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), _ => _repository.All().ToList())
                .On(nameof(IMediaFileService.Add), args => _mediaFiles.Add((BookFile)args[0]))
                .On(nameof(IMediaFileService.DeleteMany), args =>
                {
                    _mediaFiles.DeleteMany((List<BookFile>)args[0], (DeleteMediaFileReason)args[1]);
                    return null;
                });

            var (recycle, recycleFake) = Fake<IRecycleBinProvider>.Create();
            recycleFake.On(nameof(IRecycleBinProvider.DeleteFile), args =>
            {
                _recycled.Add((string)args[0]);
                File.Delete((string)args[0]);
                return null;
            });

            var (queue, _) = Fake<IManageCommandQueue>.Create();

            return new TalearrEpubService(books, authors, editions, media, recycle, _settings, new FakeConverter(), queue, LogManager.GetLogger("TalearrEpubDatabaseFixture"));
        }

        private List<string> RowPaths()
        {
            using var conn = _db.Open();
            return conn.Query<string>("SELECT \"Path\" FROM \"BookFiles\" ORDER BY \"Id\"").ToList();
        }

        [Test]
        public void library_run_saves_the_converted_epub_as_a_valid_row()
        {
            Service().Execute(new TalearrConvertLibraryToEpubCommand());

            var epubPath = Path.Combine(_folder, "The Joy Luck Club.epub");
            Assert.That(RowPaths(), Is.EqualTo(new[] { _source.Path, epubPath }));

            using (var conn = _db.Open())
            {
                var mediaInfo = conn.ExecuteScalar<string>("SELECT \"MediaInfo\" FROM \"BookFiles\" WHERE \"Path\" = @epubPath", new { epubPath });
                Assert.That(mediaInfo, Is.Not.Null.And.Not.Empty);
            }

            var epub = _repository.All().Single(f => f.Path == epubPath);
            Assert.That(epub.MediaInfo, Is.Not.Null);
            Assert.That(epub.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(epub.MediaType, Is.EqualTo("ebook"));
            Assert.That(File.Exists(epubPath), Is.True);
            Assert.That(File.Exists(_source.Path), Is.True);
        }

        [Test]
        public void replacing_saves_the_epub_row_and_removes_the_kindle_row()
        {
            _settings.Settings.KeepOriginal = false;

            Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 });

            Assert.That(RowPaths(), Is.EqualTo(new[] { Path.Combine(_folder, "The Joy Luck Club.epub") }));
            Assert.That(_recycled, Is.EqualTo(new[] { _source.Path }));
        }

        [Test]
        public void a_failed_row_write_keeps_the_kindle_file_and_row_and_leaves_no_epub_behind()
        {
            _settings.Settings.KeepOriginal = false;

            // A stale row already holds the EPUB path (no file on disk), so the insert hits the
            // unique path index after the converter has written the EPUB.
            var epubPath = Path.Combine(_folder, "The Joy Luck Club.epub");
            using (var conn = _db.Open())
            {
                _db.Insert(conn, "BookFiles", new { Path = epubPath, EditionId = 999, MediaType = "ebook" });
            }

            var ex = Assert.Throws<TalearrEpubException>(() => Service().Execute(new TalearrConvertLibraryToEpubCommand()));

            Assert.That(ex.Message, Does.Contain("Converted 0 of 1"));
            Assert.That(File.Exists(epubPath), Is.False);
            Assert.That(File.Exists(_source.Path), Is.True);
            Assert.That(RowPaths(), Is.EqualTo(new[] { _source.Path, epubPath }));
            Assert.That(_recycled, Is.Empty);
        }
    }
}
