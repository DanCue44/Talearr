using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using QueueItem = NzbDrone.Core.Queue.Queue;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Talearr (issue #3): the same recording twice on one book, the primary row "Missing" and an
    // import copy holding the files. The duplicate merge folds them into the primary row, and
    // Make Primary lets the user pick which recording is the primary one.
    [TestFixture]
    public class TalearrDuplicateRecordingsFixture
    {
        private RecordingsWorld _world;
        private List<QueueItem> _queue;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _queue = new List<QueueItem>();
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private TalearrRecordingMaintenance Subject()
        {
            var (queue, queueFake) = InterfaceFake<IQueueService>.Create();
            queueFake.On(nameof(IQueueService.GetQueue), _ => _queue);

            return new TalearrRecordingMaintenance(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Groups, _world.Facts,
                LogManager.GetCurrentClassLogger(), queueService: queue);
        }

        // The primary row, pinned to Scott Brick (US) the way a recording request pins it.
        private Book PinnedPrimary(bool withFiles)
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles);
            _world.Editions.Single(e => e.Id == 28701).ManualAdd = true;
            book.AnyEditionOk = false;
            return book;
        }

        // An import copy row the way ImportApprovedBooks.CreateNewBookInstanceForBatch makes one:
        // a "_copy_" slug, the work's provider ids and one clone of the matched edition.
        private Book ImportCopy(Edition matched, bool withFile, int id = 9500)
        {
            var copy = new Book
            {
                Id = id,
                AuthorId = 7,
                Title = "The Firm",
                TitleSlug = $"the-firm_copy_{id}",
                HardcoverBookId = "hc:378630",
                MediaType = BookMediaType.Audiobook,
                AudiobookMonitored = true,
                AnyEditionOk = true,
                UnitKeyHash = "abc" + id,
                AddOptions = new AddBookOptions()
            };
            _world.Books[copy.Id] = copy;

            var edition = new Edition
            {
                Id = id * 10,
                BookId = copy.Id,
                ForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(matched),
                HardcoverEditionId = matched.HardcoverEditionId,
                Title = matched.Title,
                Language = matched.Language,
                Publisher = matched.Publisher,
                NarratorNames = matched.NarratorNames.ToList(),
                DurationSeconds = matched.DurationSeconds,
                AudibleASIN = matched.AudibleASIN,
                ReadingFormatId = 2,
                Monitored = true
            };
            _world.Editions.Add(edition);

            if (withFile)
            {
                _world.Files.Add(new BookFile { Id = id, EditionId = edition.Id, Path = $"/audiobooks/John Grisham/The Firm ({id})/The Firm.m4b", Size = 300_000_000, DurationSeconds = 17 * 3600 });
            }

            return copy;
        }

        private List<Book> AudiobookRows => _world.Books.Values.Where(b => b.MediaType == BookMediaType.Audiobook).ToList();

        [Test]
        public void a_missing_primary_takes_the_files_of_a_copy_of_the_same_edition()
        {
            PinnedPrimary(withFiles: false);
            var copy = ImportCopy(FirmEditions.ScottBrickUs(), withFile: true);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            var view = _world.Recordings.GetRecordings(3401);
            Assert.Multiple(() =>
            {
                Assert.That(removed, Is.EqualTo(new[] { copy.Id }));
                Assert.That(_world.Deleted, Is.EqualTo(new[] { copy.Id }));
                Assert.That(_world.Files.Single().EditionId, Is.EqualTo(28701), "the file now belongs to the primary row's pinned edition");
                Assert.That(view.Recordings.Count, Is.EqualTo(1));
                Assert.That(view.Recordings[0].Role, Is.EqualTo("primary"));
                Assert.That(view.Recordings[0].HasFiles, Is.True);
            });
        }

        [Test]
        public void another_edition_of_the_same_audible_product_is_merged_too()
        {
            PinnedPrimary(withFiles: false);
            var other = FirmEditions.ScottBrickUk();
            other.AudibleASIN = FirmEditions.ScottBrickUs().AudibleASIN;
            var copy = ImportCopy(other, withFile: true);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.EqualTo(new[] { copy.Id }));
            Assert.That(_world.Files.Single().EditionId, Is.EqualTo(28701));
        }

        [Test]
        public void editions_linked_to_the_same_audible_product_are_the_same_recording()
        {
            PinnedPrimary(withFiles: false);
            var copy = ImportCopy(FirmEditions.ScottBrickUk(), withFile: true);
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B07S39LY5V", "unabridged", 1030, "Scott Brick");
            _world.LinkAudible(FirmEditions.ScottBrickUk(), "B07S39LY5V", "unabridged", 1030, "Scott Brick");

            var removed = Subject().MergeDuplicates(copy);

            Assert.That(removed, Is.EqualTo(new[] { copy.Id }));
        }

        [Test]
        public void different_recordings_are_never_merged()
        {
            PinnedPrimary(withFiles: false);
            ImportCopy(FirmEditions.MoffettAbridged(), withFile: true);
            ImportCopy(FirmEditions.ScottBrickUk(), withFile: false, id: 9600);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.Empty);
            Assert.That(_world.Deleted, Is.Empty);
            Assert.That(AudiobookRows.Count, Is.EqualTo(3));
        }

        [Test]
        public void two_rows_with_files_are_left_for_the_user()
        {
            PinnedPrimary(withFiles: true);
            ImportCopy(FirmEditions.ScottBrickUs(), withFile: true);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.Empty);
            Assert.That(_world.Files.Select(f => f.EditionId), Is.EquivalentTo(new[] { 28701, 95000 }));
        }

        [Test]
        public void an_empty_copy_of_a_recording_that_has_files_is_removed()
        {
            PinnedPrimary(withFiles: true);
            var copy = ImportCopy(FirmEditions.ScottBrickUs(), withFile: false);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.EqualTo(new[] { copy.Id }));
            Assert.That(_world.Books.ContainsKey(3401), Is.True);
        }

        [Test]
        public void of_two_empty_additional_copies_the_older_one_stays()
        {
            PinnedPrimary(withFiles: true);
            _world.Editions.Single(e => e.Id == 28701).Monitored = false;
            _world.Editions.Single(e => e.Id == 28701).ManualAdd = false;
            _world.Editions.Single(e => e.Id == 28705).Monitored = true;
            _world.Files.Single().EditionId = 28705;
            var older = ImportCopy(FirmEditions.ScottBrickUs(), withFile: false, id: 9500);
            var newer = ImportCopy(FirmEditions.ScottBrickUs(), withFile: false, id: 9600);

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.EqualTo(new[] { newer.Id }));
            Assert.That(_world.Books.ContainsKey(older.Id), Is.True);
        }

        [Test]
        public void a_row_with_a_download_in_the_queue_is_kept()
        {
            PinnedPrimary(withFiles: false);
            var copy = ImportCopy(FirmEditions.ScottBrickUs(), withFile: true);
            _queue.Add(new QueueItem { Book = copy });

            var removed = Subject().MergeDuplicates(_world.Books[3401]);

            Assert.That(removed, Is.Empty);
            Assert.That(_world.Files.Single().EditionId, Is.EqualTo(95000));
        }

        [Test]
        public void merging_twice_changes_nothing_more()
        {
            PinnedPrimary(withFiles: false);
            ImportCopy(FirmEditions.ScottBrickUs(), withFile: true);
            var subject = Subject();

            subject.MergeDuplicates(_world.Books[3401]);
            var again = subject.MergeDuplicates(_world.Books[3401]);

            Assert.That(again, Is.Empty);
            Assert.That(_world.Deleted.Count, Is.EqualTo(1));
        }

        [Test]
        public void make_primary_swaps_the_recordings_and_keeps_the_book_row()
        {
            PinnedPrimary(withFiles: false);
            var copy = ImportCopy(FirmEditions.ScottBrickUk(), withFile: true);

            var outcome = Subject().MakePrimary(copy.Id);

            var primaryEditions = _world.Editions.Where(e => e.BookId == 3401).ToList();
            var copyEditions = _world.Editions.Where(e => e.BookId == copy.Id).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(outcome.Status, Is.EqualTo(MakePrimaryStatus.Done));
                Assert.That(outcome.PrimaryBookId, Is.EqualTo(3401));
                Assert.That(primaryEditions.Single(e => e.Monitored).Id, Is.EqualTo(28702), "the book now monitors the promoted recording");
                Assert.That(primaryEditions.Single(e => e.Monitored).ManualAdd, Is.True);
                Assert.That(_world.Files.Single().EditionId, Is.EqualTo(28702), "the promoted files moved to the book");
                Assert.That(TalearrEditionKey.For(copyEditions.Single(e => e.Monitored)), Is.EqualTo("hc:edition:30583648"), "the old primary recording moved to the other row");
                Assert.That(_world.Books[copy.Id].UnitKeyHash, Is.Null);
                Assert.That(_world.Books[3401].TitleSlug, Is.EqualTo("the-firm_2"), "the book row is still the primary row");
            });

            var view = _world.Recordings.GetRecordings(3401);
            Assert.That(view.Recordings.Single(r => r.Role == "primary").HasFiles, Is.True);
            Assert.That(view.Recordings.Single(r => r.Role == "additional").HasFiles, Is.False);
            Assert.That(_world.Recordings.RemoveRecording(copy.Id, false).Status, Is.EqualTo(RemoveRecordingStatus.Removed), "the old primary recording can now be removed");
        }

        [Test]
        public void make_primary_moves_files_both_ways()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            var added = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "edition", EditionId = 28705, Search = false });
            var copyId = added.Recording.BookId;
            var copyEdition = _world.Editions.Single(e => e.BookId == copyId && e.Monitored);
            _world.Files.Add(new BookFile { Id = 2, EditionId = copyEdition.Id, Path = "/audiobooks/John Grisham/The Firm (D.W. Moffett)/The Firm.m4b", Size = 90_000_000 });

            var outcome = Subject().MakePrimary(copyId);

            Assert.Multiple(() =>
            {
                Assert.That(added.Status, Is.EqualTo(AddRecordingStatus.Created));
                Assert.That(outcome.Status, Is.EqualTo(MakePrimaryStatus.Done));
                Assert.That(_world.Files.Single(f => f.Id == 2).EditionId, Is.EqualTo(28705));
                Assert.That(_world.Editions.Single(e => e.Id == _world.Files.Single(f => f.Id == 1).EditionId).BookId, Is.EqualTo(copyId));
                Assert.That(_world.Editions.Single(e => e.BookId == copyId && e.Monitored).HardcoverEditionId, Is.EqualTo("30583648"));
                Assert.That(_world.Books[3401].AnyEditionOk, Is.False);
            });
        }

        [Test]
        public void the_primary_recording_cannot_be_made_primary_again()
        {
            PinnedPrimary(withFiles: true);

            Assert.That(Subject().MakePrimary(3401).Status, Is.EqualTo(MakePrimaryStatus.Refused));
            Assert.That(Subject().MakePrimary(3402).Status, Is.EqualTo(MakePrimaryStatus.NotFound), "ebook rows are not recordings");
        }

        [Test]
        public void asking_for_an_edition_of_a_recording_the_book_already_has_adds_no_row()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B07S39LY5V", "unabridged", 1030, "Scott Brick");
            _world.LinkAudible(FirmEditions.ScottBrickUk(), "B07S39LY5V", "unabridged", 1030, "Scott Brick");

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "edition", EditionId = 28702 });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Existing));
            Assert.That(outcome.Recording.BookId, Is.EqualTo(3401));
            Assert.That(AudiobookRows.Count, Is.EqualTo(1));
        }
    }
}
