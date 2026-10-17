using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using Chaptarr.Core.Test.Talearr.ReaderHandoff;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.EbookDelivery;

namespace Chaptarr.Core.Test.Talearr.EbookDelivery
{
    [TestFixture]
    public class TalearrOpdsFixture
    {
        private static readonly XNamespace A = TalearrOpdsFeedWriter.Atom;
        private static readonly DateTime Now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        private static BookFile EbookFile(int id, int bookId, string path, DateTime added, long size = 1000)
        {
            return new BookFile
            {
                Id = id,
                Path = path,
                Size = size,
                DateAdded = added,
                MediaType = "ebook",
                EditionId = 100 + bookId,
                Edition = new Edition { Id = 100 + bookId, BookId = bookId, Title = "edition title", Language = "eng", Publisher = "Tor" }
            };
        }

        private static TalearrOpdsSnapshot Library()
        {
            var sanderson = new Author { Id = 1, Name = "Brandon Sanderson", SortNameLastFirst = "Sanderson, Brandon" };
            var jemisin = new Author { Id = 2, Name = "N. K. Jemisin", SortNameLastFirst = "Jemisin, N. K." };

            var books = new List<Book>
            {
                new Book { Id = 10, AuthorId = 1, Title = "The Well of Ascension", MediaType = BookMediaType.Ebook, Overview = "<p>Book <b>two</b>.</p>" },
                new Book { Id = 11, AuthorId = 1, Title = "The Final Empire", MediaType = BookMediaType.Ebook },
                new Book { Id = 12, AuthorId = 2, Title = "The Fifth Season", MediaType = BookMediaType.Ebook, Genres = new List<string> { "Fantasy" } },
                new Book { Id = 13, AuthorId = 2, Title = "Pokémon Stories", MediaType = BookMediaType.Ebook },
                new Book { Id = 14, AuthorId = 2, Title = "No files", MediaType = BookMediaType.Ebook }
            };

            var files = new List<BookFile>
            {
                EbookFile(1, 10, "/books/Sanderson/Well.epub", Now.AddDays(-1)),
                EbookFile(2, 10, "/books/Sanderson/Well.azw3", Now.AddDays(-1)),
                EbookFile(3, 11, "/books/Sanderson/Final.epub", Now.AddDays(-10)),
                EbookFile(4, 12, "/books/Jemisin/Fifth.pdf", Now.AddDays(-3)),
                EbookFile(5, 13, "/books/Jemisin/Stories.epub", Now.AddDays(-30)),

                // An audio file and an unmapped file never show up.
                EbookFile(6, 12, "/books/Jemisin/Fifth.m4b", Now),
                new BookFile { Id = 7, Path = "/books/loose.epub", EditionId = 0 }
            };

            var series = new List<Series> { new Series { Id = 50, Title = "Mistborn" } };
            var links = new List<SeriesBookLink>
            {
                new SeriesBookLink { SeriesId = 50, BookId = 10, Position = "2" },
                new SeriesBookLink { SeriesId = 50, BookId = 11, Position = "1" },
                new SeriesBookLink { SeriesId = 99, BookId = 12, Position = "1" }
            };

            return TalearrOpdsLibrary.Build(books, new[] { sanderson, jemisin }, files, links, series, Now);
        }

        private static TalearrOpdsSettingsService SettingsService(Dictionary<string, string> rows, FakeEventAggregator events)
        {
            var (repository, fake) = Fake<IConfigRepository>.Create();
            fake.On(nameof(IConfigRepository.Get), args => rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    rows[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            return new TalearrOpdsSettingsService(repository, events, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void feed_is_off_by_default()
        {
            var settings = SettingsService(new Dictionary<string, string>(), new FakeEventAggregator()).Get();

            Assert.That(settings.Enabled, Is.False);
            Assert.That(settings.OfferKepub, Is.False);
            Assert.That(settings.PageSize, Is.EqualTo(50));
        }

        [Test]
        public void settings_are_talearr_config_rows_with_a_clamped_page_size()
        {
            var rows = new Dictionary<string, string>();
            var events = new FakeEventAggregator();
            var saved = SettingsService(rows, events).Save(new TalearrOpdsSettings { Enabled = true, PageSize = 5000, OfferKepub = true });

            Assert.That(saved.PageSize, Is.EqualTo(TalearrOpdsSettings.MaxPageSize));
            Assert.That(rows.Keys, Is.EquivalentTo(new[] { "talearr.opds.enabled", "talearr.opds.pagesize", "talearr.opds.kepub" }));
            Assert.That(events.Events.OfType<TalearrOpdsSettingsChangedEvent>().Count(), Is.EqualTo(1));
        }

        [Test]
        public void access_follows_the_api_key_basic_and_local_rules()
        {
            bool Login(string u, string p) => u == "dan" && p == "pw";
            const string key = "0123456789abcdef";

            Assert.That(TalearrOpdsAccess.Decide(false, key, key, null, null, true, Login), Is.EqualTo(TalearrOpdsAccessResult.Disabled));
            Assert.That(TalearrOpdsAccess.Decide(true, key, key, null, null, false, Login), Is.EqualTo(TalearrOpdsAccessResult.Allowed));
            Assert.That(TalearrOpdsAccess.Decide(true, key, "wrong", null, null, false, Login), Is.EqualTo(TalearrOpdsAccessResult.Rejected));
            Assert.That(TalearrOpdsAccess.Decide(true, key, null, "dan", "pw", false, Login), Is.EqualTo(TalearrOpdsAccessResult.Allowed));
            Assert.That(TalearrOpdsAccess.Decide(true, key, null, "anyone", key, false, Login), Is.EqualTo(TalearrOpdsAccessResult.Allowed));
            Assert.That(TalearrOpdsAccess.Decide(true, key, null, "dan", "nope", false, Login), Is.EqualTo(TalearrOpdsAccessResult.Rejected));
            Assert.That(TalearrOpdsAccess.Decide(true, key, null, null, null, false, Login), Is.EqualTo(TalearrOpdsAccessResult.NoCredentials));
            Assert.That(TalearrOpdsAccess.Decide(true, key, null, null, null, true, Login), Is.EqualTo(TalearrOpdsAccessResult.Allowed));
            Assert.That(TalearrOpdsAccess.Decide(true, string.Empty, null, "x", string.Empty, false, Login), Is.EqualTo(TalearrOpdsAccessResult.Rejected));
        }

        [Test]
        public void basic_header_is_parsed()
        {
            var header = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("dan:p:w"));

            Assert.That(TalearrOpdsAccess.TryParseBasic(header, out var user, out var password), Is.True);
            Assert.That(user, Is.EqualTo("dan"));
            Assert.That(password, Is.EqualTo("p:w"));
            Assert.That(TalearrOpdsAccess.TryParseBasic("Bearer abc", out _, out _), Is.False);
            Assert.That(TalearrOpdsAccess.TryParseBasic("Basic !!!", out _, out _), Is.False);
        }

        [Test]
        public void library_lists_only_books_with_mapped_ebook_files()
        {
            var library = Library();

            Assert.That(library.Books.Select(b => b.BookId), Is.EquivalentTo(new[] { 10, 11, 12, 13 }));
            Assert.That(library.FindBook(12).Files.Select(f => f.Id), Is.EqualTo(new[] { 4 }));
            Assert.That(library.FindFile(6).File, Is.Null);
            Assert.That(library.FindFile(7).File, Is.Null);
            Assert.That(library.FindBook(12).Series, Is.Empty, "a link to an unknown series is dropped");
        }

        [Test]
        public void authors_series_and_recent_are_grouped_and_sorted()
        {
            var library = Library();

            Assert.That(library.Authors.Select(a => a.Title), Is.EqualTo(new[] { "N. K. Jemisin", "Brandon Sanderson" }));
            Assert.That(library.Authors.Single(a => a.Id == 1).BookCount, Is.EqualTo(2));
            Assert.That(library.Series.Single().BookCount, Is.EqualTo(2));
            Assert.That(library.BySeries(50).Select(b => b.Title), Is.EqualTo(new[] { "The Final Empire", "The Well of Ascension" }));
            Assert.That(library.Recent().First().BookId, Is.EqualTo(10));
            Assert.That(library.AllBooks().First().AuthorName, Is.EqualTo("N. K. Jemisin"));
        }

        [Test]
        public void search_matches_title_author_and_series_without_accents()
        {
            var library = Library();

            Assert.That(library.Search("mistborn").Select(b => b.BookId), Is.EquivalentTo(new[] { 10, 11 }));
            Assert.That(library.Search("pokemon").Select(b => b.BookId), Is.EqualTo(new[] { 13 }));
            Assert.That(library.Search("jemisin fifth").Select(b => b.BookId), Is.EqualTo(new[] { 12 }));
            Assert.That(library.Search("   "), Is.Empty);
        }

        [Test]
        public void root_is_a_navigation_feed_with_search()
        {
            var links = new TalearrOpdsLinks("/opds", null);
            var feed = TalearrOpdsFeedWriter.Root(links, Library()).Root;

            Assert.That(feed.Name, Is.EqualTo(A + "feed"));
            Assert.That(feed.Elements(A + "entry").Select(e => e.Element(A + "title").Value), Is.EqualTo(new[] { "Recently added", "Authors", "Series", "All books" }));
            Assert.That(feed.Elements(A + "link").Single(l => (string)l.Attribute("rel") == "search").Attribute("href").Value, Is.EqualTo("/opds/opensearch.xml"));
            Assert.That(feed.Elements(A + "link").Single(l => (string)l.Attribute("rel") == "self").Attribute("type").Value, Is.EqualTo(TalearrOpdsFeedWriter.NavigationType));
        }

        [Test]
        public void book_entries_carry_acquisition_links_with_types_and_sizes()
        {
            var links = new TalearrOpdsLinks("/talearr/opds", null);
            var book = Library().FindBook(10);
            var entry = TalearrOpdsFeedWriter.BookEntry(links, book, false);

            var acquisitions = entry.Elements(A + "link").Where(l => (string)l.Attribute("rel") == "http://opds-spec.org/acquisition").ToList();
            Assert.That(acquisitions.Select(l => (string)l.Attribute("type")), Is.EqualTo(new[] { "application/epub+zip", "application/x-mobi8-ebook" }));
            Assert.That(acquisitions[0].Attribute("href").Value, Is.EqualTo("/talearr/opds/file/1"));
            Assert.That(acquisitions[0].Attribute("length").Value, Is.EqualTo("1000"));
            Assert.That(entry.Element(A + "author").Element(A + "name").Value, Is.EqualTo("Brandon Sanderson"));
            Assert.That(entry.Element(A + "summary").Value, Is.EqualTo("Mistborn #2. Book two."));
            Assert.That(entry.Elements(A + "link").Any(l => (string)l.Attribute("rel") == "http://opds-spec.org/image"), Is.True);
            Assert.That(entry.Element(TalearrOpdsFeedWriter.Dc + "language").Value, Is.EqualTo("eng"));
        }

        [Test]
        public void kepub_links_appear_only_when_offered_and_only_for_epubs()
        {
            var links = new TalearrOpdsLinks("/opds", null);
            var library = Library();

            var off = TalearrOpdsFeedWriter.BookEntry(links, library.FindBook(10), false);
            var on = TalearrOpdsFeedWriter.BookEntry(links, library.FindBook(10), true);
            var pdf = TalearrOpdsFeedWriter.BookEntry(links, library.FindBook(12), true);

            static int Kepubs(XElement e) => e.Elements(A + "link").Count(l => (string)l.Attribute("type") == TalearrOpdsFeedWriter.KepubType);

            Assert.That(Kepubs(off), Is.EqualTo(0));
            Assert.That(Kepubs(on), Is.EqualTo(1));
            Assert.That(Kepubs(pdf), Is.EqualTo(0));
            Assert.That(on.Elements(A + "link").Single(l => (string)l.Attribute("type") == TalearrOpdsFeedWriter.KepubType).Attribute("href").Value, Is.EqualTo("/opds/file/1/kepub"));
        }

        [Test]
        public void an_api_key_sign_in_is_carried_on_every_link_and_basic_keeps_it_out()
        {
            var keyed = new TalearrOpdsLinks("/opds", "abc123");
            var basic = new TalearrOpdsLinks("/opds", null);

            Assert.That(keyed.To("search", ("q", "a b")), Is.EqualTo("/opds/search?q=a%20b&apikey=abc123"));
            Assert.That(keyed.SearchTemplate(), Is.EqualTo("/opds/search?q={searchTerms}&apikey=abc123"));
            Assert.That(basic.To("recent", ("page", null)), Is.EqualTo("/opds/recent"));
            Assert.That(basic.SearchTemplate(), Is.EqualTo("/opds/search?q={searchTerms}"));
        }

        [Test]
        public void acquisition_feeds_page_with_next_and_previous_links()
        {
            var links = new TalearrOpdsLinks("/opds", null);
            var library = Library();
            var all = library.AllBooks();

            var first = TalearrOpdsPage<TalearrOpdsBook>.Of(all, 1, 10);
            Assert.That(first.PageCount, Is.EqualTo(1));

            var many = Enumerable.Range(1, 25).Select(i => all[0]).ToList();
            var page2 = TalearrOpdsPage<TalearrOpdsBook>.Of(many, 2, 10);
            var feed = TalearrOpdsFeedWriter.Books(links, "urn:x", "All books", "books", page2, Now, false, null).Root;

            string Rel(string rel) => feed.Elements(A + "link").SingleOrDefault(l => (string)l.Attribute("rel") == rel)?.Attribute("href").Value;

            Assert.That(feed.Elements(A + "entry").Count(), Is.EqualTo(10));
            Assert.That(Rel("next"), Is.EqualTo("/opds/books?page=3"));
            Assert.That(Rel("previous"), Is.EqualTo("/opds/books"));
            Assert.That(Rel("last"), Is.EqualTo("/opds/books?page=3"));
            Assert.That(feed.Element(TalearrOpdsFeedWriter.OpenSearch + "totalResults").Value, Is.EqualTo("25"));
            Assert.That(TalearrOpdsPage<TalearrOpdsBook>.Of(many, 99, 10).Page, Is.EqualTo(3));
        }

        [Test]
        public void search_feed_keeps_the_query_in_paging_links()
        {
            var links = new TalearrOpdsLinks("/opds", null);
            var many = Enumerable.Range(1, 15).Select(i => Library().Books[0]).ToList();
            var page = TalearrOpdsPage<TalearrOpdsBook>.Of(many, 1, 10);
            var feed = TalearrOpdsFeedWriter.Books(links, "urn:s", "Search", "search", page, Now, false, null, ("q", "well")).Root;

            var next = feed.Elements(A + "link").Single(l => (string)l.Attribute("rel") == "next").Attribute("href").Value;
            Assert.That(next, Is.EqualTo("/opds/search?q=well&page=2"));
        }

        [Test]
        public void opensearch_description_points_at_the_search_feed()
        {
            var document = TalearrOpdsFeedWriter.OpenSearchDescription(new TalearrOpdsLinks("/opds", null));
            var url = document.Root.Element(TalearrOpdsFeedWriter.OpenSearch + "Url");

            Assert.That(url.Attribute("template").Value, Is.EqualTo("/opds/search?q={searchTerms}"));
            Assert.That(url.Attribute("type").Value, Is.EqualTo(TalearrOpdsFeedWriter.AcquisitionType));
            Assert.That(TalearrOpdsFeedWriter.Serialize(document), Does.StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
        }

        [Test]
        public void mime_types_cover_the_common_ebook_formats()
        {
            Assert.That(TalearrOpdsFeedWriter.MimeType("/a/b.EPUB"), Is.EqualTo("application/epub+zip"));
            Assert.That(TalearrOpdsFeedWriter.MimeType("/a/b.kepub.epub"), Is.EqualTo(TalearrOpdsFeedWriter.KepubType));
            Assert.That(TalearrOpdsFeedWriter.MimeType("/a/b.pdf"), Is.EqualTo("application/pdf"));
            Assert.That(TalearrOpdsFeedWriter.MimeType("/a/b.mobi"), Is.EqualTo("application/x-mobipocket-ebook"));
            Assert.That(TalearrOpdsFeedWriter.MimeType("/a/b.xyz"), Is.EqualTo("application/octet-stream"));
        }

        [Test]
        public void health_check_warns_when_kepub_downloads_have_no_kepubify()
        {
            var opds = new TalearrOpdsSettings { Enabled = true, OfferKepub = true };

            Assert.That(TalearrEbookDeliveryCheck.GetMessage(null, null, opds, () => false), Does.Contain("kepubify"));
            Assert.That(TalearrEbookDeliveryCheck.GetMessage(null, null, opds, () => true), Is.Null);
            Assert.That(TalearrEbookDeliveryCheck.GetMessage(null, null, new TalearrOpdsSettings { OfferKepub = true }, () => false), Is.Null);
        }
    }
}
