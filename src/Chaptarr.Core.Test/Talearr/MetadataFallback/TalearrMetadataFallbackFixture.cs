using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using Newtonsoft.Json.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.Hardcover;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace Chaptarr.Core.Test.Talearr.MetadataFallback
{
    [TestFixture]
    [NonParallelizable]
    public class TalearrMetadataFallbackFixture
    {
        private const string Source = "https://api2.chaptarr.com";

        private MemoryTarget _memory;
        private LogFactory _logFactory;
        private Logger _logger;

        [SetUp]
        public void SetUp()
        {
            TalearrMetadataOutageLog.Reset(Source);

            _memory = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRuleForAllLevels(_memory);
            _logFactory = new LogFactory { Configuration = configuration };
            _logger = _logFactory.GetLogger("fallback");
        }

        [TearDown]
        public void TearDown()
        {
            TalearrMetadataOutageLog.Reset(Source);
            _logFactory.Dispose();
        }

        private (MetadataServerHealthService Health, MetadataServerHealthGate Gate) CreateGate()
        {
            var health = new MetadataServerHealthService(_logger);
            return (health, new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, _logger));
        }

        private static void OpenCircuit(MetadataServerHealthService health)
        {
            health.ReportFailure(Source, new WebException("connect failed", WebExceptionStatus.ConnectFailure));
        }

        private TalearrMetadataFallbackService CreateService(MetadataServerHealthGate gate,
                                                             FakeOpenLibraryClient openLibrary = null,
                                                             FakeFallbackSettings settings = null,
                                                             FakeProbe probe = null)
        {
            return new TalearrMetadataFallbackService(settings ?? new FakeFallbackSettings(),
                openLibrary ?? new FakeOpenLibraryClient(),
                probe ?? new FakeProbe(),
                gate,
                eventAggregator: null,
                logger: _logger);
        }

        private BookInfoProxy CreateProxy(IHttpClient http,
                                          IMetadataServerHealthGate gate,
                                          ITalearrMetadataFallback fallback,
                                          IHardcoverSearchClient hardcover = null,
                                          NzbDrone.Core.Configuration.IConfigService config = null)
        {
            return new BookInfoProxy(http,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: hardcover,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: config ?? ConfigServiceTestProxy.Create(),
                requestBuilder: null,
                logger: _logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: gate,
                metadataFallback: fallback);
        }

        // ---- Mapping (Open Library JSON to metadata server shapes) ----

        [Test]
        public void author_found_through_open_library_keeps_ol_identity_and_borrows_goodreads_and_amazon_ids()
        {
            var response = TalearrOpenLibraryMapper.BuildAuthorResponse(OpenLibraryFixtures.AuthorRecord(), OpenLibraryFixtures.WorkDocs(), "ol:OL239800A");

            Assert.That(response.Author.Id, Is.EqualTo("ol:OL239800A"));
            Assert.That(response.Author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(response.Author.GoodreadsAuthorId, Is.EqualTo("gr:431149"));
            Assert.That(response.Author.AudnexusAuthorId, Is.EqualTo("az:B000APW81E"));
            Assert.That(response.Author.HardcoverAuthorId, Is.Null);
            Assert.That(response.Author.BirthDate, Is.EqualTo("1924-09-30"));
            Assert.That(response.Author.DeathDate, Is.EqualTo("1984-08-25"));
            Assert.That(response.Author.Bio, Does.StartWith("Truman Capote was an American writer"));
            Assert.That(response.Author.Photos.First().Url, Is.EqualTo("https://covers.openlibrary.org/a/id/14854669-L.jpg"));
        }

        [Test]
        public void author_requested_from_another_catalog_keeps_that_id_and_no_namesake_ids()
        {
            var response = TalearrOpenLibraryMapper.BuildAuthorResponse(OpenLibraryFixtures.AuthorRecord(), OpenLibraryFixtures.WorkDocs(), "hc:136113");

            Assert.That(response.Author.Id, Is.EqualTo("hc:136113"));
            Assert.That(response.Author.HardcoverAuthorId, Is.EqualTo("hc:136113"));
            Assert.That(response.Author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(response.Author.GoodreadsAuthorId, Is.Null, "a name match may be a namesake; no third-party ids are attached");
            Assert.That(response.Author.AudnexusAuthorId, Is.Null);
        }

        [Test]
        public void works_map_to_books_with_prefixed_open_library_ids_and_one_edition()
        {
            var response = TalearrOpenLibraryMapper.BuildAuthorResponse(OpenLibraryFixtures.AuthorRecord(), OpenLibraryFixtures.WorkDocs(), "ol:OL239800A");

            Assert.That(response.Books.Select(b => b.Id), Is.EquivalentTo(new[] { "ol:OL1992198W", "ol:OL1992167W" }),
                "works of other authors and works without editions are left out");

            var inColdBlood = response.Books.Single(b => b.Id == "ol:OL1992198W");
            Assert.That(inColdBlood.OpenLibraryWorkId, Is.EqualTo("ol:OL1992198W"));
            Assert.That(inColdBlood.HardcoverBookId, Is.Null);
            Assert.That(inColdBlood.Title, Is.EqualTo("In Cold Blood"));
            Assert.That(inColdBlood.PublicationYear, Is.EqualTo(1965));
            Assert.That(inColdBlood.CoverUrl, Is.EqualTo("https://covers.openlibrary.org/b/id/228066-L.jpg"));
            Assert.That(inColdBlood.Editions, Has.Count.EqualTo(1));

            var edition = inColdBlood.Editions[0];
            Assert.That(edition.Id, Is.EqualTo("ol:OL50551313M"));
            Assert.That(edition.ReadingFormatId, Is.EqualTo(1));
            Assert.That(edition.Isbn13, Is.EqualTo("9780141038391"));
            Assert.That(edition.Isbn10, Is.EqualTo("0451154460"));
            Assert.That(edition.LanguageCode, Is.EqualTo("eng"));

            var tiffany = response.Books.Single(b => b.Id == "ol:OL1992167W");
            Assert.That(tiffany.Editions[0].Id, Is.EqualTo("ol:OL7351911M"), "the cover edition is preferred");
        }

        [Test]
        public void a_requested_hardcover_work_is_attached_to_the_single_open_library_work_with_that_title()
        {
            var titles = new Dictionary<string, string>
            {
                { "hc:45874", "Breakfast at Tiffany's" },
                { "hc:999", "A Book Open Library Does Not Have" }
            };

            var response = TalearrOpenLibraryMapper.BuildAuthorResponse(OpenLibraryFixtures.AuthorRecord(), OpenLibraryFixtures.WorkDocs(), "hc:136113", titles);

            Assert.That(response.Books.Single(b => b.Id == "ol:OL1992167W").HardcoverBookId, Is.EqualTo("hc:45874"));
            Assert.That(response.Books.Single(b => b.Id == "ol:OL1992198W").HardcoverBookId, Is.Null);
        }

        [Test]
        public void open_library_fields_that_change_shape_are_read_safely()
        {
            var record = OpenLibraryAuthorRecord.Parse(JToken.Parse(OpenLibraryFixtures.AuthorWithBioObject));

            Assert.That(record.Key, Is.EqualTo("OL1A"));
            Assert.That(record.Bio, Is.EqualTo("A bio in an object."));
            Assert.That(TalearrOpenLibraryMapper.ToIsoDate("1924"), Is.Null, "partial dates are dropped, not guessed");
            Assert.That(OpenLibraryWorkDoc.Parse(JToken.Parse(@"{""key"":""/works/OL1W""}")), Is.Null, "a work needs a title");
        }

        [Test]
        public void search_results_map_to_ol_authors_and_books_marked_as_fallback()
        {
            var authorDoc = JToken.Parse(OpenLibraryFixtures.AuthorSearch)["docs"].Select(OpenLibraryAuthorDoc.Parse).First();
            var author = TalearrOpenLibraryMapper.MapSearchAuthor(authorDoc);

            Assert.That(author.Name, Is.EqualTo("Truman Capote"));
            Assert.That(author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(AuthorIdentity.GetPreferredProviderId(author), Is.EqualTo("ol:OL239800A"));
            Assert.That(author.MetadataBookCount, Is.EqualTo(109));
            Assert.That(author.Died, Is.EqualTo(new DateTime(1984, 8, 25, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(TalearrMetadataFallbackMarker.GetSource(author), Is.EqualTo("Open Library"));

            var workDoc = JToken.Parse(OpenLibraryFixtures.WorkSearch)["docs"].Select(OpenLibraryWorkDoc.Parse).First();
            var book = TalearrOpenLibraryMapper.MapSearchBook(workDoc);

            Assert.That(book.OpenLibraryWorkId, Is.EqualTo("ol:OL1992198W"));
            Assert.That(BookIdentity.GetStableWorkProviderIdentityTokens(book), Is.EquivalentTo(new[] { "ol:OL1992198W" }));
            Assert.That(book.Author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(book.Editions.Single().Isbn13, Is.EqualTo("9780141038391"));
            Assert.That(book.Images.Single().Url, Is.EqualTo("https://covers.openlibrary.org/b/id/228066-L.jpg"));
            Assert.That(TalearrMetadataFallbackMarker.GetSource(book), Is.EqualTo("Open Library"));
        }

        // ---- When the fallback is used ----

        [Test]
        public void primary_up_a_legitimate_not_found_never_uses_the_fallback()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate);

            Assert.That(service.IsPrimaryUnavailable(), Is.False);
            Assert.That(service.ShouldUseForAdd("hc:136113", new AuthorNotFoundException("hc:136113")), Is.False);
            Assert.That(service.ShouldUseForAdd("hc:136113", new BookInfoException("V5 API error: Forbidden")), Is.False);
            Assert.That(service.ShouldUseForSearch(null), Is.False);
            Assert.That(service.ShouldUseForSearch(new InvalidOperationException("bad data")), Is.False);
        }

        [Test]
        public void open_library_ids_use_the_fallback_when_the_server_does_not_know_them()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate);

            Assert.That(service.ShouldUseForAdd("ol:OL239800A", new AuthorNotFoundException("ol:OL239800A")), Is.True);
        }

        [Test]
        public void server_and_network_errors_use_the_fallback_even_before_the_circuit_opens()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate);

            Assert.That(service.ShouldUseForAdd("hc:1", new BookInfoException("V5 API error: BadGateway")), Is.True);
            Assert.That(service.ShouldUseForAdd("hc:1", new BookInfoException("Failed to get author info from V5 API", new HttpRequestException("reset"))), Is.True);
            Assert.That(service.ShouldUseForSearch(new NzbDroneClientException(HttpStatusCode.ServiceUnavailable, "Hardcover search failed", new TimeoutException())), Is.True);
        }

        [Test]
        public void primary_down_uses_the_fallback_and_off_never_does()
        {
            var (health, gate) = CreateGate();
            var settings = new FakeFallbackSettings();
            var service = CreateService(gate, settings: settings);

            OpenCircuit(health);

            Assert.That(service.IsPrimaryUnavailable(), Is.True);
            Assert.That(service.ShouldUseForAdd("hc:1", new BookInfoException("Metadata server is temporarily unavailable for author hc:1; retrying in 1m")), Is.True);
            Assert.That(service.ShouldUseForSearch(null), Is.True);
            Assert.That(service.ShouldUseForAdd("hc:1", new AuthorNotFoundException("hc:1")), Is.False, "not found is an answer, not an outage");

            settings.Mode = TalearrMetadataFallbackMode.Off;

            Assert.That(service.ShouldUseForAdd("hc:1", new BookInfoException("V5 API error: BadGateway")), Is.False);
            Assert.That(service.ShouldUseForSearch(null), Is.False);
        }

        [Test]
        public void a_probe_reporting_down_counts_as_primary_down()
        {
            var (_, gate) = CreateGate();
            var probe = new FakeProbe { LastResult = new MetadataServerProbeResult { State = MetadataServerProbeState.Down } };
            var service = CreateService(gate, probe: probe);

            Assert.That(service.IsPrimaryUnavailable(), Is.True);
            Assert.That(service.GetStatus().ActiveSource, Is.EqualTo("Open Library"));
        }

        [Test]
        public void fallback_author_by_name_takes_the_exact_name_with_the_most_works_and_records_status()
        {
            var (_, gate) = CreateGate();
            var openLibrary = new FakeOpenLibraryClient();
            var service = CreateService(gate, openLibrary);

            var response = service.GetAuthor("hc:136113", "Truman Capote");

            Assert.That(response.Author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(openLibrary.Calls, Is.EqualTo(new[] { "SearchAuthors:Truman Capote", "GetAuthor:OL239800A", "GetAuthorWorks:OL239800A" }));

            var status = service.GetStatus();
            Assert.That(status.LastUsed, Is.Not.Null);
            Assert.That(status.LastOperation, Is.EqualTo("add: author hc:136113"));
            Assert.That(status.UsesSinceStart, Is.EqualTo(1));
        }

        [Test]
        public void fallback_author_without_an_exact_name_match_adds_nothing()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate);

            Assert.That(service.GetAuthor("hc:1", "Truman Capot"), Is.Null);
            Assert.That(service.GetAuthor("hc:1", null), Is.Null);
            Assert.That(service.GetStatus().LastError, Does.Contain("no matching author"));
        }

        [Test]
        public void open_library_failure_during_add_returns_null_so_the_original_error_stands()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate, new FakeOpenLibraryClient { Fail = true });

            Assert.That(service.GetAuthor("ol:OL239800A", null), Is.Null);
            Assert.That(service.GetStatus().LastError, Does.Contain("503"));
        }

        [Test]
        public void fallback_search_returns_authors_with_works_and_books()
        {
            var (_, gate) = CreateGate();
            var service = CreateService(gate);

            var results = service.Search("capote");

            Assert.That(results.OfType<Author>().Select(a => a.OpenLibraryAuthorId), Is.EqualTo(new[] { "ol:OL239800A", "ol:OL13976414A" }),
                "authors with no works are noise and are left out");
            Assert.That(results.OfType<Book>().Select(b => b.OpenLibraryWorkId), Is.EqualTo(new[] { "ol:OL1992198W" }));
            Assert.That(service.GetStatus().LastOperation, Is.EqualTo("search: capote"));
        }

        // ---- BookInfoProxy wiring ----

        [Test]
        public void primary_up_search_uses_the_normal_provider_and_never_open_library()
        {
            var (_, gate) = CreateGate();
            var openLibrary = new FakeOpenLibraryClient();
            var hardcover = new FakeHardcoverSearch();
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.NotFound), gate, CreateService(gate, openLibrary), hardcover, HardcoverConfigProxy.Create());

            var results = proxy.SearchForNewEntity("capote", "hardcover");

            Assert.That(hardcover.Calls, Is.EqualTo(1));
            Assert.That(results.OfType<Author>().Single().HardcoverAuthorId, Is.EqualTo("hc:136113"));
            Assert.That(TalearrMetadataFallbackMarker.GetSource(results[0]), Is.Null);
            Assert.That(openLibrary.Calls, Is.Empty);
        }

        [Test]
        public void primary_down_and_search_provider_failing_returns_open_library_results()
        {
            var (health, gate) = CreateGate();
            OpenCircuit(health);

            var openLibrary = new FakeOpenLibraryClient();
            var hardcover = new FakeHardcoverSearch { Error = new HttpRequestException("connection reset") };
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.BadGateway), gate, CreateService(gate, openLibrary), hardcover, HardcoverConfigProxy.Create());

            var results = proxy.SearchForNewEntity("capote", "hardcover");

            Assert.That(results.OfType<Author>().First().OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(results.OfType<Book>().Single().OpenLibraryWorkId, Is.EqualTo("ol:OL1992198W"));
            Assert.That(results.All(r => TalearrMetadataFallbackMarker.GetSource(r) == "Open Library"), Is.True);
        }

        [Test]
        public void a_missing_hardcover_token_is_not_an_outage_while_the_server_is_up()
        {
            var (_, gate) = CreateGate();
            var openLibrary = new FakeOpenLibraryClient();
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.NotFound), gate, CreateService(gate, openLibrary), new FakeHardcoverSearch(), HardcoverConfigProxy.Create(enabled: false));

            Assert.Throws<NzbDroneClientException>(() => proxy.SearchForNewEntity("capote", "hardcover"));
            Assert.That(openLibrary.Calls, Is.Empty);
        }

        [Test]
        public void open_library_failing_too_keeps_the_original_search_error()
        {
            var (health, gate) = CreateGate();
            OpenCircuit(health);

            var hardcover = new FakeHardcoverSearch { Error = new HttpRequestException("connection reset") };
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.BadGateway), gate, CreateService(gate, new FakeOpenLibraryClient { Fail = true }), hardcover, HardcoverConfigProxy.Create());

            var ex = Assert.Throws<NzbDroneClientException>(() => proxy.SearchForNewEntity("capote", "hardcover"));
            Assert.That(ex.Message, Is.EqualTo("Hardcover search failed"));
        }

        [Test]
        public void fallback_author_converts_to_audiobook_and_ebook_rows_with_open_library_ids()
        {
            var (health, gate) = CreateGate();
            OpenCircuit(health);
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.BadGateway), gate, CreateService(gate));

            Assert.That(proxy.ShouldUseMetadataFallback("hc:136113", new BookInfoException("V5 API error: BadGateway")), Is.True);

            var author = proxy.GetFallbackAuthorInfo("hc:136113", "Truman Capote");

            Assert.That(author.Name, Is.EqualTo("Truman Capote"));
            Assert.That(author.HardcoverAuthorId, Is.EqualTo("hc:136113"));
            Assert.That(author.OpenLibraryAuthorId, Is.EqualTo("ol:OL239800A"));
            Assert.That(AuthorIdentity.GetPreferredProviderId(author), Is.EqualTo("hc:136113"), "refresh after recovery uses the server's id");
            Assert.That(TalearrMetadataFallbackMarker.GetSource(author), Is.EqualTo("Open Library"));

            var rows = author.Books.Where(b => b.OpenLibraryWorkId == "ol:OL1992198W").ToList();
            Assert.That(rows.Select(b => b.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
            Assert.That(rows.All(b => b.HardcoverBookId == null && b.BaseBookId == "ol:OL1992198W"), Is.True);
            Assert.That(rows.Single(b => b.MediaType == BookMediaType.Audiobook).Editions.Single().ForeignEditionId, Is.EqualTo("ol:OL50551313M-audiobook"));
            Assert.That(rows.Single(b => b.MediaType == BookMediaType.Ebook).Editions.Single().Isbn13, Is.EqualTo("9780141038391"));
        }

        [Test]
        public void fallback_off_returns_no_fallback_author()
        {
            var (health, gate) = CreateGate();
            OpenCircuit(health);
            var settings = new FakeFallbackSettings { Mode = TalearrMetadataFallbackMode.Off };
            var openLibrary = new FakeOpenLibraryClient();
            var proxy = CreateProxy(new StatusHttpClient(HttpStatusCode.BadGateway), gate, CreateService(gate, openLibrary, settings));

            Assert.That(proxy.ShouldUseMetadataFallback("hc:136113", new BookInfoException("V5 API error: BadGateway")), Is.False);
            Assert.That(proxy.GetFallbackAuthorInfo("hc:136113", "Truman Capote"), Is.Null);
            Assert.That(openLibrary.Calls, Is.Empty);
        }

        [Test]
        public void refresh_during_an_outage_never_reaches_the_fallback()
        {
            var (_, gate) = CreateGate();
            var fallback = new RecordingFallback();
            var http = new StatusHttpClient(HttpStatusCode.BadGateway);
            var proxy = CreateProxy(http, gate, fallback);

            var results = Enumerable.Range(1, 5).Select(i => proxy.RefreshAuthorInfo($"hc:{i}")).ToList();

            Assert.That(results.All(r => r.Reason == RefreshReason.Error && r.Author == null), Is.True, "nothing to apply, so existing data stays");
            Assert.Catch(() => proxy.GetAuthorInfo("hc:1"));
            Assert.That(fallback.Calls, Does.Not.Contain(nameof(ITalearrMetadataFallback.GetAuthor)));
            Assert.That(fallback.Calls, Does.Not.Contain(nameof(ITalearrMetadataFallback.Search)));
        }

        // ---- Add path (AuthorLibraryService) ----

        private static AuthorLibraryService CreateLibrary(IProvideAuthorInfo authorInfo, Logger logger)
        {
            return new AuthorLibraryService(authorService: null,
                authorInfo: authorInfo,
                bookService: null,
                refreshSeriesService: null,
                editionService: null,
                narratorLinkService: null,
                metadataProfileService: null,
                qualityProfileService: null,
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                eventAggregator: null,
                pendingImportService: null,
                mainDatabase: null,
                importListExclusionService: null,
                editionMetadataProfileFilter: null,
                syncMetadataService: null,
                logger: logger);
        }

        private static async Task<(Author Author, bool UsedFallback)> Fetch(AuthorLibraryService library, string providerId, MonitoringConfig config)
        {
            var method = typeof(AuthorLibraryService).GetMethod("FetchAuthorBlobOrFallbackAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null);
            return await (Task<(Author, bool)>)method.Invoke(library, new object[] { providerId, config });
        }

        [Test]
        public async Task explicit_add_during_an_outage_uses_the_fallback_author()
        {
            var fallbackAuthor = new Author { Name = "Truman Capote", HardcoverAuthorId = "hc:136113" };
            var authorInfo = new FailingAuthorInfo(new BookInfoException("V5 API error: BadGateway"), (_, _) => true, (_, name) => name == "Truman Capote" ? fallbackAuthor : null);
            var library = CreateLibrary(authorInfo, _logger);

            var (author, usedFallback) = await Fetch(library, "hc:136113", new MonitoringConfig { AllowMetadataFallback = true, AuthorName = "Truman Capote" });

            Assert.That(usedFallback, Is.True);
            Assert.That(author, Is.SameAs(fallbackAuthor));
        }

        [Test]
        public void queue_retries_and_adds_that_did_not_opt_in_keep_the_original_error()
        {
            var error = new BookInfoException("V5 API error: BadGateway");
            var authorInfo = new FailingAuthorInfo(error, (_, _) => true, (_, _) => new Author { Name = "x" });
            var library = CreateLibrary(authorInfo, _logger);

            Assert.ThrowsAsync<BookInfoException>(() => Fetch(library, "hc:1", new MonitoringConfig { AllowMetadataFallback = true, IsFromQueue = true }));
            Assert.ThrowsAsync<BookInfoException>(() => Fetch(library, "hc:1", new MonitoringConfig()));
            Assert.ThrowsAsync<BookInfoException>(() => Fetch(library, "hc:1", null));
            Assert.That(authorInfo.FallbackCalls, Is.Zero);
        }

        [Test]
        public void no_fallback_author_rethrows_the_metadata_server_error()
        {
            var authorInfo = new FailingAuthorInfo(new AuthorNotFoundException("ol:OL1A"), (_, _) => true, (_, _) => null);
            var library = CreateLibrary(authorInfo, _logger);

            Assert.ThrowsAsync<AuthorNotFoundException>(() => Fetch(library, "ol:OL1A", new MonitoringConfig { AllowMetadataFallback = true }));
            Assert.That(authorInfo.FallbackCalls, Is.EqualTo(1));
        }

        [Test]
        public void single_book_add_allows_the_fallback_and_passes_the_title()
        {
            var book = new Book { Title = "Breakfast at Tiffany's", MediaType = BookMediaType.Audiobook, Author = new Author { Name = "Truman Capote" } };

            var config = AddBookService.BuildRequestedBookMonitoringConfig(book, new List<string> { "hc:45874" }, searchRequested: false);

            Assert.That(config.AllowMetadataFallback, Is.True);
            Assert.That(config.MetadataFallbackWorkTitles["hc:45874"], Is.EqualTo("Breakfast at Tiffany's"));
        }

        // ---- Reconciliation when the server is back ----

        private static Book LocalFallbackBook(int id, string title, BookMediaType mediaType, string olWorkId, string isbn13 = null)
        {
            return new Book
            {
                Id = id,
                Title = title,
                MediaType = mediaType,
                OpenLibraryWorkId = olWorkId,
                BaseBookId = olWorkId,
                Editions = new List<Edition> { new Edition { ForeignEditionId = "ol:OL1M-audiobook", Isbn13 = isbn13 } }
            };
        }

        private static Book RemoteBook(string title, BookMediaType mediaType, string hcId, params string[] isbn13s)
        {
            return new Book
            {
                Title = title,
                MediaType = mediaType,
                HardcoverBookId = hcId,
                BaseBookId = hcId,
                Editions = isbn13s.Select((isbn, i) => new Edition { ForeignEditionId = $"hc:edition:{i}-audiobook", Isbn13 = isbn }).ToList()
            };
        }

        [Test]
        public void server_book_with_the_same_isbn_takes_the_open_library_id_and_then_matches_the_local_row()
        {
            var local = LocalFallbackBook(10, "In Cold Blood: A True Account", BookMediaType.Audiobook, "ol:OL1992198W", "9780141038391");
            var remote = RemoteBook("In Cold Blood", BookMediaType.Audiobook, "hc:1", "9780679745587", "9780141038391");
            var other = RemoteBook("Other Voices, Other Rooms", BookMediaType.Audiobook, "hc:2");

            Assert.That(BookIdentity.MatchesByStableWorkProviderId(local, remote), Is.False);

            var attached = TalearrFallbackReconciler.Attach(new[] { local }, new[] { remote, other }, _logger, "Truman Capote");

            Assert.That(attached, Is.EqualTo(1));
            Assert.That(remote.OpenLibraryWorkId, Is.EqualTo("ol:OL1992198W"));
            Assert.That(BookIdentity.MatchesByStableWorkProviderId(local, remote), Is.True, "the normal refresh now updates the local row in place");
            Assert.That(other.OpenLibraryWorkId, Is.Null);
        }

        [Test]
        public void without_a_shared_isbn_a_unique_exact_title_matches()
        {
            var local = LocalFallbackBook(10, "Breakfast at Tiffany's", BookMediaType.Ebook, "ol:OL1992167W");
            var audiobook = RemoteBook("Breakfast at Tiffany's", BookMediaType.Audiobook, "hc:45874");
            var ebook = RemoteBook("Breakfast at Tiffany's", BookMediaType.Ebook, "hc:45874");

            TalearrFallbackReconciler.Attach(new[] { local }, new[] { audiobook, ebook });

            Assert.That(ebook.OpenLibraryWorkId, Is.EqualTo("ol:OL1992167W"));
            Assert.That(audiobook.OpenLibraryWorkId, Is.Null, "media types are never mixed");
        }

        [Test]
        public void ambiguous_or_missing_matches_change_nothing()
        {
            var local = LocalFallbackBook(10, "Collected Stories", BookMediaType.Audiobook, "ol:OL5W");
            var first = RemoteBook("Collected Stories", BookMediaType.Audiobook, "hc:1");
            var second = RemoteBook("Collected Stories", BookMediaType.Audiobook, "hc:2");
            var lonely = LocalFallbackBook(11, "A Title The Server Lacks", BookMediaType.Audiobook, "ol:OL6W");

            var attached = TalearrFallbackReconciler.Attach(new[] { local, lonely }, new[] { first, second });

            Assert.That(attached, Is.Zero);
            Assert.That(first.OpenLibraryWorkId, Is.Null);
            Assert.That(second.OpenLibraryWorkId, Is.Null);
        }

        [Test]
        public void books_that_already_have_a_server_id_are_left_to_the_normal_matching()
        {
            var local = LocalFallbackBook(10, "Breakfast at Tiffany's", BookMediaType.Audiobook, "ol:OL1992167W");
            local.HardcoverBookId = "hc:45874";
            var remote = RemoteBook("Breakfast at Tiffany's", BookMediaType.Audiobook, "hc:45874");

            Assert.That(TalearrFallbackReconciler.IsOpenLibraryOnly(local), Is.False);
            Assert.That(TalearrFallbackReconciler.Attach(new[] { local }, new[] { remote }), Is.Zero);
            Assert.That(BookIdentity.MatchesByStableWorkProviderId(local, remote), Is.True);
        }

        [Test]
        public void one_server_book_is_never_claimed_by_two_open_library_books()
        {
            var first = LocalFallbackBook(10, "In Cold Blood", BookMediaType.Audiobook, "ol:OL1W", "9780141038391");
            var second = LocalFallbackBook(11, "In Cold Blood", BookMediaType.Audiobook, "ol:OL2W", "9780141038391");
            var remote = RemoteBook("In Cold Blood", BookMediaType.Audiobook, "hc:1", "9780141038391");

            Assert.That(TalearrFallbackReconciler.Attach(new[] { first, second }, new[] { remote }), Is.EqualTo(1));
            Assert.That(remote.OpenLibraryWorkId, Is.EqualTo("ol:OL1W"));
        }

        private class LocalBooksProxy : DispatchProxy
        {
            public List<Book> Books { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IBookService.GetBooksByAuthor))
                {
                    return Books;
                }

                throw new InvalidOperationException($"refresh matching must not call IBookService.{targetMethod?.Name}");
            }
        }

        private class PassThroughProfiles : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case "Exists":
                        return true;
                    case "FilterBooks":
                        return ((Author)args[0]).Books;
                    default:
                        throw new InvalidOperationException($"not expected: {targetMethod?.Name}");
                }
            }
        }

        [Test]
        public void author_refresh_after_recovery_updates_the_open_library_row_instead_of_adding_a_duplicate()
        {
            var local = LocalFallbackBook(10, "In Cold Blood", BookMediaType.Audiobook, "ol:OL1992198W", "9780141038391");
            local.AuthorId = 7;
            local.BookFiles = new List<NzbDrone.Core.MediaFiles.BookFile>();

            var bookService = DispatchProxy.Create<IBookService, LocalBooksProxy>();
            ((LocalBooksProxy)(object)bookService).Books = new List<Book> { local };

            var service = new RefreshAuthorService(authorInfo: null,
                authorService: null,
                bookService: bookService,
                editionService: null,
                metadataProfileService: DispatchProxy.Create<NzbDrone.Core.Profiles.Metadata.IMetadataProfileService, PassThroughProfiles>(),
                refreshBookService: null,
                refreshSeriesService: null,
                eventAggregator: null,
                commandQueueManager: null,
                mediaFileService: null,
                historyService: null,
                rootFolderService: null,
                checkIfAuthorShouldBeRefreshed: null,
                monitorNewBookService: null,
                configService: null,
                importListExclusionService: null,
                syncMetadataService: null,
                syncQueueService: null,
                rootFolderSettingsResolver: null,
                logger: _logger);

            var remoteBook = RemoteBook("In Cold Blood", BookMediaType.Audiobook, "hc:1", "9780141038391");
            remoteBook.Editions[0].ReadingFormatId = 2;
            var remoteAuthor = new Author { Id = 7, Name = "Truman Capote", HardcoverAuthorId = "hc:136113", Books = new List<Book> { remoteBook } };
            var localAuthor = new Author { Id = 7, Name = "Truman Capote", HardcoverAuthorId = "hc:136113", AudiobookMetadataProfileId = 1 };

            var getRemoteChildren = typeof(RefreshAuthorService).GetMethod("GetRemoteChildren", BindingFlags.NonPublic | BindingFlags.Instance);
            var remoteChildren = (List<Book>)getRemoteChildren.Invoke(service, new object[] { localAuthor, remoteAuthor });

            Assert.That(remoteChildren.Single().OpenLibraryWorkId, Is.EqualTo("ol:OL1992198W"));

            var getMatching = typeof(RefreshAuthorService).GetMethod("GetMatchingExistingChildren", BindingFlags.NonPublic | BindingFlags.Instance);
            var match = (Tuple<Book, List<Book>>)getMatching.Invoke(service, new object[] { new List<Book> { local }, remoteChildren.Single() });

            Assert.That(match.Item1, Is.SameAs(local), "the existing row is updated in place");
            Assert.That(match.Item2, Is.Empty);
        }

        // ---- Settings, status and the HTTP client ----

        [Test]
        public void setting_defaults_to_when_primary_down_and_round_trips()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var settings = new TalearrMetadataFallbackSettingsService(repository, _logger);

            Assert.That(settings.GetMode(), Is.EqualTo(TalearrMetadataFallbackMode.WhenPrimaryDown));

            settings.SaveMode(TalearrMetadataFallbackMode.WhenPrimaryDownShowSource);
            Assert.That(proxy.Values[TalearrMetadataFallbackSettingsService.ModeKey], Is.EqualTo("whenPrimaryDownShowSource"));
            Assert.That(new TalearrMetadataFallbackSettingsService(repository, _logger).GetMode(), Is.EqualTo(TalearrMetadataFallbackMode.WhenPrimaryDownShowSource));

            proxy.Values[TalearrMetadataFallbackSettingsService.ModeKey] = "something else";
            Assert.That(new TalearrMetadataFallbackSettingsService(repository, _logger).GetMode(), Is.EqualTo(TalearrMetadataFallbackMode.WhenPrimaryDown));
        }

        [Test]
        public void health_check_names_the_active_source_and_the_last_use()
        {
            var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            var down = TalearrMetadataFallbackCheck.GetMessage(new TalearrMetadataFallbackStatus
            {
                Mode = TalearrMetadataFallbackMode.WhenPrimaryDown,
                PrimaryUnavailable = true,
                LastUsed = now.AddMinutes(-5),
                LastOperation = "search: capote"
            }, now);

            Assert.That(down, Does.Contain("using Open Library"));
            Assert.That(down, Does.Contain("2026-10-17 11:55 UTC (search: capote)"));
            Assert.That(down, Does.Not.Contain("\u2014"));

            Assert.That(TalearrMetadataFallbackCheck.GetMessage(new TalearrMetadataFallbackStatus { Mode = TalearrMetadataFallbackMode.WhenPrimaryDown }, now), Is.Null);
            Assert.That(TalearrMetadataFallbackCheck.GetMessage(new TalearrMetadataFallbackStatus { Mode = TalearrMetadataFallbackMode.Off, PrimaryUnavailable = true }, now), Is.Null);
            Assert.That(TalearrMetadataFallbackCheck.GetMessage(new TalearrMetadataFallbackStatus
            {
                Mode = TalearrMetadataFallbackMode.WhenPrimaryDown,
                LastUsed = now.AddHours(-2),
                LastOperation = "add: author hc:1"
            }, now), Does.Contain("The metadata server is back"));
        }

        [Test]
        public void open_library_client_is_polite_caches_and_separates_not_found_from_errors()
        {
            var ok = new StatusHttpClient(HttpStatusCode.OK, OpenLibraryFixtures.Author);
            var client = new OpenLibraryClient(ok, new CacheManager(), _logger);

            var first = client.GetAuthor("OL239800A");
            var second = client.GetAuthor("/authors/OL239800A");

            Assert.That(first.Name, Is.EqualTo("Truman Capote"));
            Assert.That(second.Name, Is.EqualTo("Truman Capote"));
            Assert.That(ok.Requests, Has.Count.EqualTo(1), "the second call is served from the cache");

            var request = ok.Requests[0];
            Assert.That(request.Url.FullUri, Is.EqualTo("https://openlibrary.org/authors/OL239800A.json"));
            Assert.That(request.Headers.GetSingleValue("User-Agent"), Does.StartWith("Talearr/"));
            Assert.That(request.RateLimit, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(request.RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(15)));

            Assert.That(new OpenLibraryClient(new StatusHttpClient(HttpStatusCode.NotFound), new CacheManager(), _logger).GetAuthor("OL1A"), Is.Null);
            Assert.Throws<OpenLibraryException>(() => new OpenLibraryClient(new StatusHttpClient(HttpStatusCode.ServiceUnavailable), new CacheManager(), _logger).GetAuthor("OL1A"));
            Assert.That(client.GetAuthor("not-a-key"), Is.Null);
        }
    }
}
