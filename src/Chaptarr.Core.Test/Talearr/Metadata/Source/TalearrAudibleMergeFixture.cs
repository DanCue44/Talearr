using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // Symbiote Wars by J.N. Chaney and Chris Kennedy, as Hardcover and Audible listed it on
    // 2026-11-09 (Talearr #7): Hardcover has books 1 and 2 outside the series and without an
    // audiobook edition, book 3 not under J.N. Chaney at all, book 4's audiobook without an ASIN.
    [TestFixture]
    public class TalearrAudibleMergeFixture
    {
        private const string Kafer = "Jeffrey Kafer";

        private static V5AuthorResponse Hardcover()
        {
            V5Book Book(int id, string title, params V5Edition[] editions)
            {
                return new V5Book
                {
                    Id = $"hc:{id}",
                    BaseBookId = $"hc:{id}",
                    HardcoverBookId = $"hc:{id}",
                    Title = title,
                    Editions = editions.ToList(),
                    HasAudiobook = editions.Any(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio),
                    HasEbook = editions.Any(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Ebook)
                };
            }

            V5Edition Ebook(int id) => new V5Edition { Id = $"hc:edition:{id}", ReadingFormatId = TalearrHardcoverMapper.V5Ebook, Format = "ebook", Title = "ebook", Asins = new List<string>() };

            var reconAudio = new V5Edition
            {
                Id = "hc:edition:32278563",
                ReadingFormatId = TalearrHardcoverMapper.V5Audio,
                Format = "audiobook",
                Title = "Recon by Fire",
                Asins = new List<string>()
            };
            reconAudio.NarratorNames.Add(Kafer);

            var recon = Book(1904547, "Recon by Fire", Ebook(1), reconAudio);
            recon.SeriesName = "Symbiote Wars";
            recon.SeriesPosition = "4";
            var assault = Book(1904548, "Assault", Ebook(2));
            assault.SeriesName = "Symbiote Wars";
            assault.SeriesPosition = "5";

            return new V5AuthorResponse
            {
                TalearrSource = true,
                Author = new V5AuthorData { Id = "hc:181208", HardcoverAuthorId = "hc:181208", Name = "J.N. Chaney" },
                Books = new List<V5Book>
                {
                    Book(1846586, "Symbiote Wars", Ebook(3)),
                    Book(1846574, "Opening Moves: Symbiote Wars book 2", Ebook(4)),
                    recon,
                    assault
                },
                Series = new List<V5Series>
                {
                    new V5Series
                    {
                        Id = "hc:171839",
                        HardcoverSeriesId = "hc:171839",
                        Name = "Symbiote Wars",
                        SeriesType = "main",
                        Books = new List<V5SeriesBook>
                        {
                            new V5SeriesBook { BookId = "hc:1904547", Title = "Recon by Fire", Position = "4", IsPrimary = true },
                            new V5SeriesBook { BookId = "hc:1904548", Title = "Assault", Position = "5", IsPrimary = true }
                        }
                    }
                },
                Summary = new V5Summary { FormatsAvailable = new V5FormatsAvailable() }
            };
        }

        private static AudibleCatalogItem Item(string asin, string title, string subtitle, string sequence)
        {
            return new AudibleCatalogItem
            {
                Asin = asin,
                Title = title,
                Subtitle = subtitle,
                Authors = new List<string> { "J.N. Chaney", "Chris Kennedy" },
                Narrators = new List<string> { Kafer },
                Series = new List<AudibleCatalogSeries> { new AudibleCatalogSeries { Asin = "B0CT4NYQ7P", Title = "Symbiote Wars", Sequence = sequence } },
                RuntimeMinutes = 600,
                ReleaseDate = "2024-01-28",
                Language = "english",
                FormatType = "unabridged",
                DeliveryType = "SinglePartBook"
            };
        }

        private static List<AudibleCatalogItem> Audible()
        {
            return new List<AudibleCatalogItem>
            {
                Item("B0DD7Z6QJQ", "Recon by Fire", "Symbiote Wars, Book 4", "4"),
                Item("B0CSZJ9R7R", "Symbiote Wars", null, "1"),
                Item("B0D4FKJX6M", "Gaining the Edge", "Symbiote Wars, Book 3", "3"),
                Item("B0DKPFKVKT", "Assault", "Symbiote Wars, Book 5", "5"),
                Item("B0CWVSPBP8", "Opening Moves", "Symbiote Wars, Book 2", "2")
            };
        }

        private static V5Book BookById(V5AuthorResponse response, string id) => response.Books.Single(b => b.Id == id);

        private static string Place(V5AuthorResponse response, string bookId)
        {
            return response.Series.SelectMany(s => s.Books.Where(m => m.BookId == bookId).Select(m => $"{s.Name} #{m.Position}")).SingleOrDefault();
        }

        [Test]
        public void fill_adds_audiobook_editions_and_series_places_hardcover_lacks()
        {
            var response = Hardcover();

            var stats = TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            var book1 = BookById(response, "hc:1846586");
            Assert.That(book1.HasAudiobook, Is.True);
            Assert.That(book1.Editions.Single(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio).Id, Is.EqualTo("az:B0CSZJ9R7R"));
            Assert.That(book1.Editions.Single(e => e.Id == "az:B0CSZJ9R7R").NarratorNames, Is.EqualTo(new[] { Kafer }));
            Assert.That(Place(response, "hc:1846586"), Is.EqualTo("Symbiote Wars #1"));
            Assert.That(book1.SeriesName, Is.EqualTo("Symbiote Wars"));

            // "Opening Moves: Symbiote Wars book 2" is Audible's "Opening Moves".
            Assert.That(BookById(response, "hc:1846574").Editions.Any(e => e.Id == "az:B0CWVSPBP8"), Is.True);
            Assert.That(Place(response, "hc:1846574"), Is.EqualTo("Symbiote Wars #2"));

            // Hardcover's audiobook of book 4 keeps its id and gains the ASIN.
            var recon = BookById(response, "hc:1904547");
            Assert.That(recon.Editions.Count(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio), Is.EqualTo(1));
            Assert.That(recon.Editions.Single(e => e.Id == "hc:edition:32278563").Asin, Is.EqualTo("B0DD7Z6QJQ"));

            Assert.That(BookById(response, "hc:1904548").Editions.Any(e => e.Id == "az:B0DKPFKVKT"), Is.True);

            // The fill never adds books or series of its own.
            Assert.That(response.Books.Count, Is.EqualTo(4));
            Assert.That(response.Series.Count, Is.EqualTo(1));
            Assert.That(stats.BooksAdded, Is.EqualTo(0));
            Assert.That(stats.AsinsFilled, Is.EqualTo(1));
        }

        [Test]
        public void audible_leading_adds_the_books_hardcover_does_not_list_for_the_author()
        {
            var response = Hardcover();

            var stats = TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: true, "us");

            var gaining = BookById(response, "az:B0D4FKJX6M");
            Assert.That(gaining.Title, Is.EqualTo("Gaining the Edge"));
            Assert.That(gaining.Subtitle, Is.Null, "the series label is not a subtitle");
            Assert.That(gaining.HasAudiobook, Is.True);
            Assert.That(Place(response, "az:B0D4FKJX6M"), Is.EqualTo("Symbiote Wars #3"));
            Assert.That(stats.BooksAdded, Is.EqualTo(1));

            // Matched books get Audible's edition, ranked first for edition selection.
            var recon = BookById(response, "hc:1904547");
            var audible = recon.Editions.Single(e => e.Id == "az:B0DD7Z6QJQ");
            Assert.That(audible.RatingCount, Is.GreaterThan(recon.Editions.Where(e => e != audible).Max(e => e.RatingCount)));
            Assert.That(Place(response, "hc:1904547"), Is.EqualTo("Symbiote Wars #4"));
        }

        [Test]
        public void a_collection_is_never_matched_to_book_one()
        {
            var response = Hardcover();
            var box = Item("B0BOXBOX01", "Symbiote Wars: Books 1-3", null, "1-3");

            TalearrAudibleMerge.Apply(response, new[] { box }, audibleLeads: false, "us");
            Assert.That(BookById(response, "hc:1846586").HasAudiobook, Is.False);

            TalearrAudibleMerge.Apply(response, new[] { box }, audibleLeads: true, "us");
            Assert.That(BookById(response, "az:B0BOXBOX01").IsOmnibus, Is.True);
            Assert.That(BookById(response, "hc:1846586").HasAudiobook, Is.False);
        }

        [Test]
        public void fill_keeps_the_series_hardcover_already_gives_the_book()
        {
            var response = Hardcover();
            var book1 = BookById(response, "hc:1846586");
            book1.SeriesName = "Chaney Omniverse";
            response.Series.Add(new V5Series
            {
                Id = "hc:9",
                HardcoverSeriesId = "hc:9",
                Name = "Chaney Omniverse",
                Books = new List<V5SeriesBook> { new V5SeriesBook { BookId = "hc:1846586", Position = "7" } }
            });

            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            // It joins Hardcover's own Symbiote Wars too, but its main series stays Hardcover's.
            Assert.That(book1.SeriesName, Is.EqualTo("Chaney Omniverse"));
            var places = response.Series.SelectMany(s => s.Books.Where(m => m.BookId == "hc:1846586").Select(m => $"{s.Name} #{m.Position}"));
            Assert.That(places, Is.EquivalentTo(new[] { "Chaney Omniverse #7", "Symbiote Wars #1" }));
            Assert.That(book1.HasAudiobook, Is.True, "the audiobook edition is still filled");
        }

        [Test]
        public void a_series_hardcover_does_not_have_is_added_when_audible_leads()
        {
            var response = Hardcover();
            response.Series.Clear();
            BookById(response, "hc:1904547").SeriesName = null;
            BookById(response, "hc:1904548").SeriesName = null;

            var stats = TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: true, "us");

            var series = response.Series.Single();
            Assert.That(series.Id, Is.EqualTo("az:B0CT4NYQ7P"));
            Assert.That(series.AmazonSeriesAsin, Is.EqualTo("az:B0CT4NYQ7P"));
            Assert.That(series.Books.OrderBy(m => m.Position).Select(m => m.Position), Is.EqualTo(new[] { "1", "2", "3", "4", "5" }));
            Assert.That(stats.SeriesCreated, Is.EqualTo(1));
        }

        private static V5AuthorResponse Single(string id, string title, string subtitle, string series, string position)
        {
            var book = new V5Book { Id = id, BaseBookId = id, HardcoverBookId = id, Title = title, Subtitle = subtitle, SeriesName = series, SeriesPosition = position, Editions = new List<V5Edition>() };
            return new V5AuthorResponse
            {
                TalearrSource = true,
                Author = new V5AuthorData { Id = "hc:241293", Name = "J.N. Chaney" },
                Books = new List<V5Book> { book },
                Series = new List<V5Series>
                {
                    new V5Series { Id = "hc:1", HardcoverSeriesId = "hc:1", Name = series, Books = new List<V5SeriesBook> { new V5SeriesBook { BookId = id, Position = position } } }
                }
            };
        }

        private static AudibleCatalogItem InSeries(string asin, string title, string subtitle, string series, string sequence)
        {
            var item = Item(asin, title, subtitle, sequence);
            item.Series = new List<AudibleCatalogSeries> { new AudibleCatalogSeries { Asin = "B0SERIES01", Title = series, Sequence = sequence } };
            return item;
        }

        [Test]
        public void a_series_place_in_brackets_is_not_part_of_the_title()
        {
            var response = Single("hc:3086386", "Monuments to the Dead", null, "The Forgotten Ship", "8");

            TalearrAudibleMerge.Apply(response, new[] { InSeries("B0HQ5LHLJL", "Monuments to the Dead (The Forgotten Ship Book 8)", null, "The Forgotten Ship", "8") }, audibleLeads: true, "us");

            Assert.That(response.Books.Count, Is.EqualTo(1));
            Assert.That(response.Books[0].Editions.Single().Title, Is.EqualTo("Monuments to the Dead"));
        }

        [Test]
        public void the_same_main_title_in_the_same_series_place_is_the_same_book()
        {
            var response = Single("hc:1563494", "Formation", "Age of Expansion - A Kurtherian Gambit Series", "The Ghost Squadron", "1");

            TalearrAudibleMerge.Apply(response, new[] { InSeries("B078WF99ZW", "Formation: Age of Expansion", "The Ghost Squadron, Book 1", "The Ghost Squadron", "1") }, audibleLeads: true, "us");

            Assert.That(response.Books.Count, Is.EqualTo(1));
            Assert.That(response.Books[0].Editions.Single().Asin, Is.EqualTo("B078WF99ZW"));
        }

        [Test]
        public void a_later_book_of_the_series_sharing_the_main_title_stays_apart()
        {
            var response = Single("hc:80", "Sunkiller", null, "Sunkiller", "1");

            TalearrAudibleMerge.Apply(response, new[] { InSeries("B0B1F8JTKN", "Sunkiller: Odyssey", "Sunkiller, Book 2", "Sunkiller", "2") }, audibleLeads: true, "us");

            Assert.That(response.Books.Select(b => b.Id), Is.EquivalentTo(new[] { "hc:80", "az:B0B1F8JTKN" }));
        }

        [Test]
        public void only_books_credited_to_the_author_in_the_marketplace_language_are_kept()
        {
            var wanted = new HashSet<string> { TalearrSourceGapFill.Normalize("J. N. Chaney") };

            Assert.That(TalearrAudibleAuthorCatalog.IsAuthorsBook(Item("B000000001", "A", null, "1"), wanted, "english"), Is.True);

            var narratedOnly = Item("B000000002", "B", null, "1");
            narratedOnly.Authors = new List<string> { "Someone Else" };
            Assert.That(TalearrAudibleAuthorCatalog.IsAuthorsBook(narratedOnly, wanted, "english"), Is.False);

            var german = Item("B000000003", "C", null, "1");
            german.Language = "german";
            Assert.That(TalearrAudibleAuthorCatalog.IsAuthorsBook(german, wanted, "english"), Is.False);

            var podcast = Item("B000000004", "D", null, "1");
            podcast.DeliveryType = "PodcastParent";
            Assert.That(TalearrAudibleAuthorCatalog.IsAuthorsBook(podcast, wanted, "english"), Is.False);

            Assert.That(TalearrAudibleAuthorCatalog.MarketplaceLanguage("de"), Is.EqualTo("german"));
            Assert.That(TalearrAudibleAuthorCatalog.MarketplaceLanguage("uk"), Is.EqualTo("english"));
        }

        [Test]
        public void the_merged_payload_converts_to_audiobook_rows_in_the_series()
        {
            var response = Hardcover();
            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: true, "us");
            var proxy = Proxy(response);

            var author = proxy.GetAuthorInfo("hc:181208");
            var audiobooks = author.Books.Where(b => b.MediaType == BookMediaType.Audiobook).ToList();

            Assert.That(audiobooks.Select(b => b.Title), Is.EquivalentTo(new[] { "Symbiote Wars", "Opening Moves: Symbiote Wars book 2", "Gaining the Edge", "Recon by Fire", "Assault" }));

            var gaining = audiobooks.Single(b => b.Title == "Gaining the Edge");
            Assert.That(gaining.ForeignEditionId, Is.EqualTo("az:B0D4FKJX6M"));
            Assert.That(gaining.Editions.Single().ForeignEditionId, Is.EqualTo("az:B0D4FKJX6M-audiobook"));
            Assert.That(gaining.SeriesLinks.Single().Position, Is.EqualTo("3"));

            // A single-book lookup of the Audible-only book is answered from its author.
            var lookup = proxy.GetBookInfo("az:B0D4FKJX6M", BookMediaType.Audiobook, "hc:181208");
            Assert.That(lookup.Item2.Title, Is.EqualTo("Gaining the Edge"));
        }

        private static BookInfoProxy Proxy(V5AuthorResponse response)
        {
            var logger = LogManager.GetLogger("talearr-audible-merge-test");
            var health = new MetadataServerHealthService(logger);
            return new BookInfoProxy(new ScriptedHttpClient(_ => throw new InvalidOperationException("the metadata server must not be called")),
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: ConfigServiceTestProxy.Create(),
                requestBuilder: null,
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, logger),
                metadataFallback: null,
                talearrSource: new FixedPayload(response));
        }

        private sealed class FixedPayload : ITalearrSourcePayloads
        {
            private readonly V5AuthorResponse _response;

            public FixedPayload(V5AuthorResponse response)
            {
                _response = response;
            }

            public bool IsActive => true;

            public V5AuthorResponse GetAuthor(int hardcoverAuthorId, bool useCache = true) => hardcoverAuthorId == 181208 ? _response : null;

            public Dictionary<int, string> GetSignatures(IReadOnlyCollection<int> hardcoverAuthorIds) => new Dictionary<int, string>();

            public int? GetBookAuthorId(int hardcoverBookId, out int canonicalBookId)
            {
                canonicalBookId = hardcoverBookId;
                return 181208;
            }

            public int? GetEditionBookId(int hardcoverEditionId) => null;
        }
    }
}
