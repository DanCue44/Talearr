using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Audiobookshelf;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrAbsMatcherFixture
    {
        private AbsWorld _world;
        private Book _firmAudio;
        private Book _firmCopy;
        private Book _firmEbook;

        [SetUp]
        public void SetUp()
        {
            _world = new AbsWorld();

            _firmAudio = _world.AddBook(3401, 7, "The Firm", BookMediaType.Audiobook, "hc:378630");
            _world.AddEdition(_firmAudio, 1, true, asin: "B002V5BSLC", durationSeconds: 61800, narrator: "Scott Brick");
            _firmCopy = _world.AddBook(3403, 7, "The Firm", BookMediaType.Audiobook, "hc:378630", slug: "the-firm_wanted_2");
            _world.AddEdition(_firmCopy, 2, true, durationSeconds: 21600, narrator: "D.W. Moffett", title: "The Firm (Abridged)");
            _firmEbook = _world.AddBook(3402, 7, "The Firm", BookMediaType.Ebook, "hc:378630");
            _world.AddEdition(_firmEbook, 3, true, isbn10: "0307576140");

            var exchange = _world.AddBook(3410, 7, "The Exchange: After the Firm", BookMediaType.Audiobook, "hc:999001");
            _world.AddEdition(exchange, 10, true);

            var dune = _world.AddBook(3500, 10, "Dune", BookMediaType.Audiobook, "hc:dune");
            _world.AddEdition(dune, 20, true);

            var poppy = _world.AddBook(3600, 9, "The Poppy War", BookMediaType.Ebook, "hc:poppy");
            _world.AddEdition(poppy, 30, true, isbn13: "9780062662569");
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        [Test]
        public void should_match_by_asin_and_widen_to_every_recording_of_the_work()
        {
            var item = AbsItems.Audio("li-1", "Some Other Title", "Nobody", asin: "b002v5bslc");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Select(m => m.BookId), Is.EquivalentTo(new[] { 3401, 3403 }));
            Assert.That(matches.All(m => m.Method == TalearrAbsMatchMethods.Asin && !m.Managed), Is.True);
        }

        [Test]
        public void should_match_a_hyphenated_isbn_13_to_an_edition_stored_as_isbn_10_and_the_ebook_rows_only()
        {
            var item = AbsItems.Ebook("li-2", "Firm", "J. Grisham", isbn: TalearrAbsItemReader.NormalizeIsbn("978-0-307-57614-9"));

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Select(m => m.BookId), Is.EquivalentTo(new[] { 3402 }));
            Assert.That(matches[0].Method, Is.EqualTo(TalearrAbsMatchMethods.Isbn));
        }

        [Test]
        public void should_match_by_title_and_author_when_there_is_no_identifier()
        {
            var item = AbsItems.Audio("li-3", "The Firm", "John Grisham");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Select(m => m.BookId), Is.EquivalentTo(new[] { 3401, 3403 }));
            Assert.That(matches.All(m => m.Method == TalearrAbsMatchMethods.Title), Is.True);
        }

        [Test]
        public void should_treat_author_initials_and_spacing_as_the_same_author()
        {
            var item = AbsItems.Ebook("li-4", "The Poppy War", "R.F. Kuang");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Select(m => m.BookId), Is.EqualTo(new[] { 3600 }));
        }

        [Test]
        public void should_not_match_a_longer_title_to_a_shorter_book()
        {
            var item = AbsItems.Audio("li-5", "Dune Messiah", "Frank Herbert");

            Assert.That(TalearrAbsMatcher.Match(item, _world.Lookup), Is.Empty);
        }

        [Test]
        public void should_not_match_a_sibling_title_containing_the_book_title()
        {
            var item = AbsItems.Audio("li-6", "The Exchange: After the Firm", "John Grisham");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Select(m => m.BookId), Is.EqualTo(new[] { 3410 }));
        }

        [Test]
        public void should_not_match_by_title_when_the_author_is_unknown_to_taleArr()
        {
            var item = AbsItems.Audio("li-7", "The Firm", "Somebody Else");

            Assert.That(TalearrAbsMatcher.Match(item, _world.Lookup), Is.Empty);
        }

        [Test]
        public void should_mark_taleArr_own_files_as_managed_by_path()
        {
            var edition = _world.Editions.First(e => e.Id == 1);
            _world.AddFile(edition, "/audiobooks/John Grisham/The Firm/The Firm.m4b");
            var item = AbsItems.Audio("li-8", "The Firm", "John Grisham", localPath: "/audiobooks/John Grisham/The Firm");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Count, Is.EqualTo(1));
            Assert.That(matches[0].BookId, Is.EqualTo(3401));
            Assert.That(matches[0].Managed, Is.True);
            Assert.That(matches[0].Method, Is.EqualTo(TalearrAbsMatchMethods.Path));
        }

        [Test]
        public void should_not_count_a_sibling_folder_with_a_common_prefix_as_the_same_path()
        {
            var edition = _world.Editions.First(e => e.Id == 1);
            _world.AddFile(edition, "/audiobooks/John Grisham/The Firm 2/x.m4b");
            var item = AbsItems.Audio("li-9", "The Firm", "John Grisham", localPath: "/audiobooks/John Grisham/The Firm");

            var matches = TalearrAbsMatcher.Match(item, _world.Lookup);

            Assert.That(matches.Any(m => m.Managed), Is.False);
            Assert.That(matches.Select(m => m.BookId), Is.EquivalentTo(new[] { 3401, 3403 }));
        }
    }
}
