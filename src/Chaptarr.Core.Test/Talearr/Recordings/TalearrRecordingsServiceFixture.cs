using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrRecordingsServiceFixture
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

        // Someone is listening to the abridged D.W. Moffett reading (downloaded) and wants the
        // unabridged one too.
        private void AbridgedPrimaryWithFiles()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
        }

        [Test]
        public void monitored_follows_audiobook_monitoring_not_the_legacy_flag()
        {
            AbridgedPrimaryWithFiles();
            _world.Books[3401].Monitored = false;

            Assert.That(_world.Recordings.GetRecordings(3401).Recordings.Single().Monitored, Is.True);

            _world.Books[3401].AudiobookMonitored = false;
            Assert.That(_world.Recordings.GetRecordings(3401).Recordings.Single().Monitored, Is.False);
        }

        [Test]
        public void add_unabridged_creates_an_upstream_copy_row_and_searches_it()
        {
            AbridgedPrimaryWithFiles();

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged", RequestedBy = "allseerr", ExternalRef = "req-812" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
            var copy = _world.Books[outcome.Recording.BookId];
            Assert.Multiple(() =>
            {
                Assert.That(copy.Id, Is.Not.EqualTo(3401));
                Assert.That(copy.TitleSlug, Is.EqualTo("the-firm_2_wanted_28701"));
                Assert.That(copy.BaseBookId, Is.EqualTo("hc:378630"));
                Assert.That(copy.AddOptions.AddType, Is.EqualTo(BookAddType.Manual));
                Assert.That(outcome.Recording.Role, Is.EqualTo("additional"));
                Assert.That(outcome.Recording.Abridged, Is.EqualTo("unabridged"));
                Assert.That(outcome.Recording.AbridgedSource, Is.EqualTo(AbridgedSources.Audible));
                Assert.That(outcome.Recording.Narrators, Is.EqualTo(new[] { "Scott Brick" }));
                Assert.That(outcome.Recording.Asin, Is.EqualTo("B002V5BSLC"));
                Assert.That(outcome.Recording.AudibleUrl, Is.EqualTo("https://www.audible.com/pd/B002V5BSLC"));
                Assert.That(outcome.SearchQueued, Is.True, "search on add is on by default");
                Assert.That(_world.SearchedBookIds, Is.EqualTo(new[] { copy.Id }));

                // The abridged recording is kept as it is.
                Assert.That(_world.Books[3401].TitleSlug, Is.EqualTo("the-firm_2"));
                Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28705));
                Assert.That(_world.Deleted, Is.Empty);

                var request = _world.Store.GetRequests("hc:378630").Single();
                Assert.That(request.RequestedBy, Is.EqualTo("allseerr"));
                Assert.That(request.ExternalRef, Is.EqualTo("req-812"));
                Assert.That(request.CopyBookIdHint, Is.EqualTo(copy.Id));
            });
        }

        [Test]
        public void asking_again_returns_the_existing_recording()
        {
            AbridgedPrimaryWithFiles();
            var first = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });
            var rows = _world.Books.Count;

            var again = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3402, Kind = "unabridged" });
            var byEdition = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "edition", EditionId = 28701 });
            var primaryAgain = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "abridged" });

            Assert.That(again.Status, Is.EqualTo(AddRecordingStatus.Existing));
            Assert.That(again.Recording.BookId, Is.EqualTo(first.Recording.BookId));
            Assert.That(byEdition.Status, Is.EqualTo(AddRecordingStatus.Existing));
            Assert.That(primaryAgain.Status, Is.EqualTo(AddRecordingStatus.Existing));
            Assert.That(primaryAgain.Recording.BookId, Is.EqualTo(3401));
            Assert.That(_world.Books.Count, Is.EqualTo(rows), "no duplicate rows");
            Assert.That(_world.SearchedBookIds, Has.Count.EqualTo(1), "existing recordings are not searched again");
        }

        [Test]
        public void upstream_dedupes_a_wanted_copy_it_already_made()
        {
            AbridgedPrimaryWithFiles();
            var first = _world.BookService.AddWantedEdition(3401, 28701);

            var second = _world.BookService.AddWantedEdition(3401, 28701);

            Assert.That(second.Id, Is.EqualTo(first.Id));
        }

        [Test]
        public void without_files_upstream_pins_the_edition_on_the_book_itself()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: false);
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Pinned));
            Assert.That(outcome.Recording.BookId, Is.EqualTo(3401));
            Assert.That(_world.Books.Values.Count(b => b.MediaType == BookMediaType.Audiobook), Is.EqualTo(1));
            Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28701));
            Assert.That(_world.Books[3401].AnyEditionOk, Is.False);
            Assert.That(_world.SearchedBookIds, Is.EqualTo(new[] { 3401 }));
        }

        [Test]
        public void get_recordings_from_the_ebook_row_lists_recordings_and_grouped_candidates()
        {
            AbridgedPrimaryWithFiles();

            var view = _world.Recordings.GetRecordings(3402);

            Assert.That(view.BaseBookId, Is.EqualTo("hc:378630"));
            Assert.That(view.Marketplace, Is.EqualTo("us"));
            Assert.That(view.ProviderIdsAll, Does.Contain("hc:378630"));

            var primary = view.Recordings.Single();
            Assert.Multiple(() =>
            {
                Assert.That(primary.BookId, Is.EqualTo(3401));
                Assert.That(primary.Role, Is.EqualTo("primary"));
                Assert.That(primary.Abridged, Is.EqualTo("abridged"));
                Assert.That(primary.HasFiles, Is.True);
                Assert.That(primary.Files.Count, Is.EqualTo(1));
                Assert.That(primary.Files.DurationMinutes, Is.EqualTo(360));
                Assert.That(primary.Path, Is.EqualTo("/audiobooks/John Grisham/The Firm - D.W. Moffett"));
                Assert.That(primary.CanRemove, Is.False);
            });

            // US and UK Scott Brick readings are the same recording, so they show once.
            var scottBrick = view.Candidates.Single(c => c.Narrators.Contains("Scott Brick"));
            Assert.That(scottBrick.EditionIds, Is.EquivalentTo(new[] { 28701, 28702 }));
            Assert.That(scottBrick.Abridged, Is.EqualTo("unabridged"));
            Assert.That(view.Candidates.SelectMany(c => c.EditionIds), Does.Not.Contain(28705), "the existing recording is not a candidate");
        }

        private Edition AddEdition(int id, string publisher, int minutes, params string[] narrators)
        {
            var edition = new Edition { Id = id, BookId = 3401, ForeignEditionId = $"gr:{id}-audiobook", Title = "The Firm", Language = "eng", Publisher = publisher, DurationSeconds = minutes * 60, ReadingFormatId = 2, NarratorNames = narrators.ToList() };
            _world.Editions.Add(edition);
            return edition;
        }

        private List<List<int>> CandidateEditionIds()
        {
            return _world.Recordings.GetRecordings(3401).Candidates.Select(c => c.EditionIds.OrderBy(id => id).ToList()).ToList();
        }

        [Test]
        public void another_listing_of_the_recording_you_have_is_not_a_candidate()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
            AddEdition(28760, "Books on Tape", 0, "Scott Brick");
            _world.LinkAudible(AddEdition(28761, "Random House Audio", 1031, "Scott Brick"), "B002V5BSLC", "unabridged", 1030, "Scott Brick");

            var ids = CandidateEditionIds().SelectMany(c => c).ToList();

            Assert.That(ids, Does.Not.Contain(28760), "same narrator, nothing tells it apart");
            Assert.That(ids, Does.Not.Contain(28761), "same Audible product");
            Assert.That(ids, Does.Not.Contain(28702), "the UK Scott Brick edition");
            Assert.That(ids, Does.Contain(28705), "the Moffett reading is still offered");
        }

        [Test]
        public void listings_with_missing_facts_fold_into_one_candidate()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            AddEdition(28762, "Random House Audio", 362, "D.W. Moffett");
            AddEdition(28763, "Random House Audio", 361);

            var moffett = CandidateEditionIds().Single(c => c.Contains(28705));

            Assert.That(moffett, Is.EquivalentTo(new[] { 28705, 28762, 28763 }));
        }

        [Test]
        public void a_shorter_cast_list_is_the_same_recording()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            AddEdition(28764, "Audible Originals", 207, "Reader One", "Reader Two", "Reader Three");
            AddEdition(28765, "Audible Originals", 207, "Reader Two", "Reader One");

            Assert.That(CandidateEditionIds(), Has.One.EquivalentTo(new[] { 28764, 28765 }));
        }

        [Test]
        public void different_runtimes_or_publishers_stay_apart()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);
            AddEdition(28766, "Random House Audio", 360, "D.W. Moffett");
            AddEdition(28767, "Random House Audio", 500, "D.W. Moffett");
            AddEdition(28768, "Caedmon", 360);

            var candidates = CandidateEditionIds();

            Assert.That(candidates, Has.None.Contains(28766).And.Contains(28767));
            Assert.That(candidates, Has.One.EquivalentTo(new[] { 28768 }), "no narrators and another publisher");
        }

        [Test]
        public void narrator_kind_picks_that_narrators_edition()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "narrator", Narrator = "DW Moffett", Search = false });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
            Assert.That(outcome.Recording.EditionId, Is.Not.EqualTo(28701));
            Assert.That(outcome.Recording.Narrators, Is.EqualTo(new[] { "D.W. Moffett" }));
            Assert.That(outcome.SearchQueued, Is.False);
            Assert.That(_world.SearchedBookIds, Is.Empty);
        }

        [Test]
        public void search_on_add_follows_the_setting_when_the_request_does_not_say()
        {
            AbridgedPrimaryWithFiles();
            _world.Settings.Save(new TalearrRecordingsSettings { SearchOnAdd = false });

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Created));
            Assert.That(_world.SearchedBookIds, Is.Empty);
        }

        [Test]
        public void no_fitting_edition_is_a_not_found_with_the_candidates()
        {
            _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: true);

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.NoMatch), "nothing says any edition is unabridged without Audible facts");
            Assert.That(outcome.Candidates, Is.Not.Empty);
        }

        [Test]
        public void equal_candidates_are_ambiguous()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            foreach (var (id, narrator) in new[] { (28801, "Reader One"), (28802, "Reader Two") })
            {
                _world.Editions.Add(new Edition { Id = id, BookId = 3401, ForeignEditionId = $"gr:{id}-audiobook", Title = "The Firm (Unabridged)", Language = "eng", ReadingFormatId = 2, NarratorNames = new List<string> { narrator } });
            }

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.Ambiguous));
            Assert.That(outcome.Candidates.SelectMany(c => c.EditionIds), Is.EquivalentTo(new[] { 28801, 28802 }));
            Assert.That(_world.Books.Values.Count(b => b.MediaType == BookMediaType.Audiobook), Is.EqualTo(1), "nothing is created");
        }

        [Test]
        public void an_edition_from_elsewhere_is_not_found()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);

            var outcome = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "edition", EditionId = 1 });

            Assert.That(outcome.Status, Is.EqualTo(AddRecordingStatus.NoMatch));
        }

        [Test]
        public void ebook_only_works_and_bad_input_are_rejected()
        {
            _world.Books[50] = new Book { Id = 50, AuthorId = 8, Title = "Only Ebook", BaseBookId = "hc:1", HardcoverBookId = "hc:1", MediaType = BookMediaType.Ebook, AddOptions = new AddBookOptions() };

            Assert.That(_world.Recordings.AddRecording(new AddRecordingRequest { BookId = 50, Kind = "unabridged" }).Status, Is.EqualTo(AddRecordingStatus.Invalid));
            Assert.That(_world.Recordings.AddRecording(new AddRecordingRequest { BookId = 50, Kind = "everything" }).Status, Is.EqualTo(AddRecordingStatus.Invalid));
            Assert.That(_world.Recordings.AddRecording(new AddRecordingRequest { BookId = 50, Kind = "narrator" }).Status, Is.EqualTo(AddRecordingStatus.Invalid));
            Assert.That(_world.Recordings.AddRecording(new AddRecordingRequest { BookId = 50, Kind = "edition" }).Status, Is.EqualTo(AddRecordingStatus.Invalid));
            Assert.That(_world.Recordings.GetRecordings(50), Is.Null);
            Assert.That(_world.Recordings.AddRecording(new AddRecordingRequest { BookId = 404, Kind = "abridged" }).Status, Is.EqualTo(AddRecordingStatus.NoMatch));
        }

        [Test]
        public void remove_is_manual_and_only_for_additional_recordings()
        {
            AbridgedPrimaryWithFiles();
            var copy = _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" }).Recording;

            var refused = _world.Recordings.RemoveRecording(3401, deleteFiles: true);
            var removed = _world.Recordings.RemoveRecording(copy.BookId, deleteFiles: false);

            Assert.That(refused.Status, Is.EqualTo(RemoveRecordingStatus.Refused));
            Assert.That(removed.Status, Is.EqualTo(RemoveRecordingStatus.Removed));
            Assert.That(_world.Deleted, Is.EqualTo(new[] { copy.BookId }));
            Assert.That(_world.Books.ContainsKey(3401), Is.True);
            Assert.That(_world.Recordings.RemoveRecording(3402, false).Status, Is.EqualTo(RemoveRecordingStatus.NotFound), "ebook rows are not recordings");
        }

        [Test]
        public void keeping_both_is_the_default_and_nothing_removes_a_recording_on_its_own()
        {
            AbridgedPrimaryWithFiles();
            _world.Recordings.AddRecording(new AddRecordingRequest { BookId = 3401, Kind = "unabridged" });

            Assert.That(_world.Settings.Get().KeepBoth, Is.True);
            Assert.That(_world.Recordings.GetRecordings(3401).Recordings.Select(r => r.Abridged), Is.EquivalentTo(new[] { "abridged", "unabridged" }));
            Assert.That(_world.Deleted, Is.Empty);
        }
    }
}
