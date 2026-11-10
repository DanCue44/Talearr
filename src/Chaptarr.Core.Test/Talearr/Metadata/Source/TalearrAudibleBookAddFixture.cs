using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.Metadata.Source;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // Adding an audiobook only Audible lists for an author (Talearr #7: Gaining the Edge, Symbiote
    // Wars #3, which Hardcover files under the combined author "J.N. Chaney, Chris Kennedy").
    [TestFixture]
    public class TalearrAudibleBookAddFixture
    {
        private const string Chaney = "hc:241293";
        private const string GainingTheEdge = "B0D4FKJX6M";

        [Test]
        public void a_kept_audiobook_joins_the_payload_without_audible_leading()
        {
            var response = Payload(Book("hc:1846586", "Symbiote Wars"));
            var items = new List<AudibleCatalogItem>
            {
                Item("B0CSZJ9R7R", "Symbiote Wars", null, "1"),
                Item(GainingTheEdge, "Gaining the Edge", "Symbiote Wars, Book 3", "3"),
                Item("B0DKPFKVKT", "Assault", "Symbiote Wars, Book 5", "5")
            };

            var stats = TalearrAudibleMerge.Apply(response, items, audibleLeads: false, "us", new[] { GainingTheEdge });

            var gaining = response.Books.Single(b => b.Id == "az:" + GainingTheEdge);
            Assert.That(gaining.Title, Is.EqualTo("Gaining the Edge"));
            Assert.That(response.Series.Single().Books.Single(m => m.BookId == gaining.Id).Position, Is.EqualTo("3"));
            Assert.That(response.Books.Any(b => b.Title == "Assault"), Is.False, "only the kept audiobook is added");
            Assert.That(stats.BooksAdded, Is.EqualTo(1));
        }

        [Test]
        public void the_asin_of_a_requested_audible_book()
        {
            Assert.That(TalearrAudibleBookRequests.Asin("az:b0d4fkjx6m"), Is.EqualTo(GainingTheEdge));
            Assert.That(TalearrAudibleBookRequests.Asin("az:B0D4FKJX6M-audiobook"), Is.EqualTo(GainingTheEdge));
            Assert.That(TalearrAudibleBookRequests.Asin("hc:2816228"), Is.Null);

            var audible = new Book { ForeignEditionId = "az:" + GainingTheEdge, TitleSlug = "az:" + GainingTheEdge };
            Assert.That(TalearrAudibleBookRequests.RequestedAsin(audible), Is.EqualTo(GainingTheEdge));

            var hardcover = new Book { HardcoverBookId = "hc:2816228", ForeignEditionId = "az:" + GainingTheEdge };
            Assert.That(TalearrAudibleBookRequests.RequestedAsin(hardcover), Is.Null, "a book with a work id is added as usual");
        }

        [Test]
        public void an_added_audible_book_is_found_by_its_asin_or_an_edition()
        {
            var lead = new Book { Id = 7, MediaType = BookMediaType.Audiobook, ForeignEditionId = "az:B0LEAD0001-audiobook" };
            var other = new Book { Id = 3, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:1", ForeignEditionId = "hc:edition:5" };
            var editions = new Dictionary<int, List<Edition>> { [7] = new List<Edition> { new Edition { ForeignEditionId = "az:" + GainingTheEdge + "-audiobook" } } };

            Assert.That(TalearrAudibleBookRequests.FindBook(new[] { other, lead }, "B0LEAD0001", BookMediaType.Audiobook, id => null), Is.SameAs(lead));
            Assert.That(TalearrAudibleBookRequests.FindBook(new[] { other, lead }, GainingTheEdge, BookMediaType.Audiobook, id => editions.GetValueOrDefault(id)), Is.SameAs(lead));
            Assert.That(TalearrAudibleBookRequests.FindBook(new[] { other, lead }, GainingTheEdge, BookMediaType.Ebook, id => editions.GetValueOrDefault(id)), Is.Null);
        }

        [Test]
        public void kept_audiobooks_are_stored_per_author_and_forgotten_when_deleted()
        {
            var (repository, _) = MemoryConfig.Create();
            var settings = new TalearrAudibleSourceSettings(repository, LogManager.GetCurrentClassLogger());

            settings.KeepBook(Chaney, GainingTheEdge.ToLowerInvariant());
            settings.KeepBook(Chaney, GainingTheEdge);

            Assert.That(settings.GetKeptBooks("HC:241293"), Is.EqualTo(new[] { GainingTheEdge }));
            Assert.That(new TalearrAudibleSourceSettings(repository, LogManager.GetCurrentClassLogger()).GetKeptBooks(Chaney), Is.EqualTo(new[] { GainingTheEdge }));
            Assert.That(settings.GetKeptBooks("hc:1"), Is.Empty);

            settings.Handle(new BookDeletedEvent(new Book { Title = "Gaining the Edge", ForeignEditionId = $"az:{GainingTheEdge}-audiobook" }, false, false));

            Assert.That(settings.GetKeptBooks(Chaney), Is.Empty);
        }

        [Test]
        public void search_places_an_audible_only_book_under_the_library_author()
        {
            var search = Search(Item(GainingTheEdge, "Gaining the Edge", "Symbiote Wars, Book 3", "3"));

            var hardcoverResults = new List<object>
            {
                new Book { Title = "Gaining the Edge", Author = new Author { Name = "J.N. Chaney, Chris Kennedy", HardcoverAuthorId = "hc:1164325" } }
            };

            var hit = search.Search("Gaining the Edge", hardcoverResults).Single();
            Assert.That(hit.Item.Asin, Is.EqualTo(GainingTheEdge));
            Assert.That(hit.Title, Is.EqualTo("Gaining the Edge"));
            Assert.That(hit.HardcoverAuthorId, Is.EqualTo(Chaney));
            Assert.That(hit.AuthorName, Is.EqualTo("J.N. Chaney"));
        }

        [Test]
        public void search_leaves_out_what_hardcover_already_lists_for_that_author()
        {
            var search = Search(Item(GainingTheEdge, "Gaining the Edge", "Symbiote Wars, Book 3", "3"));

            var hardcoverResults = new List<object>
            {
                new Book { Title = "Gaining the Edge", Author = new Author { Name = "J.N. Chaney", HardcoverAuthorId = Chaney } }
            };

            Assert.That(search.Search("Gaining the Edge", hardcoverResults), Is.Empty);
        }

        [Test]
        public void search_skips_authors_with_no_hardcover_id()
        {
            var item = Item(GainingTheEdge, "Gaining the Edge", null, "3");
            item.Authors = new List<string> { "Someone Else" };

            Assert.That(Search(item).Search("Gaining the Edge", new List<object>()), Is.Empty);
        }

        private static TalearrAudibleBookSearch Search(params AudibleCatalogItem[] items)
        {
            var client = DispatchProxy.Create<ITalearrAudibleClient, AudibleClientProxy>();
            var page = new AudibleCatalogPage { TotalResults = items.Length };
            page.Items.AddRange(items);
            ((AudibleClientProxy)(object)client).Page = page;

            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authors).Authors = new List<Author> { new Author { Name = "J.N. Chaney", HardcoverAuthorId = Chaney } };

            return new TalearrAudibleBookSearch(client, null, authors, LogManager.GetCurrentClassLogger());
        }

        private static V5AuthorResponse Payload(params V5Book[] books)
        {
            return new V5AuthorResponse
            {
                Author = new V5AuthorData { Id = Chaney, Name = "J.N. Chaney" },
                Books = books.ToList(),
                Series = new List<V5Series>
                {
                    new V5Series
                    {
                        Id = "hc:series:1",
                        Name = "Symbiote Wars",
                        Books = new List<V5SeriesBook> { new V5SeriesBook { BookId = books[0].Id, Position = "1" } }
                    }
                }
            };
        }

        private static V5Book Book(string id, string title)
        {
            return new V5Book { Id = id, BaseBookId = id, Title = title, Editions = new List<V5Edition>() };
        }

        private static AudibleCatalogItem Item(string asin, string title, string subtitle, string sequence)
        {
            return new AudibleCatalogItem
            {
                Asin = asin,
                Title = title,
                Subtitle = subtitle,
                Authors = new List<string> { "J.N. Chaney", "Chris Kennedy" },
                Narrators = new List<string> { "Jeffrey Kafer" },
                Series = new List<AudibleCatalogSeries> { new AudibleCatalogSeries { Asin = "B0CT4NYQ7P", Title = "Symbiote Wars", Sequence = sequence } },
                RuntimeMinutes = 600,
                ReleaseDate = "2024-06-13",
                Language = "english",
                FormatType = "unabridged",
                DeliveryType = "SinglePartBook"
            };
        }

        public class AudibleClientProxy : DispatchProxy
        {
            public AudibleCatalogPage Page { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(ITalearrAudibleClient.SearchKeywords))
                {
                    return Page;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        public class AuthorServiceProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorService.GetAllAuthors))
                {
                    return Authors;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        public class MemoryConfig : DispatchProxy
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public static (IConfigRepository Repository, MemoryConfig Proxy) Create()
            {
                var repository = DispatchProxy.Create<IConfigRepository, MemoryConfig>();
                return (repository, (MemoryConfig)(object)repository);
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IConfigRepository.Get):
                        var key = ((string)args[0]).ToLowerInvariant();
                        return _values.TryGetValue(key, out var value) ? new Config { Key = key, Value = value } : null;
                    case nameof(IConfigRepository.Upsert):
                        _values[((string)args[0]).ToLowerInvariant()] = (string)args[1];
                        return null;
                    case "All":
                        return _values.Select(kv => new Config { Key = kv.Key, Value = kv.Value }).ToList();
                    case "Delete" when args.Length == 1 && args[0] is Config config:
                        _values.Remove(config.Key);
                        return null;
                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }
    }
}
