using System.Linq;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Talearr: asking for the other reading while the book itself, still without files, is wanted
    // for one reading (Allseerr's "both" sends the audiobook, then asks for the abridged recording
    // too) adds a row of its own instead of re-pinning the book.
    [TestFixture]
    public class TalearrRecordingsKeepPrimaryFixture
    {
        private RecordingsWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private void Link()
        {
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
        }

        [Test]
        public void abridged_too_for_a_book_searching_its_unabridged_reading_gets_its_own_row()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            Link();

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "abridged", RequestedBy = "allseerr" });

            Assert.Multiple(() =>
            {
                Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
                Assert.That(outcome.Recording.BookId, Is.Not.EqualTo(3401));
                Assert.That(outcome.Recording.Abridged, Is.EqualTo("abridged"));
                Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28701), "the book keeps its unabridged reading");
                Assert.That(_world.SearchedBookIds, Is.EqualTo(new[] { outcome.Recording.BookId }));
            });
        }

        [Test]
        public void a_book_nobody_wants_yet_is_still_pinned()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            book.AudiobookMonitored = false;
            Link();

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "abridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Pinned));
            Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28705));
        }

        [Test]
        public void a_pinned_abridged_book_keeps_its_pin_when_the_unabridged_one_is_asked_for()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: false);
            _world.Editions.Single(e => e.Id == 28705).ManualAdd = true;
            Link();

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
            Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28705));
        }

        [Test]
        public void a_per_book_abridged_only_policy_keeps_the_book_on_its_abridged_reading()
        {
            var books = new FakeBookGrabPolicyService();
            books.Overrides["hc:378630"] = AbridgedAudiobookPolicy.AbridgedOnly;
            var recordings = new TalearrRecordingsService(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Groups, _world.Facts, _world.Resolver,
                _world.Store, _world.Settings, _world.CommandQueue, LogManager.GetCurrentClassLogger(), books);

            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: false);
            Link();

            var outcome = recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
            Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28705));
        }

        [Test]
        public void asking_for_the_reading_the_book_already_wants_still_pins_it()
        {
            _world.AddFirm(FirmEditions.ScottBrickUk(), withFiles: false);
            Link();

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.Not.EqualTo(AddRecordingStatus.Created));
            Assert.That(_world.Books.Values.Count(b => b.MediaType == BookMediaType.Audiobook), Is.EqualTo(1));
        }
    }
}
