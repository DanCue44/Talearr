using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // Talearr: One Second After (William R. Forstchen) was requested by Allseerr as audiobook
    // gr:4987669, the Goodreads id its lookup returns. The author was already in the library
    // (Hardcover hc:219059) with the ebook hc:49431, but no Hardcover book carries that Goodreads
    // id, so the add parked the request and re-posting it parked it again.
    [TestFixture]
    public class TalearrForeignWorkMatchFixture
    {
        private const string Requested = "gr:4987669";

        [TestCase("gr:4987669", true)]
        [TestCase("ol:OL123W", true)]
        [TestCase("hc:49431", false)]
        [TestCase("az:B002V1BPOS", false)]
        [TestCase("4987669", false)]
        [TestCase(null, false)]
        public void only_goodreads_and_open_library_ids_are_foreign_to_the_hardcover_catalog(string providerId, bool foreign)
        {
            Assert.That(TalearrForeignWorkMatch.IsForeignWorkId(providerId), Is.EqualTo(foreign));
        }

        [Test]
        public void an_existing_author_request_by_goodreads_id_is_carried_onto_the_hardcover_book()
        {
            var catalog = HardcoverCatalog();

            // The metadata server lists the requested id as an edition of its own work id.
            var server = Book("One Second After", BookMediaType.Audiobook, null);
            server.GoodreadsWorkId = "gr:5052646";

            var bridged = TalearrForeignWorkMatch.Bridge(catalog, server, Requested, server.Title, BookMediaType.Audiobook, Library());

            Assert.That(bridged, Is.SameAs(catalog.Single(b => b.HardcoverBookId == "hc:49431" && b.MediaType == BookMediaType.Audiobook)));
            Assert.That(BookIdentity.GetProviderIdentityTokens(bridged), Does.Contain(Requested));
            Assert.That(BookIdentity.GetProviderIdentityTokens(bridged), Does.Contain("gr:5052646"));
            Assert.That(catalog, Has.Count.EqualTo(4), "no server copy is added next to Hardcover's book");
            Assert.That(catalog.Where(b => b.MediaType == BookMediaType.Ebook).All(b => b.RemoteProviderIds == null), Is.True);
        }

        [Test]
        public void a_request_by_the_library_author_uses_the_lookup_title_when_the_server_catalog_is_not_read()
        {
            var catalog = HardcoverCatalog();

            var bridged = TalearrForeignWorkMatch.Bridge(catalog, null, Requested, "One Second After", BookMediaType.Audiobook, Library());

            Assert.That(bridged?.HardcoverBookId, Is.EqualTo("hc:49431"));
            Assert.That(bridged.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(bridged.RemoteProviderIds, Is.EquivalentTo(new[] { Requested }));
        }

        [Test]
        public void several_same_title_books_resolve_to_the_one_the_library_holds()
        {
            var catalog = HardcoverCatalog();

            // Without the library hint the bare title and its subtitled copy both match.
            Assert.That(TalearrForeignWorkMatch.PickSameTitle(catalog, "One Second After", BookMediaType.Audiobook,
                new List<Book> { Book("One Second After", BookMediaType.Ebook, "hc:777") })?.HardcoverBookId, Is.EqualTo("hc:49431"),
                "falls back to the exact title");
            Assert.That(TalearrForeignWorkMatch.PickSameTitle(catalog, "One Second After", BookMediaType.Audiobook, Library())?.HardcoverBookId,
                Is.EqualTo("hc:49431"));
        }

        [Test]
        public void ambiguous_titles_are_not_guessed()
        {
            var catalog = new List<Book>
            {
                Book("One Second After: Book One", BookMediaType.Audiobook, "hc:1"),
                Book("One Second After: Collector's Edition", BookMediaType.Audiobook, "hc:2")
            };

            Assert.That(TalearrForeignWorkMatch.Bridge(catalog, null, Requested, "One Second After", BookMediaType.Audiobook, new List<Book>()), Is.Null);
            Assert.That(catalog.All(b => b.RemoteProviderIds == null), Is.True);
        }

        [Test]
        public void a_title_hardcover_does_not_list_adds_the_server_copy_as_before()
        {
            var catalog = HardcoverCatalog();
            var server = Book("Some Unlisted Story", BookMediaType.Audiobook, null);
            server.GoodreadsWorkId = "gr:42";

            var bridged = TalearrForeignWorkMatch.Bridge(catalog, server, "gr:42", server.Title, BookMediaType.Audiobook, Library());

            Assert.That(bridged, Is.SameAs(server));
            Assert.That(catalog, Does.Contain(server));
        }

        [Test]
        public void a_goodreads_request_finds_the_library_row_by_title()
        {
            var library = Library();
            library.Add(Book("One Second After", BookMediaType.Audiobook, "hc:49431"));

            var found = TalearrForeignWorkMatch.PickSameTitle(library, "One Second After", BookMediaType.Audiobook);

            Assert.That(found, Is.SameAs(library.Last()));
            Assert.That(TalearrForeignWorkMatch.PickSameTitle(Library(), "One Second After", BookMediaType.Audiobook), Is.Null,
                "the ebook row does not stand for an audiobook request");
        }

        // Hardcover's catalog for the author: both formats of One Second After, plus a subtitled
        // copy of the same title as Hardcover often lists.
        private static List<Book> HardcoverCatalog()
        {
            return new List<Book>
            {
                Book("One Second After", BookMediaType.Ebook, "hc:49431"),
                Book("One Second After", BookMediaType.Audiobook, "hc:49431"),
                Book("One Second After: A John Matherson Novel", BookMediaType.Audiobook, "hc:3088801"),
                Book("One Year After", BookMediaType.Audiobook, "hc:49432")
            };
        }

        private static List<Book> Library()
        {
            return new List<Book> { Book("One Second After", BookMediaType.Ebook, "hc:49431") };
        }

        private static Book Book(string title, BookMediaType mediaType, string hardcoverBookId)
        {
            return new Book { Title = title, MediaType = mediaType, HardcoverBookId = hardcoverBookId, AuthorId = 29 };
        }
    }
}
