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
        public void media_types_and_other_titles_never_match()
        {
            var locals = new List<Book> { Local(1, "Herald of Justice", "gr:111", mediaType: BookMediaType.Ebook), Local(2, "Fist of Orion", "gr:222") };
            var remotes = new List<Book> { Remote("Herald of Justice", "gr:268656700"), Remote("Fist of Orion: Book 2", "gr:333") };

            Assert.That(TalearrStaleIdentityReconciler.Attach(locals, remotes), Is.Zero);
            Assert.That(remotes.All(r => r.RemoteProviderIds == null || r.RemoteProviderIds.Count == 0), Is.True);
        }
    }
}
