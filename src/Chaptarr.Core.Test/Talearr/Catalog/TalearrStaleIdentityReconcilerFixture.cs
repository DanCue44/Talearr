using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    [TestFixture]
    public class TalearrStaleIdentityReconcilerFixture
    {
        private static Book Local(int id, string title, string gr, string asin = null, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            return new Book { Id = id, Title = title, GoodreadsWorkId = gr, AudibleASIN = asin, MediaType = mediaType, Editions = new List<Edition>() };
        }

        private static Book Remote(string title, string gr, string asin = null, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            return new Book { Title = title, GoodreadsWorkId = gr, AudibleASIN = asin, MediaType = mediaType, Editions = new List<Edition>() };
        }

        private static bool Carries(Book remote, string token)
        {
            return remote.RemoteProviderIds?.Contains(token) == true;
        }

        [Test]
        public void a_row_with_an_old_id_is_attached_to_the_servers_copy_of_the_same_title()
        {
            var locals = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(1, "The First Peacemaker", "gr:1") };
            var herald = Remote("Herald of Justice", "gr:268656700");
            var remotes = new List<Book> { herald, Remote("The First Peacemaker", "gr:1") };

            var attached = TalearrStaleIdentityReconciler.Attach(locals, remotes);

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(Carries(herald, "gr:111"), Is.True, "the refresh now matches the old row to the server's copy");
        }

        [Test]
        public void sharing_only_edition_ids_still_counts_as_an_old_row()
        {
            // The refresh matches works by work id, so a shared ASIN alone does not reach the old row.
            var locals = new List<Book> { Local(9838, "Herald of Justice", "gr:111", "B0H623QSX6") };
            var herald = Remote("Herald of Justice", "gr:268656700", "B0H623QSX6");

            TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { herald });

            Assert.That(Carries(herald, "gr:111"), Is.True);
        }

        [Test]
        public void rows_the_server_still_lists_are_left_alone()
        {
            var locals = new List<Book> { Local(1, "Afterfall", "gr:1") };
            var remote = Remote("Afterfall", "gr:1");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { remote }), Is.Zero);
            Assert.That(remote.RemoteProviderIds, Is.Null.Or.Empty);
        }

        [Test]
        public void a_server_book_another_row_already_matches_is_not_taken()
        {
            // An old row next to a current one of the same title: that is a duplicate for the merge
            // tool, not something to fold in on a refresh.
            var locals = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(424, "Herald of Justice", "gr:268656700") };
            var herald = Remote("Herald of Justice", "gr:268656700");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { herald }), Is.Zero);
            Assert.That(Carries(herald, "gr:111"), Is.False);
        }

        private static Book Hardcover(int id, string title, string hc)
        {
            return new Book { Id = id, Title = title, HardcoverBookId = hc, MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
        }

        private static Book RemoteHardcover(string title, string hc)
        {
            return new Book { Title = title, HardcoverBookId = hc, MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
        }

        [Test]
        public void an_old_row_with_files_takes_the_server_book_from_an_empty_copy()
        {
            // Herald of Justice: the old row has the audiobook, the server's copy was added next to
            // it without files. The refresh keeps the row with the files and merges the empty one.
            var withFiles = Local(9838, "Herald of Justice", "gr:111");
            var empty = Local(424, "Herald of Justice", "gr:268656700");
            var herald = Remote("Herald of Justice", "gr:268656700");

            var attached = TalearrStaleIdentityReconciler.Attach(new List<Book> { withFiles, empty }, new List<Book> { herald }, isDisposable: b => b.Id == 424);

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(Carries(herald, "gr:111"), Is.True);
        }

        [Test]
        public void a_server_book_matched_by_a_row_worth_keeping_is_not_taken()
        {
            var locals = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(424, "Herald of Justice", "gr:268656700") };
            var herald = Remote("Herald of Justice", "gr:268656700");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { herald }, isDisposable: _ => false), Is.Zero);
            Assert.That(Carries(herald, "gr:111"), Is.False);
        }

        [Test]
        public void an_old_empty_row_does_not_take_a_matched_server_book()
        {
            // Nothing to keep: the old row is deleted by the refresh as before.
            var locals = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(424, "Herald of Justice", "gr:268656700") };
            var herald = Remote("Herald of Justice", "gr:268656700");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { herald }, isDisposable: _ => true), Is.Zero);
        }

        [Test]
        public void a_row_known_only_by_its_asin_joins_the_part_of_the_same_number()
        {
            // The Synchronicity War: Audible's "The Synchronicity War 1" holds the files, Hardcover
            // calls the book "The Synchronicity War Part 1".
            var audible = new Book { Id = 381, Title = "The Synchronicity War 1", AudibleASIN = "B00QVL6T7K", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var empty = Hardcover(376, "The Synchronicity War Part 1", "hc:479709");
            var part1 = RemoteHardcover("The Synchronicity War Part 1", "hc:479709");
            var part2 = RemoteHardcover("The Synchronicity War Part 2", "hc:913716");

            var attached = TalearrStaleIdentityReconciler.Attach(new List<Book> { audible, empty }, new List<Book> { part1, part2 }, isDisposable: b => b.Id == 376);

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(audible.RemoteProviderIds, Does.Contain("hc:479709"));
            Assert.That(audible.RemoteProviderIds, Does.Not.Contain("hc:913716"));
        }

        [Test]
        public void a_row_whose_asin_the_server_still_lists_is_left_alone()
        {
            var audible = new Book { Id = 381, Title = "The Synchronicity War 1", AudibleASIN = "B00QVL6T7K", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var remote = new Book { Title = "The Synchronicity War 1", AudibleASIN = "B00QVL6T7K", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };

            Assert.That(TalearrStaleIdentityReconciler.Attach(new List<Book> { audible }, new List<Book> { remote }, isDisposable: _ => false), Is.Zero);
            Assert.That(audible.RemoteProviderIds, Is.Null);
        }

        [TestCase("The Synchronicity War 2", "The Synchronicity War Part 2", true)]
        [TestCase("The Synchronicity War 2", "The Synchronicity War Book 2", true)]
        [TestCase("The Synchronicity War 2", "The Synchronicity War Volume 2", true)]
        [TestCase("The Synchronicity War 2", "The Synchronicity War Part 3", false)]
        [TestCase("Part 2", "Part Two", false)]
        [TestCase("The Synchronicity War 1", "The Synchronicity War Part 1: Scifi action and adventure", true)]
        [TestCase("Rumors of War: A Novel of the Empire", "Rumors of War: The Other Story", false)]
        [TestCase("Opening Moves", "Opening Moves: Symbiote Wars book 2", true)]
        [TestCase("Recon by Fire", "Recon by Fire: Symbiote Wars, Book 4", true)]
        [TestCase("Fist of Orion", "Fist of Orion: Fist of Orion, Book 2", false)]
        public void numbered_parts_compare_by_number(string local, string remote, bool same)
        {
            var row = Local(1, local, "gr:1");
            var server = Remote(remote, "gr:2");

            Assert.That(TalearrStaleIdentityReconciler.Attach(new List<Book> { row }, new List<Book> { server }), Is.EqualTo(same ? 1 : 0));
        }

        [Test]
        public void two_server_books_with_the_title_are_left_alone_unless_an_edition_id_picks_one()
        {
            var locals = new List<Book> { Local(5, "Assault", "gr:111", "B000000001") };
            var first = Remote("Assault", "gr:2");
            var second = Remote("Assault", "gr:3");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { first, second }), Is.Zero);

            var withAsin = Remote("Assault", "gr:3", "B000000001");
            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { Remote("Assault", "gr:2"), withAsin }), Is.EqualTo(1));
            Assert.That(Carries(withAsin, "gr:111"), Is.True);
        }

        [Test]
        public void old_copies_of_one_work_go_together_and_different_ids_are_left_alone()
        {
            var sameWork = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(15063, "Herald of Justice", "gr:111") };
            var herald = Remote("Herald of Justice", "gr:268656700");

            Assert.That(TalearrStaleIdentityReconciler.Attach(sameWork, new List<Book> { herald }), Is.EqualTo(1));
            Assert.That(Carries(herald, "gr:111"), Is.True);

            var differentIds = new List<Book> { Local(9838, "Herald of Justice", "gr:111"), Local(15063, "Herald of Justice", "gr:222") };
            var other = Remote("Herald of Justice", "gr:268656700");

            Assert.That(TalearrStaleIdentityReconciler.Attach(differentIds, new List<Book> { other }), Is.Zero);
        }

        [Test]
        public void old_copies_under_different_ids_merge_into_the_one_with_files()
        {
            // Symbiote Wars: an old Goodreads row, an Audiobookshelf import with the files, and the
            // server's empty copy. The refresh keeps the row with the files and merges the others.
            var old = Local(10034, "Symbiote Wars", "gr:210377490");
            var withFiles = Local(15160, "Symbiote Wars", "gr:222");
            var empty = Hardcover(350, "Symbiote Wars", "hc:1846586");
            var server = RemoteHardcover("Symbiote Wars", "hc:1846586");

            var attached = TalearrStaleIdentityReconciler.Attach(new List<Book> { old, withFiles, empty }, new List<Book> { server }, isDisposable: b => b.Id != 15160);

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(Carries(server, "gr:210377490") && Carries(server, "gr:222"), Is.True);
        }

        [Test]
        public void old_copies_known_by_book_ids_merge_into_the_one_with_files()
        {
            // The same, with the Goodreads book ids Chaptarr stored instead of work ids
            Book Row(int id, string gr) => new Book { Id = id, Title = "Opening Moves", BaseBookId = "gr:" + gr, GoodreadsBookId = gr, MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var old = Row(10035, "211566581");
            var withFiles = Row(15161, "333");
            var empty = Hardcover(516, "Opening Moves: Symbiote Wars book 2", "hc:1846574");
            var server = RemoteHardcover("Opening Moves: Symbiote Wars book 2", "hc:1846574");

            var attached = TalearrStaleIdentityReconciler.Attach(new List<Book> { old, withFiles, empty }, new List<Book> { server }, isDisposable: b => b.Id != 15161);

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(old.RemoteProviderIds, Does.Contain("hc:1846574"));
            Assert.That(withFiles.RemoteProviderIds, Does.Contain("hc:1846574"));
        }

        [Test]
        public void old_copies_under_different_ids_both_worth_keeping_are_left_alone()
        {
            var locals = new List<Book> { Local(10034, "Symbiote Wars", "gr:210377490"), Local(15160, "Symbiote Wars", "gr:222") };
            var server = RemoteHardcover("Symbiote Wars", "hc:1846586");

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, new List<Book> { server }, isDisposable: _ => false), Is.Zero);
        }

        [Test]
        public void an_old_row_is_attached_to_an_audiobook_only_audible_lists()
        {
            // Gaining the Edge: Hardcover does not list it under J.N. Chaney; with Audible leading
            // it comes back as az:{ASIN}, and the old Goodreads row becomes that book.
            var old = Local(604, "Gaining the Edge", "gr:215410099");
            var audible = new Book { Title = "Gaining the Edge", ForeignEditionId = "az:B0D4FKJX6M", BaseBookId = "az:B0D4FKJX6M", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };

            Assert.That(TalearrStaleIdentityReconciler.Attach(new List<Book> { old }, new List<Book> { audible }), Is.EqualTo(1));
            Assert.That(Carries(audible, "gr:215410099"), Is.True);
        }

        [Test]
        public void a_row_known_by_a_book_id_takes_the_audible_id()
        {
            // The same with the Goodreads book id Chaptarr stored (no work id): both share that old
            // id for the refresh, which then matches them by work id.
            var old = new Book { Id = 604, Title = "Gaining the Edge", BaseBookId = "gr:215410099", GoodreadsBookId = "gr:215410099", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            var audible = new Book { Title = "Gaining the Edge", ForeignEditionId = "az:B0D4FKJX6M", BaseBookId = "az:B0D4FKJX6M", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };

            Assert.That(TalearrStaleIdentityReconciler.Attach(new List<Book> { old }, new List<Book> { audible }), Is.EqualTo(1));
            Assert.That(BookIdentity.FindWorkFirstMatchResult(new[] { old }, audible).Matches, Is.EqualTo(new[] { old }));
        }

        [Test]
        public void media_types_and_other_titles_never_match()
        {
            var locals = new List<Book> { Local(1, "Herald of Justice", "gr:111", mediaType: BookMediaType.Ebook), Local(2, "Fist of Orion", "gr:222") };
            var remotes = new List<Book> { Remote("Herald of Justice", "gr:268656700"), Remote("Fist of Orion: Book 2", "gr:333") };

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, remotes), Is.Zero);
            Assert.That(remotes.All(r => r.RemoteProviderIds == null || r.RemoteProviderIds.Count == 0), Is.True);
        }
    }
}
