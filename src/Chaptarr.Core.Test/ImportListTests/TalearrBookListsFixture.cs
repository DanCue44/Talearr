using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Bookwyrm;
using NzbDrone.Core.ImportLists.OpenLibrary;

namespace Chaptarr.Core.Test.ImportListTests
{
    // Talearr: Open Library (Readarr #1709) and Bookwyrm (Readarr #2877) import lists, against
    // responses shaped like the live APIs.
    [TestFixture]
    public class TalearrBookListsFixture
    {
        private const string ReadingLogPage = @"{""page"":1,""numFound"":2,""reading_log_entries"":[
            {""work"":{""title"":""Hold Me Tight"",""key"":""/works/OL7909038W"",""author_keys"":[""/authors/OL5487556A""],""author_names"":[""Sue Johnson""]},""logged_edition"":""/books/OL28313024M""},
            {""work"":{""title"":""The Invisible Man"",""key"":""/works/OL52266W"",""author_names"":[""H. G. Wells""]},""logged_edition"":null}]}";

        private const string ListSeeds = @"{""size"":3,""entries"":[
            {""url"":""/works/OL180686W"",""type"":""work"",""title"":""Ephesus""},
            {""url"":""/books/OL13518421M"",""type"":""edition"",""title"":""Six temples at Thebes""},
            {""url"":""/authors/OL1A"",""type"":""author"",""title"":""Someone""}]}";

        private const string ShelfPage1 = @"{""type"":""OrderedCollectionPage"",""next"":""https://bw.example/user/mouse/shelf/read?page=2"",""orderedItems"":[
            {""id"":""https://bw.example/book/1308"",""type"":""Edition"",""title"":""Three Men in a Boat"",""openlibraryKey"":""OL8632183M"",""isbn13"":""9781862052215"",""work"":""https://bw.example/book/1307"",""authors"":[""https://bw.example/author/266""]},
            {""id"":""https://bw.example/book/1309"",""type"":""Edition"",""title"":""No Keys At All"",""openlibraryKey"":"""",""isbn13"":"""",""isbn10"":"""",""work"":""https://bw.example/book/1310"",""authors"":[]}]}";

        private const string ShelfPage2 = @"{""type"":""OrderedCollectionPage"",""orderedItems"":[
            {""id"":""https://bw.example/book/2000"",""type"":""Edition"",""title"":""ISBN Only"",""openlibraryKey"":"""",""isbn13"":""9780141439518"",""work"":""https://bw.example/book/2001"",""authors"":[""https://bw.example/author/266""]}]}";

        private static OpenLibraryImportListSettings OpenLibrarySettings(OpenLibraryListSource source, string listId = null)
        {
            return new OpenLibraryImportListSettings { Username = "mekBot", Source = (int)source, ListId = listId };
        }

        [Test]
        public void reading_log_entries_become_open_library_works()
        {
            var (list, fake) = OpenLibraryList(OpenLibrarySettings(OpenLibraryListSource.WantToRead), url =>
                url.Contains("/people/mekBot/books/want-to-read.json") ? (HttpStatusCode.OK, ReadingLogPage) : (HttpStatusCode.NotFound, string.Empty));

            var items = list.Fetch();

            Assert.That(items.Select(i => i.BookProviderId), Is.EqualTo(new[] { "ol:OL7909038W", "ol:OL52266W" }));
            Assert.That(items[0].Author, Is.EqualTo("Sue Johnson"));
            Assert.That(items[0].AuthorProviderId, Is.Null, "the author is mapped from the work by the metadata source");
            Assert.That(fake.Requests.Single().Url.FullUri, Does.Contain("page=1&limit=100"));
        }

        [TestCase(OpenLibraryListSource.CurrentlyReading, "currently-reading")]
        [TestCase(OpenLibraryListSource.AlreadyRead, "already-read")]
        [TestCase(OpenLibraryListSource.WantToRead, "want-to-read")]
        public void each_shelf_has_its_open_library_name(OpenLibraryListSource source, string slug)
        {
            Assert.That(OpenLibraryImportList.ShelfSlug(source), Is.EqualTo(slug));
        }

        [Test]
        public void list_seeds_resolve_editions_to_works_and_skip_authors()
        {
            var (list, _) = OpenLibraryList(OpenLibrarySettings(OpenLibraryListSource.List, "https://openlibrary.org/people/mekBot/lists/OL95357L/Museum"), url =>
            {
                if (url.Contains("/lists/OL95357L/seeds.json"))
                {
                    return (HttpStatusCode.OK, ListSeeds);
                }

                return url.EndsWith("/books/OL13518421M.json")
                    ? (HttpStatusCode.OK, @"{""works"":[{""key"":""/works/OL37062W""}]}")
                    : (HttpStatusCode.NotFound, string.Empty);
            });

            var items = list.Fetch();

            Assert.That(items.Select(i => i.BookProviderId), Is.EqualTo(new[] { "ol:OL180686W", "ol:OL37062W" }));
        }

        [Test]
        public void import_limit_stops_the_list_early()
        {
            var settings = OpenLibrarySettings(OpenLibraryListSource.WantToRead);
            settings.ImportLimit = 1;
            var (list, _) = OpenLibraryList(settings, _ => (HttpStatusCode.OK, ReadingLogPage));

            Assert.That(list.Fetch().Count, Is.EqualTo(1));
        }

        [Test]
        public void a_private_reading_log_is_explained_in_the_test()
        {
            var (list, _) = OpenLibraryList(OpenLibrarySettings(OpenLibraryListSource.WantToRead), _ => (HttpStatusCode.Forbidden, @"{""detail"":""This reading log is private""}"));

            var result = list.Test();

            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.Contains("private"));
        }

        [Test]
        public void an_unknown_user_is_explained_in_the_test()
        {
            var (list, _) = OpenLibraryList(OpenLibrarySettings(OpenLibraryListSource.WantToRead), _ => (HttpStatusCode.NotFound, @"{""detail"":""User not found""}"));

            var result = list.Test();

            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.Contains("no user"));
        }

        [Test]
        public void bookwyrm_shelf_follows_pages_and_resolves_each_book()
        {
            var settings = new BookwyrmImportListSettings { BaseUrl = "https://bw.example/", Username = "@mouse", Shelf = "read" };
            var (list, fake) = BookwyrmList(settings, url =>
            {
                switch (url)
                {
                    case "https://bw.example/user/mouse/books/read?page=1":
                        return (HttpStatusCode.OK, ShelfPage1);
                    case "https://bw.example/user/mouse/shelf/read?page=2":
                        return (HttpStatusCode.OK, ShelfPage2);
                    case "https://bw.example/book/1307":
                        return (HttpStatusCode.OK, @"{""type"":""Work"",""openlibraryKey"":""OL1793164W""}");
                    case "https://bw.example/author/266":
                        return (HttpStatusCode.OK, @"{""type"":""Author"",""name"":""Jerome K. Jerome""}");
                    case "https://openlibrary.org/isbn/9780141439518.json":
                        return (HttpStatusCode.OK, @"{""works"":[{""key"":""/works/OL66554W""}]}");
                    default:
                        return (HttpStatusCode.NotFound, string.Empty);
                }
            });

            var items = list.Fetch();

            Assert.That(items.Select(i => i.BookProviderId), Is.EqualTo(new[] { "ol:OL1793164W", "ol:OL66554W" }));
            Assert.That(items[0].Author, Is.EqualTo("Jerome K. Jerome"));
            Assert.That(fake.Requests.Count(r => r.Url.FullUri == "https://bw.example/author/266"), Is.EqualTo(1), "authors are looked up once per sync");
        }

        [Test]
        public void bookwyrm_test_reports_a_missing_shelf()
        {
            var settings = new BookwyrmImportListSettings { BaseUrl = "https://bw.example", Username = "mouse", Shelf = "nope" };
            var (list, _) = BookwyrmList(settings, _ => (HttpStatusCode.NotFound, string.Empty));

            var result = list.Test();

            Assert.That(result.Errors.Select(e => e.ErrorMessage), Has.Some.Contains("no such user or shelf"));
        }

        private static (OpenLibraryImportList List, FakeHttpClient Fake) OpenLibraryList(OpenLibraryImportListSettings settings, Func<string, (HttpStatusCode, string)> handler)
        {
            var (client, fake) = FakeHttpClient.Create(request => handler(request.Url.FullUri));
            var list = new OpenLibraryImportList(new OpenLibraryListClient(client, LogManager.GetCurrentClassLogger()),
                DispatchProxy.Create<IImportListStatusService, NoOp>(), null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition { Id = 1, Name = "Open Library", Settings = settings }
            };

            return (list, fake);
        }

        private static (BookwyrmImportList List, FakeHttpClient Fake) BookwyrmList(BookwyrmImportListSettings settings, Func<string, (HttpStatusCode, string)> handler)
        {
            var (client, fake) = FakeHttpClient.Create(request => handler(request.Url.FullUri));
            var list = new BookwyrmImportList(new OpenLibraryListClient(client, LogManager.GetCurrentClassLogger()),
                DispatchProxy.Create<IImportListStatusService, NoOp>(), null, null, null, null, null, null, null, LogManager.GetCurrentClassLogger())
            {
                Definition = new ImportListDefinition { Id = 2, Name = "Bookwyrm", Settings = settings }
            };

            return (list, fake);
        }

        public class NoOp : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var type = targetMethod.ReturnType;
                return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }
    }
}
