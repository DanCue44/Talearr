using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Metadata.Source;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // Talearr #10: Audible lists "Dietmar Wehr" (and The System States Rebellion Omnibus without
    // his Audible id); Hardcover and the library have "Dietmar Arthur Wehr".
    [TestFixture]
    public class TalearrAuthorNameMatchFixture
    {
        private const string Wehr = "hc:259462";
        private const string Omnibus = "B01B7CHK98";

        [TestCase("dietmar wehr", "dietmar arthur wehr", true)]
        [TestCase("dietmar arthur wehr", "dietmar wehr", true)]
        [TestCase("dietmar a wehr", "dietmar arthur wehr", true)]
        [TestCase("dietmar wehr", "dietmar wehr", true)]
        [TestCase("john a smith", "john b smith", false)]
        [TestCase("john smith", "jane smith", false)]
        [TestCase("dietmar wehr", "dietmar arthur wehrle", false)]
        [TestCase("wehr", "dietmar wehr", false)]
        [TestCase("john ronald reuel tolkien", "john tolkien jr", false)]
        public void same_author(string a, string b, bool expected)
        {
            Assert.That(TalearrAuthorNameMatch.SameAuthor(a, b), Is.EqualTo(expected));
        }

        [Test]
        public void short_form_drops_middle_names()
        {
            Assert.That(TalearrAuthorNameMatch.ShortForm("dietmar arthur wehr"), Is.EqualTo("dietmar wehr"));
            Assert.That(TalearrAuthorNameMatch.ShortForm("dietmar wehr"), Is.Null);
        }

        [Test]
        public void a_name_two_library_authors_could_be_is_not_matched()
        {
            var authors = new Dictionary<string, (string Id, string Name)>
            {
                ["john adam smith"] = ("hc:1", "John Adam Smith"),
                ["john bob smith"] = ("hc:2", "John Bob Smith")
            };

            Assert.That(TalearrAuthorNameMatch.FindUnique("john smith", authors, a => a.Id), Is.Null);
            Assert.That(TalearrAuthorNameMatch.FindUnique("john adam smith", authors, a => a.Id)?.Id, Is.EqualTo("hc:1"));
        }

        [Test]
        public void an_audible_name_without_the_middle_name_finds_the_library_author()
        {
            var search = Search(new List<Author> { new Author { Name = "Dietmar Arthur Wehr", HardcoverAuthorId = Wehr } });

            var hit = search.FindLibraryAuthor(new[] { "Dietmar Wehr" });

            Assert.That(hit.HardcoverAuthorId, Is.EqualTo(Wehr));
            Assert.That(hit.AuthorName, Is.EqualTo("Dietmar Arthur Wehr"));
        }

        [Test]
        public void an_alias_finds_the_library_author()
        {
            var search = Search(new List<Author>
            {
                new Author { Name = "D. A. Wehr", HardcoverAuthorId = Wehr, Aliases = new List<string> { "Dietmar Wehr" } }
            });

            Assert.That(search.FindLibraryAuthor(new[] { "Dietmar Wehr" })?.HardcoverAuthorId, Is.EqualTo(Wehr));
            Assert.That(search.FindLibraryAuthor(new[] { "Someone Else" }), Is.Null);
        }

        [Test]
        public void the_add_search_places_an_audible_book_under_the_author_with_a_middle_name()
        {
            var item = Item("B0FXF5SPR7", "Armageddon Alley", "Cross-Time Wars #1");
            var search = Search(new List<Author> { new Author { Name = "Dietmar Arthur Wehr", HardcoverAuthorId = Wehr } }, item);

            var hit = search.Search("Armageddon Alley", new List<object>()).Single();

            Assert.That(hit.HardcoverAuthorId, Is.EqualTo(Wehr));
        }

        [Test]
        public void the_author_catalog_tries_the_name_without_middle_names()
        {
            using var folder = new TalearrTestFolder();
            var client = DispatchProxy.Create<ITalearrAudibleClient, AuthorClientProxy>();
            var proxy = (AuthorClientProxy)(object)client;
            proxy.Pages["Dietmar Wehr"] = Page(Item(Omnibus, "The System States Rebellion Omnibus", "System States Rebellion, Books 1-2"));

            var catalog = new TalearrAudibleAuthorCatalog(client, null, folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            var result = catalog.Get(Wehr, new[] { "Dietmar Arthur Wehr" });

            Assert.That(proxy.Queries, Is.EqualTo(new[] { "Dietmar Arthur Wehr", "Dietmar Wehr" }).IgnoreCase);
            Assert.That(result.Items.Select(i => i.Asin), Is.EqualTo(new[] { Omnibus }));
        }

        [Test]
        public void the_author_catalog_keeps_to_the_full_name_when_it_finds_books()
        {
            using var folder = new TalearrTestFolder();
            var client = DispatchProxy.Create<ITalearrAudibleClient, AuthorClientProxy>();
            var proxy = (AuthorClientProxy)(object)client;
            proxy.Pages["Dietmar Arthur Wehr"] = Page(Item("B00QVLIZ9K", "The Synchronicity War 1", null, "Dietmar Arthur Wehr"));

            var catalog = new TalearrAudibleAuthorCatalog(client, null, folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            catalog.Get(Wehr, new[] { "Dietmar Arthur Wehr" });

            Assert.That(proxy.Queries, Is.EqualTo(new[] { "Dietmar Arthur Wehr" }));
        }

        [Test]
        public void a_stored_catalog_without_ratings_is_read_again()
        {
            using var folder = new TalearrTestFolder();
            var database = folder.CreateDatabase();
            var client = DispatchProxy.Create<ITalearrAudibleClient, AuthorClientProxy>();
            var proxy = (AuthorClientProxy)(object)client;
            proxy.Pages["Dietmar Wehr"] = Page(Item(Omnibus, "The System States Rebellion Omnibus", null));

            new TalearrAudibleAuthorCatalog(client, null, database, LogManager.GetCurrentClassLogger()).Get(Wehr, new[] { "Dietmar Wehr" });

            var rated = Item(Omnibus, "The System States Rebellion Omnibus", null);
            rated.RatingCount = 836;
            proxy.Pages["Dietmar Wehr"] = Page(rated);
            var result = new TalearrAudibleAuthorCatalog(client, null, database, LogManager.GetCurrentClassLogger()).Get(Wehr, new[] { "Dietmar Wehr" });
            new TalearrAudibleAuthorCatalog(client, null, database, LogManager.GetCurrentClassLogger()).Get(Wehr, new[] { "Dietmar Wehr" });

            Assert.That(proxy.Queries, Has.Count.EqualTo(2));
            Assert.That(result.Items.Single().RatingCount, Is.EqualTo(836));
        }

        private static TalearrAudibleBookSearch Search(List<Author> library, params AudibleCatalogItem[] items)
        {
            var client = DispatchProxy.Create<ITalearrAudibleClient, TalearrAudibleBookAddFixture.AudibleClientProxy>();
            ((TalearrAudibleBookAddFixture.AudibleClientProxy)(object)client).Page = Page(items);

            var authors = DispatchProxy.Create<IAuthorService, TalearrAudibleBookAddFixture.AuthorServiceProxy>();
            ((TalearrAudibleBookAddFixture.AuthorServiceProxy)(object)authors).Authors = library;

            return new TalearrAudibleBookSearch(client, null, authors, LogManager.GetCurrentClassLogger());
        }

        private static AudibleCatalogPage Page(params AudibleCatalogItem[] items)
        {
            var page = new AudibleCatalogPage { TotalResults = items.Length };
            page.Items.AddRange(items);
            return page;
        }

        private static AudibleCatalogItem Item(string asin, string title, string subtitle, string author = "Dietmar Wehr")
        {
            return new AudibleCatalogItem
            {
                Asin = asin,
                Title = title,
                Subtitle = subtitle,
                Authors = new List<string> { author },
                Narrators = new List<string> { "Jeffrey Hofer" },
                RuntimeMinutes = 690,
                ReleaseDate = "2016-01-26",
                Language = "english",
                FormatType = "unabridged",
                DeliveryType = "SinglePartBook"
            };
        }

        public class AuthorClientProxy : DispatchProxy
        {
            public Dictionary<string, AudibleCatalogPage> Pages { get; } = new Dictionary<string, AudibleCatalogPage>(StringComparer.OrdinalIgnoreCase);
            public List<string> Queries { get; } = new List<string>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ITalearrAudibleClient.SearchAuthor))
                {
                    var name = (string)args[1];
                    Queries.Add(name);
                    return Pages.TryGetValue(name, out var page) ? page : new AudibleCatalogPage();
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }
}
