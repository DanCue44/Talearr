using System.Linq;
using Chaptarr.Core.Test.Talearr.BookEdits;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.PreferredSeries;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.PreferredSeries
{
    [TestFixture]
    public class TalearrPreferredSeriesFixture
    {
        private const int AuthorId = 7;

        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrPreferredSeriesStore _store;
        private FakeBookEditLibrary _library;
        private TalearrPreferredSeriesService _service;

        private Book _audiobook;
        private Book _ebook;
        private Series _discworld;
        private Series _watch;
        private Series _ebookDiscworld;
        private Series _ebookWatch;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrPreferredSeriesStore(_database, LogManager.GetLogger("TalearrTest"));
            _library = new FakeBookEditLibrary();
            _service = new TalearrPreferredSeriesService(_store, _library, LogManager.GetLogger("TalearrTest"));

            _audiobook = AddBook(1, BookMediaType.Audiobook);
            _ebook = AddBook(2, BookMediaType.Ebook);

            _discworld = _library.AddProviderSeries("Discworld", BookMediaType.Audiobook, "gr:1");
            _watch = _library.AddProviderSeries("Discworld - City Watch", BookMediaType.Audiobook, "gr:2");
            _ebookDiscworld = _library.AddProviderSeries("Discworld", BookMediaType.Ebook, "gr:1");
            _ebookWatch = _library.AddProviderSeries("Discworld - City Watch", BookMediaType.Ebook, "gr:2");

            _library.Link(_discworld, _audiobook, "8");
            _library.Link(_watch, _audiobook, "1");
            _library.Link(_ebookDiscworld, _ebook, "8");
            _library.Link(_ebookWatch, _ebook, "1");
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private Book AddBook(int id, BookMediaType mediaType)
        {
            var book = new Book
            {
                Id = id,
                AuthorId = AuthorId,
                Title = "Guards! Guards!",
                MediaType = mediaType,
                BaseBookId = "hc:777",
                SeriesName = "Discworld",
                SeriesPosition = "8"
            };

            _library.Books.Add(book);
            return book;
        }

        [Test]
        public void schema_thirteen_adds_the_preferred_series_table()
        {
            using var connection = _database.OpenConnection();
            Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.GreaterThanOrEqualTo(13));
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'PreferredSeries'"), Is.EqualTo(1));
        }

        [Test]
        public void without_a_choice_nothing_changes()
        {
            var details = _service.Get(_audiobook.Id);

            Assert.Multiple(() =>
            {
                Assert.That(details.PreferredSeries, Is.Null);
                Assert.That(details.CurrentSeries, Is.EqualTo("Discworld"));
                Assert.That(details.Options.Select(o => o.Title), Is.EqualTo(new[] { "Discworld", "Discworld - City Watch" }));
            });

            _service.Reapply(new[] { _audiobook, _ebook });
            Assert.That(_audiobook.SeriesName, Is.EqualTo("Discworld"));
            Assert.That(_audiobook.SeriesPosition, Is.EqualTo("8"));
        }

        [Test]
        public void choosing_a_series_writes_it_onto_every_row_of_the_work()
        {
            var result = _service.Set(_ebook.Id, "discworld - city watch");

            Assert.That(result.Status, Is.EqualTo(TalearrPreferredSeriesStatus.Ok));
            Assert.Multiple(() =>
            {
                Assert.That(result.Details.PreferredSeries, Is.EqualTo("Discworld - City Watch"));
                Assert.That(_audiobook.SeriesId, Is.EqualTo(_watch.Id));
                Assert.That(_audiobook.SeriesName, Is.EqualTo("Discworld - City Watch"));
                Assert.That(_audiobook.SeriesPosition, Is.EqualTo("1"));
                Assert.That(_ebook.SeriesId, Is.EqualTo(_ebookWatch.Id));
                Assert.That(_ebook.SeriesPosition, Is.EqualTo("1"));
            });

            // Read back from talearr.db.
            var stored = _store.GetAll().Single();
            Assert.That(stored.SeriesTitle, Is.EqualTo("Discworld - City Watch"));
            Assert.That(stored.Provider[_audiobook.Id].SeriesName, Is.EqualTo("Discworld"));
        }

        [Test]
        public void a_series_the_book_is_not_in_is_refused()
        {
            var result = _service.Set(_audiobook.Id, "Rincewind");

            Assert.That(result.Status, Is.EqualTo(TalearrPreferredSeriesStatus.Invalid));
            Assert.That(_audiobook.SeriesName, Is.EqualTo("Discworld"));
            Assert.That(_store.GetAll(), Is.Empty);
        }

        [Test]
        public void the_choice_survives_a_refresh_and_clearing_brings_back_the_newest_provider_series()
        {
            _service.Set(_audiobook.Id, "Discworld - City Watch");

            // A refresh sends the provider's main series again, under a new name since the choice.
            var remote = new Book { MediaType = BookMediaType.Audiobook, BaseBookId = "hc:777", SeriesName = "Discworld (Main)", SeriesPosition = "8" };
            _service.ApplyToRemoteBook(_audiobook, remote);

            Assert.Multiple(() =>
            {
                Assert.That(remote.SeriesName, Is.EqualTo("Discworld - City Watch"));
                Assert.That(remote.SeriesPosition, Is.EqualTo("1"));
                Assert.That(remote.SeriesId, Is.EqualTo(_watch.Id));
            });

            // Something else wrote the provider's value back; Reapply fixes it.
            _audiobook.SeriesName = "Discworld";
            _audiobook.SeriesPosition = "8";
            _service.Reapply(new[] { _audiobook });
            Assert.That(_audiobook.SeriesName, Is.EqualTo("Discworld - City Watch"));

            var cleared = _service.Set(_audiobook.Id, null);

            Assert.Multiple(() =>
            {
                Assert.That(cleared.Details.PreferredSeries, Is.Null);
                Assert.That(_audiobook.SeriesName, Is.EqualTo("Discworld (Main)"));
                Assert.That(_audiobook.SeriesPosition, Is.EqualTo("8"));
                Assert.That(_ebook.SeriesName, Is.EqualTo("Discworld"));
                Assert.That(_store.GetAll(), Is.Empty);
            });
        }

        [Test]
        public void a_book_that_left_the_series_keeps_the_metadata_series()
        {
            _service.Set(_audiobook.Id, "Discworld - City Watch");
            _library.Links.RemoveAll(l => l.SeriesId == _watch.Id);

            var remote = new Book { MediaType = BookMediaType.Audiobook, BaseBookId = "hc:777", SeriesName = "Discworld", SeriesPosition = "8" };
            _service.ApplyToRemoteBook(_audiobook, remote);

            Assert.That(remote.SeriesName, Is.EqualTo("Discworld"));
        }
    }
}
