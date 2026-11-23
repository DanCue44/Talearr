using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Recordings;
using NzbDrone.Core.Talearr.Titles;

namespace Chaptarr.Core.Test.Talearr.Titles
{
    internal sealed class FakeTitleSettings : ITalearrTitleSettingsService
    {
        public TalearrTitleSettings Settings { get; set; } = TalearrTitleSettings.Default;

        public TalearrTitleSettings Get() => Settings.Clone();

        public TalearrTitleSettings Save(TalearrTitleSettings settings)
        {
            Settings = settings.Clone();
            return Settings.Clone();
        }
    }

    internal sealed class FakeTitleLibrary : ITalearrTitleLibrary
    {
        public List<Book> Books { get; } = new List<Book>();
        public int AuthorReads { get; private set; }

        public Book GetBook(int bookId) => Books.FirstOrDefault(b => b.Id == bookId);

        public List<Book> GetAuthorBooks(int authorId)
        {
            AuthorReads++;
            return Books.Where(b => b.AuthorId == authorId).ToList();
        }
    }

    [TestFixture]
    public class TalearrDisplayTitleFixture
    {
        private const string Viridian = "The Viridian Gate Archives";

        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrTitleOverrideStore _store;
        private FakeTitleSettings _settings;
        private FakeTitleLibrary _library;
        private TalearrDisplayTitleService _service;

        private Book _book1;
        private Book _book1Ebook;
        private Book _book5;
        private Book _book3;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrTitleOverrideStore(_database, LogManager.GetLogger("TalearrTest"));
            _settings = new FakeTitleSettings();
            _library = new FakeTitleLibrary();
            _service = new TalearrDisplayTitleService(_settings, _store, _library, LogManager.GetLogger("TalearrTest"));

            _book1 = AddBook(1, "work-1", "Viridian Gate Online: Cataclysm", "1", BookMediaType.Audiobook);
            _book1Ebook = AddBook(2, "work-1", "Viridian Gate Online: Cataclysm", "1", BookMediaType.Ebook);
            _book3 = AddBook(3, "work-3", "Darkling Siege", "3", BookMediaType.Audiobook);
            _book5 = AddBook(5, "work-5", "Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5", "5", BookMediaType.Audiobook);
        }

        [TearDown]
        public void TearDown()
        {
            TalearrSearchTitles.Register(null);
            _folder.Dispose();
        }

        private Book AddBook(int id, string workKey, string title, string position, BookMediaType mediaType)
        {
            var book = new Book
            {
                Id = id,
                AuthorId = 7,
                BaseBookId = workKey,
                Title = title,
                SeriesName = Viridian,
                SeriesPosition = position,
                MediaType = mediaType
            };

            _library.Books.Add(book);
            return book;
        }

        private void CleanupOn()
        {
            _settings.Settings = new TalearrTitleSettings { CleanSeriesFromTitles = true };
        }

        [Test]
        public void should_leave_titles_alone_by_default()
        {
            Assert.That(_service.Resolve(_book1, _book1.Title), Is.EqualTo("Viridian Gate Online: Cataclysm"));
            Assert.That(_service.Resolve(_book5, _book5.Title), Is.EqualTo(_book5.Title));
            Assert.That(_library.AuthorReads, Is.EqualTo(0));
        }

        [Test]
        public void should_clean_series_from_titles_when_on()
        {
            CleanupOn();

            Assert.That(_service.Resolve(_book1, _book1.Title), Is.EqualTo("Cataclysm"));
            Assert.That(_service.Resolve(_book5, _book5.Title), Is.EqualTo("The Lich Priest"));
            Assert.That(_service.Resolve(_book3, _book3.Title), Is.EqualTo("Darkling Siege"));
        }

        [Test]
        public void should_not_treat_the_ebook_row_of_the_same_work_as_another_book()
        {
            CleanupOn();
            _library.Books.Remove(_book5);

            Assert.That(_service.Resolve(_book1, _book1.Title), Is.EqualTo("Viridian Gate Online: Cataclysm"));
        }

        [Test]
        public void should_read_the_author_books_once_for_a_page()
        {
            CleanupOn();

            _service.Resolve(_book1, _book1.Title);
            _service.Resolve(_book1Ebook, _book1Ebook.Title);
            _service.Resolve(_book5, _book5.Title);

            Assert.That(_library.AuthorReads, Is.EqualTo(1));
        }

        [Test]
        public void should_use_the_user_title_for_both_rows_of_the_work_and_over_cleanup()
        {
            CleanupOn();

            var result = _service.Set(_book1.Id, "  Cataclysm (VGO 1)  ");

            Assert.That(result.Status, Is.EqualTo(TalearrTitleOverrideStatus.Ok));
            Assert.That(result.Details.Title, Is.EqualTo("Cataclysm (VGO 1)"));
            Assert.That(result.Details.MetadataTitle, Is.EqualTo("Viridian Gate Online: Cataclysm"));
            Assert.That(result.Details.AutomaticTitle, Is.EqualTo("Cataclysm"));
            Assert.That(_service.Resolve(_book1, _book1.Title), Is.EqualTo("Cataclysm (VGO 1)"));
            Assert.That(_service.Resolve(_book1Ebook, _book1Ebook.Title), Is.EqualTo("Cataclysm (VGO 1)"));

            // The Books row keeps the metadata title.
            Assert.That(_book1.Title, Is.EqualTo("Viridian Gate Online: Cataclysm"));
        }

        [Test]
        public void should_keep_the_user_title_in_talearr_db()
        {
            _service.Set(_book5.Id, "The Lich Priest");

            var fresh = new TalearrDisplayTitleService(_settings, new TalearrTitleOverrideStore(_folder.CreateDatabase(), LogManager.GetLogger("TalearrTest")), _library, LogManager.GetLogger("TalearrTest"));

            Assert.That(fresh.Resolve(_book5, _book5.Title), Is.EqualTo("The Lich Priest"));
            Assert.That(fresh.Get(_book5.Id).Title, Is.EqualTo("The Lich Priest"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void should_clear_the_user_title(string cleared)
        {
            _service.Set(_book5.Id, "The Lich Priest");
            var result = _service.Set(_book5.Id, cleared);

            Assert.That(result.Status, Is.EqualTo(TalearrTitleOverrideStatus.Ok));
            Assert.That(result.Details.Title, Is.Null);
            Assert.That(_service.Resolve(_book5, _book5.Title), Is.EqualTo(_book5.Title));
        }

        [Test]
        public void should_refuse_a_title_that_is_too_long()
        {
            var result = _service.Set(_book5.Id, new string('a', TalearrDisplayTitleService.MaxTitleLength + 1));

            Assert.That(result.Status, Is.EqualTo(TalearrTitleOverrideStatus.Invalid));
            Assert.That(_service.Get(_book5.Id).Title, Is.Null);
        }

        [Test]
        public void should_report_a_missing_book()
        {
            Assert.That(_service.Set(99, "x").Status, Is.EqualTo(TalearrTitleOverrideStatus.NotFound));
            Assert.That(_service.Get(99), Is.Null);
        }

        [Test]
        public void should_offer_the_display_title_to_searches_once_started()
        {
            Assert.That(_service.AlternateTitle(_book1), Is.Null);

            CleanupOn();
            Assert.That(TalearrSearchTitles.ForSearch(_book1, _book1.Title), Is.Null);

            _service.Handle(new ApplicationStartedEvent());

            Assert.That(_service.AlternateTitle(_book1), Is.EqualTo("Cataclysm"));
            Assert.That(TalearrSearchTitles.ForSearch(_book1, _book1.Title), Is.EqualTo("Cataclysm"));
            Assert.That(TalearrSearchTitles.ForSearch(_book3, _book3.Title), Is.Null);

            _service.Set(_book3.Id, "Darkling Siege Remastered");
            Assert.That(TalearrSearchTitles.ForSearch(_book3, _book3.Title), Is.EqualTo("Darkling Siege Remastered"));
        }

        [Test]
        public void should_name_files_with_the_clean_title()
        {
            CleanupOn();

            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetLogger("TalearrTest"),
                displayTitles: new Lazy<ITalearrDisplayTitleService>(() => _service));

            var author = new Author { Id = 7, Name = "Tao Wong" };
            _book5.Author = author;
            var edition = new Edition { Title = _book5.Title, Book = _book5 };
            var bookFile = new BookFile { Path = "/books/x.m4b", Quality = new QualityModel(Quality.Unknown) };

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = "{Book Title}";

            Assert.That(builder.BuildBookFileName(author, edition, bookFile, namingConfig, new List<CustomFormat>()), Is.EqualTo("The Lich Priest"));

            _service.Set(_book5.Id, "Lich Priest");
            Assert.That(builder.BuildBookFileName(author, edition, bookFile, namingConfig, new List<CustomFormat>()), Is.EqualTo("Lich Priest"));
        }

        private class StubNamingConfigService : INamingConfigService
        {
            public NamingConfig GetConfig() => NamingConfig.Default;
            public void Save(NamingConfig namingConfig)
            {
            }
        }

        private class StubQualityDefinitionService : IQualityDefinitionService
        {
            public void Update(QualityDefinition qualityDefinition) => throw new NotImplementedException();
            public void UpdateMany(List<QualityDefinition> qualityDefinitions) => throw new NotImplementedException();
            public List<QualityDefinition> All() => throw new NotImplementedException();
            public QualityDefinition GetById(int id) => throw new NotImplementedException();
            public QualityDefinition Get(Quality quality) => new QualityDefinition(quality);
        }

        private class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }
    }
}
