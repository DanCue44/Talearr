using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrInAudiobookshelfSpecificationFixture
    {
        private const int Unabridged = 61800;
        private const int AbridgedLength = 21600;

        private AbsWorld _world;
        private Book _firm;
        private Book _firmEbook;
        private TalearrInAudiobookshelfSpecification _spec;

        [SetUp]
        public void SetUp()
        {
            _world = new AbsWorld();
            _world.AddConnection();

            _firm = _world.AddBook(3401, 7, "The Firm", BookMediaType.Audiobook, "hc:378630");
            _world.AddEdition(_firm, 1, true, durationSeconds: Unabridged, narrator: "Scott Brick");
            _firmEbook = _world.AddBook(3402, 7, "The Firm", BookMediaType.Ebook, "hc:378630");
            _world.AddEdition(_firmEbook, 3, true, isbn13: "9780307576149");

            _spec = new TalearrInAudiobookshelfSpecification(LogManager.GetCurrentClassLogger(), _world.Library);
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private static RemoteBook Release(string title, params Book[] books)
        {
            var isEbook = books.All(b => b.MediaType == BookMediaType.Ebook);
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(isEbook ? Quality.EPUB : Quality.M4B) },
                Books = books.ToList()
            };
        }

        [Test]
        public void should_reject_an_audiobook_release_when_an_unabridged_copy_is_in_audiobookshelf()
        {
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham", narrator: "Scott Brick", duration: Unabridged - 600));

            var decision = _spec.IsSatisfiedBy(Release("John Grisham - The Firm [M4B]", _firm), null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.CanBypass, Is.True, "a soft filter, so interactive search can still grab it");
            Assert.That(decision.Category, Is.EqualTo(TalearrInAudiobookshelfSpecification.Category));
            Assert.That(decision.Reason, Does.Contain("Already in Audiobookshelf").And.Contain("The Firm").And.Contain("Scott Brick"));
        }

        [Test]
        public void should_label_the_rejection_in_audiobookshelf()
        {
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));

            var decision = _spec.IsSatisfiedBy(Release("The Firm", _firm), null);
            var rejection = new Rejection(decision.Reason, _spec.Type, decision.CanBypass, decision.Category, decision.Severity)
            {
                Source = nameof(TalearrInAudiobookshelfSpecification)
            };

            Assert.That(TalearrRejectionLabels.GetLabel(rejection), Is.EqualTo("In Audiobookshelf"));
        }

        [Test]
        public void should_not_let_an_abridged_copy_block_a_wanted_unabridged_recording()
        {
            _world.Cache(1, AbsItems.Audio("li-firm-ab", "The Firm", "John Grisham", narrator: "D.W. Moffett", duration: AbridgedLength));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm Unabridged", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_not_let_a_copy_marked_abridged_block_even_without_a_runtime()
        {
            _world.Cache(1, AbsItems.Audio("li-firm-ab", "The Firm", "John Grisham", abridged: AbridgedMarker.Abridged));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_let_an_abridged_copy_satisfy_a_wanted_abridged_recording()
        {
            _world.Resolver.Markers[_firm.Id] = AbridgedMarker.Abridged;
            _world.Cache(1, AbsItems.Audio("li-firm-ab", "The Firm", "John Grisham", abridged: AbridgedMarker.Abridged));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm Abridged", _firm), null).Accepted, Is.False);
        }

        [Test]
        public void should_not_let_an_unabridged_copy_satisfy_a_wanted_abridged_recording()
        {
            _world.Resolver.Markers[_firm.Id] = AbridgedMarker.Abridged;
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham", abridged: AbridgedMarker.Unabridged));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm Abridged", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_ignore_abridgement_when_the_grab_policy_is_any()
        {
            _world.GrabPolicy.Policy = new TalearrGrabPolicy { AbridgedPolicy = AbridgedAudiobookPolicy.Any };
            _world.Cache(1, AbsItems.Audio("li-firm-ab", "The Firm", "John Grisham", abridged: AbridgedMarker.Abridged));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.False);
        }

        [Test]
        public void should_give_a_copy_to_the_recording_with_the_same_narrator()
        {
            var copy = _world.AddBook(3403, 7, "The Firm", BookMediaType.Audiobook, "hc:378630", slug: "the-firm_wanted_2");
            _world.AddEdition(copy, 2, true, durationSeconds: Unabridged + 300, narrator: "D.W. Moffett");

            // An unabridged Moffett reading is in Audiobookshelf: it is the copy row's recording,
            // not the Scott Brick one the primary row wants.
            _world.Cache(1, AbsItems.Audio("li-moffett", "The Firm", "John Grisham", narrator: "D.W. Moffett", duration: Unabridged));

            Assert.Multiple(() =>
            {
                Assert.That(_spec.IsSatisfiedBy(Release("The Firm", copy), null).Accepted, Is.False, "copy row is satisfied");
                Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True, "primary row still wanted");
            });
        }

        [Test]
        public void should_give_an_unknown_narrator_copy_to_the_primary_recording()
        {
            var copy = _world.AddBook(3403, 7, "The Firm", BookMediaType.Audiobook, "hc:378630", slug: "the-firm_wanted_2");
            _world.AddEdition(copy, 2, true, durationSeconds: Unabridged + 300, narrator: "D.W. Moffett");

            _world.Cache(1, AbsItems.Audio("li-anon", "The Firm", "John Grisham", duration: Unabridged));

            Assert.Multiple(() =>
            {
                Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.False);
                Assert.That(_spec.IsSatisfiedBy(Release("The Firm", copy), null).Accepted, Is.True);
            });
        }

        [Test]
        public void should_let_any_narrator_satisfy_a_book_with_one_recording()
        {
            _world.Cache(1, AbsItems.Audio("li-other", "The Firm", "John Grisham", narrator: "Someone Else", duration: Unabridged));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.False);
        }

        [Test]
        public void should_reject_an_ebook_release_when_the_ebook_is_in_audiobookshelf()
        {
            _world.Cache(1, AbsItems.Ebook("li-firm-epub", "The Firm", "John Grisham", isbn: "9780307576149"));

            Assert.Multiple(() =>
            {
                Assert.That(_spec.IsSatisfiedBy(Release("John Grisham - The Firm (epub)", _firmEbook), null).Accepted, Is.False);
                Assert.That(_spec.IsSatisfiedBy(Release("John Grisham - The Firm [M4B]", _firm), null).Accepted, Is.True, "an ebook does not stand in for the audiobook");
            });
        }

        [Test]
        public void should_accept_when_skip_owned_is_off()
        {
            _world.Connections.List.Clear();
            _world.AddConnection(skipOwned: false);
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_accept_when_sync_is_off_even_with_rows_left_in_the_cache()
        {
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));
            _world.Connections.List.Clear();
            _world.AddConnection(sync: false);
            _world.Library.Invalidate();

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_accept_when_taleArr_has_files_for_the_book_so_upgrades_still_work()
        {
            _world.AddFile(_world.Editions.First(e => e.Id == 1), "/audiobooks/John Grisham/The Firm/The Firm.mp3");
            _world.Cache(1, AbsItems.Audio("li-firm-elsewhere", "The Firm", "John Grisham", localPath: "/audiobooks/Hand Added/The Firm"));

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
        }

        [Test]
        public void should_never_count_taleArr_own_import()
        {
            var file = _world.AddFile(_world.Editions.First(e => e.Id == 1), "/audiobooks/John Grisham/The Firm/The Firm.m4b");
            _world.Cache(1, AbsItems.Audio("li-own", "The Firm", "John Grisham", localPath: "/audiobooks/John Grisham/The Firm"));

            // The file goes away (say, deleted for a redownload): the Audiobookshelf item is still
            // Talearr's own folder, so it never blocks the redownload.
            _world.Files.Remove(file);
            _world.Library.Invalidate();

            Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
            Assert.That(_world.Library.GetItems(_firm.Id).Single().Match.Managed, Is.True);
        }

        [Test]
        public void should_accept_a_pack_unless_every_book_is_in_audiobookshelf()
        {
            var other = _world.AddBook(3500, 7, "The Pelican Brief", BookMediaType.Audiobook, "hc:pelican");
            _world.AddEdition(other, 40, true);
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));

            Assert.That(_spec.IsSatisfiedBy(Release("Grisham Collection", _firm, other), null).Accepted, Is.True);
        }

        [Test]
        public void should_accept_when_nothing_is_cached_or_no_service()
        {
            Assert.Multiple(() =>
            {
                Assert.That(_spec.IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
                Assert.That(new TalearrInAudiobookshelfSpecification(LogManager.GetCurrentClassLogger()).IsSatisfiedBy(Release("The Firm", _firm), null).Accepted, Is.True);
            });
        }

        [Test]
        public void should_build_the_item_link_from_the_link_url_setting()
        {
            _world.Connections.List[0].Settings.TalearrLinkUrl = "https://abs.example.com/audiobookshelf/";
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));

            Assert.That(_world.Library.GetItems(_firm.Id).Single().Url, Is.EqualTo("https://abs.example.com/audiobookshelf/item/li-firm"));
        }

        [Test]
        public void should_fall_back_to_the_api_address_for_links_and_refuse_unsafe_link_urls()
        {
            _world.Connections.List[0].Settings.TalearrLinkUrl = "javascript:alert(1)";
            _world.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham"));

            Assert.That(_world.Library.GetItems(_firm.Id).Single().Url, Is.EqualTo("http://audiobookshelf:13378/item/li-firm"));
        }
    }
}
