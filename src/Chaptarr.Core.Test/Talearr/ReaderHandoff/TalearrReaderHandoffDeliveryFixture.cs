using System;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace Chaptarr.Core.Test.Talearr.ReaderHandoff
{
    // Talearr: the delivery log behind the reader hand-off (Chaptarr #130).
    [TestFixture]
    public class TalearrReaderHandoffDeliveryFixture
    {
        private TalearrTestFolder _dbFolder;
        private TalearrReaderHandoffStore _store;
        private FakeEventAggregator _events;
        private TalearrReaderHandoffService _service;
        private string _root;
        private string _library;
        private string _watch;
        private Author _author;

        [SetUp]
        public void SetUp()
        {
            _dbFolder = new TalearrTestFolder();
            _store = new TalearrReaderHandoffStore(_dbFolder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            _events = new FakeEventAggregator();
            _service = new TalearrReaderHandoffService(null, new FakeKepubify(), LogManager.GetCurrentClassLogger(), _store, _events);

            _root = Path.Combine(Path.GetTempPath(), "talearr-handoff-delivery-" + Guid.NewGuid().ToString("N"));
            _library = Path.Combine(_root, "library");
            _watch = Path.Combine(_root, "ingest");
            Directory.CreateDirectory(_library);
            Directory.CreateDirectory(_watch);
            _author = new Author { Name = "Navessa Allen" };
        }

        [TearDown]
        public void TearDown()
        {
            _dbFolder.Dispose();

            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private TalearrReaderHandoffSettings Settings()
        {
            return new TalearrReaderHandoffSettings { Enabled = true, Path = _watch };
        }

        private BookFile LibraryFile(int id, string name, string content)
        {
            var path = Path.Combine(_library, name);
            File.WriteAllText(path, content);
            return new BookFile { Id = id, Path = path };
        }

        private string[] WatchFiles()
        {
            return Directory.GetFiles(_watch).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        }

        [Test]
        public void a_replayed_import_is_not_sent_twice_even_after_the_reader_took_the_copy()
        {
            var file = LibraryFile(41, "Caught Up.epub", "v1");

            _service.HandOff(_author, new[] { file }, Settings());

            // The reader ingests and removes the copy.
            File.Delete(Path.Combine(_watch, "Caught Up.epub"));

            var written = _service.HandOff(_author, new[] { file }, Settings());

            Assert.That(written, Is.Empty);
            Assert.That(WatchFiles(), Is.Empty);
            Assert.That(_store.Get(41).State, Is.EqualTo(TalearrReaderHandoffStates.Delivered));
        }

        [Test]
        public void an_upgrade_or_changed_file_is_sent_again()
        {
            var original = LibraryFile(41, "Caught Up.epub", "v1");
            _service.HandOff(_author, new[] { original }, Settings());
            File.Delete(Path.Combine(_watch, "Caught Up.epub"));

            // An upgrade is a new library file.
            var upgrade = LibraryFile(42, "Caught Up.epub", "version two");

            Assert.That(_service.HandOff(_author, new[] { upgrade }, Settings()), Has.Count.EqualTo(1));
            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Caught Up.epub" }));
        }

        [Test]
        public void force_sends_a_delivered_file_again()
        {
            var file = LibraryFile(41, "Caught Up.epub", "v1");
            _service.HandOff(_author, new[] { file }, Settings());
            File.Delete(Path.Combine(_watch, "Caught Up.epub"));

            Assert.That(_service.HandOff(_author, new[] { file }, Settings(), force: true), Has.Count.EqualTo(1));
        }

        [Test]
        public void a_different_book_with_the_same_name_is_never_overwritten()
        {
            File.WriteAllText(Path.Combine(_watch, "Bride.epub"), "another book still waiting");
            var file = LibraryFile(50, "Bride.epub", "this book");

            var written = _service.HandOff(_author, new[] { file }, Settings());

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Bride (2).epub", "Bride.epub" }));
            Assert.That(File.ReadAllText(Path.Combine(_watch, "Bride.epub")), Is.EqualTo("another book still waiting"));
            Assert.That(written.Single(), Does.EndWith("Bride (2).epub"));
        }

        [Test]
        public void the_same_book_still_waiting_in_the_folder_is_not_copied_twice()
        {
            File.WriteAllText(Path.Combine(_watch, "Bride.epub"), "this book");
            var file = LibraryFile(50, "Bride.epub", "this book");

            _service.HandOff(_author, new[] { file }, Settings());

            Assert.That(WatchFiles(), Is.EqualTo(new[] { "Bride.epub" }));
        }

        [Test]
        public void a_missing_folder_is_recorded_as_a_failure_and_announced()
        {
            var file = LibraryFile(60, "Deep End.epub", "x");
            var settings = Settings();
            settings.Path = Path.Combine(_root, "not-there");

            Assert.Throws<DirectoryNotFoundException>(() => _service.HandOff(_author, new[] { file }, settings));

            var failure = _store.GetFailed().Single();
            Assert.That(failure.BookFileId, Is.EqualTo(60));
            Assert.That(failure.Attempts, Is.EqualTo(1));
            Assert.That(failure.LastError, Does.Contain("does not exist"));
            Assert.That(_events.Events.OfType<TalearrReaderHandoffDeliveriesChangedEvent>().Count(), Is.EqualTo(1));

            Assert.Throws<DirectoryNotFoundException>(() => _service.HandOff(_author, new[] { file }, settings));
            Assert.That(_store.GetFailed().Single().Attempts, Is.EqualTo(2));
        }

        [Test]
        public void a_successful_retry_clears_the_failure()
        {
            var file = LibraryFile(60, "Deep End.epub", "x");
            var broken = Settings();
            broken.Path = Path.Combine(_root, "not-there");
            Assert.Throws<DirectoryNotFoundException>(() => _service.HandOff(_author, new[] { file }, broken));

            _service.HandOff(_author, new[] { file }, Settings(), force: true);

            Assert.That(_store.GetFailed(), Is.Empty);
            Assert.That(_store.Get(60).State, Is.EqualTo(TalearrReaderHandoffStates.Delivered));
        }

        [Test]
        public void health_message_counts_failures_only_when_the_hand_off_is_on()
        {
            Assert.That(TalearrReaderHandoffCheck.GetFailedMessage(Settings(), 0), Is.Null);
            Assert.That(TalearrReaderHandoffCheck.GetFailedMessage(new TalearrReaderHandoffSettings(), 3), Is.Null);
            Assert.That(TalearrReaderHandoffCheck.GetFailedMessage(Settings(), 1), Does.Contain("1 ebook could not"));
            Assert.That(TalearrReaderHandoffCheck.GetFailedMessage(Settings(), 3), Does.Contain("3 ebooks could not"));
        }

        [TestCase("Book.epub", "Book (2).epub")]
        [TestCase("Book.kepub.epub", "Book (2).kepub.epub")]
        public void free_name_counts_up_and_keeps_the_extension(string name, string expected)
        {
            File.WriteAllText(Path.Combine(_watch, name), "x");

            Assert.That(Path.GetFileName(TalearrReaderHandoffService.FreeName(Path.Combine(_watch, name))), Is.EqualTo(expected));
        }
    }
}
