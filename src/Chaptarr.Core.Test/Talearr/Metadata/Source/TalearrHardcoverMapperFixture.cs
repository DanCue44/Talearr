using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrHardcoverMapperFixture
    {
        internal static BookInfoProxy CreateProxy()
        {
            var logger = LogManager.GetLogger("hardcover-mapper-test");
            var health = new MetadataServerHealthService(logger);
            return new BookInfoProxy(new ScriptedHttpClient(_ => throw new System.InvalidOperationException("no HTTP in conversion")),
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
                metadataFallback: null);
        }

        [Test]
        public void author_uses_server_id_shapes_and_borrows_identifiers()
        {
            var response = TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog());

            Assert.That(response.Author.Id, Is.EqualTo("hc:42"));
            Assert.That(response.Author.HardcoverAuthorId, Is.EqualTo("hc:42"));
            Assert.That(response.Author.GoodreadsAuthorId, Is.EqualTo("gr:874602"));
            Assert.That(response.Author.Viaf, Is.EqualTo("71391324"));
            Assert.That(response.Author.Isni, Is.EqualTo("0000000121441570"));
            Assert.That(response.Author.Pseudonyms, Is.EqualTo(new[] { "U. Example" }));
            Assert.That(response.Author.ProviderUrls["hardcover"], Is.EqualTo("https://hardcover.app/authors/ursula-example"));
            Assert.That(response.Author.Photos.Single().Url, Is.EqualTo("https://assets.hardcover.app/author/42.jpg"));
        }

        [Test]
        public void works_editions_and_formats_map_to_v5()
        {
            var response = TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog());

            Assert.That(response.Books.Select(b => b.Id), Is.EqualTo(new[] { "hc:100", "hc:101", "hc:106" }));

            var book = response.Books.Single(b => b.Id == "hc:100");
            Assert.That(book.BaseBookId, Is.EqualTo("hc:100"));
            Assert.That(book.HardcoverBookId, Is.EqualTo("hc:100"));
            Assert.That(book.ProviderIdsAllSnake["hc"], Is.EqualTo(new[] { "hc:100", "hc:104" }), "the deduped Hardcover row is an alias of its canonical work");
            Assert.That(book.SeriesName, Is.EqualTo("Wind Cycle"));
            Assert.That(book.SeriesPosition, Is.EqualTo("1"));
            Assert.That(book.RatingAverage, Is.EqualTo(4.1m));
            Assert.That(book.RatingCount, Is.EqualTo(900));
            Assert.That(book.DurationSeconds, Is.EqualTo(36000), "from the default audio edition");
            Assert.That(book.Genres, Is.EqualTo(new[] { "Fantasy", "Classics" }));
            Assert.That(book.HasAudiobook && book.HasEbook && book.HasPhysical, Is.True);
            Assert.That(book.IsOmnibus, Is.False);

            // The duplicate edition 506 (canonical_id 503) is left out.
            Assert.That(book.Editions.Select(e => e.Id), Is.EqualTo(new[] { "hc:edition:501", "hc:edition:502", "hc:edition:503" }));

            var audio = book.Editions[0];
            Assert.That(audio.ReadingFormatId, Is.EqualTo(2));
            Assert.That(audio.Format, Is.EqualTo("audiobook"));
            Assert.That(audio.HardcoverEditionId, Is.EqualTo("501"));
            Assert.That(audio.Asin, Is.EqualTo("B00AUDIO01"));
            Assert.That(audio.DurationSeconds, Is.EqualTo(36000));
            Assert.That(audio.Language, Is.EqualTo("eng"));
            Assert.That(audio.Narrators.Select(n => (n.Name, n.HardcoverNarratorId)), Is.EqualTo(new[] { ("Nora Narrator", "hc:900") }));
            Assert.That(audio.Publisher, Is.EqualTo("Recorded Books"));

            var ebook = book.Editions[1];
            Assert.That(ebook.ReadingFormatId, Is.EqualTo(3), "Hardcover reading_format_id 4 (Ebook) is V5 format 3");
            Assert.That(ebook.Isbn13, Is.EqualTo("9780547773742"));
            Assert.That(ebook.Asin, Is.EqualTo("B0KINDLE01"));
            Assert.That(ebook.RatingCount, Is.EqualTo(800));

            var physical = book.Editions[2];
            Assert.That(physical.ReadingFormatId, Is.EqualTo(1));
            Assert.That(physical.Isbn10, Is.EqualTo("0553383043"));
            Assert.That(physical.PageCount, Is.EqualTo(183));
        }

        [Test]
        public void compilation_flags_mark_the_omnibus()
        {
            var response = TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog());

            Assert.That(response.Books.Single(b => b.Id == "hc:101").IsOmnibus, Is.True);
            Assert.That(response.Books.Single(b => b.Id == "hc:106").IsOmnibus, Is.False);
        }

        [Test]
        public void series_carry_hardcover_ids_positions_and_primary_slots()
        {
            var response = TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog());

            var series = response.Series.Single();
            Assert.That(series.Id, Is.EqualTo("hc:7"));
            Assert.That(series.HardcoverSeriesId, Is.EqualTo("hc:7"));
            Assert.That(series.Name, Is.EqualTo("Wind Cycle"));
            Assert.That(series.TotalBooks, Is.EqualTo(6));
            Assert.That(series.PrimaryBooks, Is.EqualTo(5));
            Assert.That(series.Numbered, Is.True);
            Assert.That(series.Books.Select(b => (b.BookId, b.Position, b.IsPrimary)), Is.EqualTo(new[]
            {
                ("hc:100", "1", (bool?)true),
                ("hc:101", "1", (bool?)false),
                ("hc:106", "1.5", (bool?)false)
            }));
        }

        [TestCase(1, null, 1)]
        [TestCase(2, null, 2)]
        [TestCase(3, null, 1)]
        [TestCase(4, null, 3)]
        [TestCase(0, "Audible Audio", 2)]
        [TestCase(0, "Kindle Edition", 3)]
        [TestCase(0, "Paperback", 1)]
        public void reading_formats_map_to_v5(int hardcover, string editionFormat, int expected)
        {
            Assert.That(TalearrHardcoverMapper.ToV5ReadingFormat(hardcover, editionFormat), Is.EqualTo(expected));
        }

        [Test]
        public void existing_conversion_builds_both_media_types_with_server_identity()
        {
            var response = TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog());
            var author = CreateProxy().ConvertV5AuthorResponse(response);

            Assert.That(author.HardcoverAuthorId, Is.EqualTo("hc:42"));
            Assert.That(author.GoodreadsAuthorId, Is.EqualTo("gr:874602"));
            Assert.That(author.Books.Count(b => b.MediaType == BookMediaType.Audiobook), Is.EqualTo(3));
            Assert.That(author.Books.Count(b => b.MediaType == BookMediaType.Ebook), Is.EqualTo(3));

            var audiobook = author.Books.Single(b => b.MediaType == BookMediaType.Audiobook && b.HardcoverBookId == "hc:100");
            Assert.That(audiobook.ForeignEditionId, Is.EqualTo("hc:100"));
            Assert.That(audiobook.BaseBookId, Is.EqualTo("hc:100"));
            Assert.That(audiobook.RemoteProviderIds, Does.Contain("hc:104"));
            Assert.That(audiobook.Editions.Select(e => e.ForeignEditionId), Does.Contain("hc:edition:501-audiobook"));
            Assert.That(audiobook.Editions.Single(e => e.ForeignEditionId == "hc:edition:501-audiobook").HardcoverEditionId, Is.EqualTo("501"));
            Assert.That(audiobook.Editions.Single(e => e.Monitored).ReadingFormatId, Is.EqualTo(2), "the audio edition is picked for the audiobook row");

            var ebook = author.Books.Single(b => b.MediaType == BookMediaType.Ebook && b.HardcoverBookId == "hc:100");
            Assert.That(ebook.Editions.Select(e => e.ForeignEditionId), Does.Contain("hc:edition:502-ebook"));
            Assert.That(ebook.Editions.Single(e => e.Monitored).ReadingFormatId, Is.EqualTo(3));

            Assert.That(author.Books.Where(b => b.HardcoverBookId == "hc:101").All(b => b.IsOmnibus), Is.True);

            // Hardcover series reach the V5 payload; the current conversion keeps only Goodreads
            // backed series (Phase 3b accepts Hardcover ids).
            Assert.That(response.Series.Count, Is.EqualTo(1));
            Assert.That(author.Series, Is.Empty);
        }

        [Test]
        public void new_work_and_edition_ids_match_server_rows_by_identity()
        {
            var author = CreateProxy().ConvertV5AuthorResponse(TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog()));
            var remote = author.Books.Single(b => b.MediaType == BookMediaType.Audiobook && b.HardcoverBookId == "hc:100");

            var serverRow = new Book { Id = 1, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:100", Title = "The Wind Book" };
            var deduped = new Book { Id = 2, MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:104", Title = "The Wind Book" };

            Assert.That(BookIdentity.MatchesByStableWorkProviderId(serverRow, remote), Is.True);
            Assert.That(BookIdentity.MatchesByStableWorkProviderId(deduped, remote), Is.True);
        }
    }
}
