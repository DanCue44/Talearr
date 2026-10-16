using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrAudibleFactsFixture
    {
        private TalearrTestFolder _folder;
        private TalearrRecordingsStore _store;
        private FakeAudibleHttp _fake;
        private TalearrRecordingsSettingsService _settings;
        private TalearrAudibleFactsService _service;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = _folder.CreateStore();
            _now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

            var (http, fake) = FakeAudibleHttp.Create();
            _fake = fake;
            var client = new TalearrAudibleClient(http, LogManager.GetCurrentClassLogger(), () => _now);

            var (repository, _) = ConfigRepositoryProxy.Create();
            _settings = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());
            _service = new TalearrAudibleFactsService(client, _store, _settings, LogManager.GetCurrentClassLogger(), () => _now);
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private void RecordedUs()
        {
            _fake.On("api.audible.com/1.0/catalog/products?asins=", AudibleRecordings.UsBatch);
            _fake.On("api.audible.com/1.0/catalog/products?title=", AudibleRecordings.UsSearchTheFirm);
        }

        [Test]
        public void stale_stored_us_asin_resolves_through_the_asin_list()
        {
            RecordedUs();
            var edition = FirmEditions.ScottBrickUs();

            var summary = _service.Resolve(new[] { edition }, FirmEditions.BookTitle, FirmEditions.Author);
            var facts = _service.GetCachedFacts(new[] { edition }, "us")[edition.Id];

            Assert.Multiple(() =>
            {
                Assert.That(summary.Matched, Is.EqualTo(1));
                Assert.That(summary.Searches, Is.EqualTo(0), "the ASIN list answered, no search needed");
                Assert.That(_fake.CountTo("asins="), Is.EqualTo(1));
                Assert.That(facts.Link.Asin, Is.EqualTo("B002V5BSLC"));
                Assert.That(facts.Link.Method, Is.EqualTo(EditionAudibleLink.MethodAsinsList));
                Assert.That(facts.Link.EditionIdHint, Is.EqualTo(28701));
                Assert.That(facts.Product.Abridged, Is.EqualTo(AbridgedMarker.Unabridged));
                Assert.That(facts.Product.RuntimeMinutes, Is.EqualTo(1030));
            });
        }

        [Test]
        public void uk_asin_resolves_in_the_uk_marketplace()
        {
            _settings.Save(new TalearrRecordingsSettings { Marketplace = "uk" });
            _fake.On("api.audible.co.uk/1.0/catalog/products?asins=", AudibleRecordings.UkBatch);
            var edition = FirmEditions.ScottBrickUk();

            _service.Resolve(new[] { edition }, FirmEditions.BookTitle, FirmEditions.Author);
            var facts = _service.GetCachedFacts(new[] { edition })[edition.Id];

            Assert.That(facts.Link.Method, Is.EqualTo(EditionAudibleLink.MethodStoredAsin));
            Assert.That(facts.Link.Confidence, Is.EqualTo(1.0));
            Assert.That(facts.Product.Publisher, Is.EqualTo("Hodder & Stoughton"));
            Assert.That(_service.GetCachedFacts(new[] { edition }, "us")[edition.Id].Link, Is.Null, "facts are per marketplace");
        }

        [Test]
        public void abridged_reading_without_ids_gets_no_link_from_a_search_that_only_finds_other_narrators()
        {
            RecordedUs();
            var moffett = FirmEditions.MoffettAbridged();

            var summary = _service.Resolve(new[] { moffett }, FirmEditions.BookTitle, FirmEditions.Author);
            var facts = _service.GetCachedFacts(new[] { moffett }, "us")[moffett.Id];

            Assert.That(summary.Searches, Is.EqualTo(1));
            Assert.That(facts.Link, Is.Not.Null, "the miss is remembered");
            Assert.That(facts.Link.Matched, Is.False);
            Assert.That(facts.Product, Is.Null);
        }

        [Test]
        public void one_batch_and_one_search_serve_every_edition_of_the_book()
        {
            RecordedUs();

            _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            Assert.That(_fake.Requests, Has.Count.EqualTo(2));
            Assert.That(_fake.CountTo("asins="), Is.EqualTo(1));
            Assert.That(_fake.CountTo("title="), Is.EqualTo(1));
        }

        [Test]
        public void a_second_run_inside_the_cache_window_makes_no_requests()
        {
            RecordedUs();
            _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);
            var before = _fake.Requests.Count;

            _now = _now.AddDays(6);
            var summary = _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            Assert.That(_fake.Requests.Count, Is.EqualTo(before));
            Assert.That(summary.Skipped, Is.EqualTo(4));
        }

        [Test]
        public void misses_expire_after_seven_days_and_matches_after_thirty()
        {
            RecordedUs();
            _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            _now = _now.AddDays(8);
            var summary = _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            Assert.That(summary.Skipped, Is.EqualTo(2), "the two matched editions are still fresh");
            Assert.That(summary.Editions - summary.Skipped, Is.EqualTo(2));
        }

        [Test]
        public void force_ignores_the_cache()
        {
            RecordedUs();
            _service.Resolve(new[] { FirmEditions.ScottBrickUs() }, FirmEditions.BookTitle, FirmEditions.Author);

            _service.Resolve(new[] { FirmEditions.ScottBrickUs() }, FirmEditions.BookTitle, FirmEditions.Author, new AudibleResolveOptions { Force = true });

            Assert.That(_fake.CountTo("asins="), Is.EqualTo(2));
        }

        [Test]
        public void search_fallback_finds_the_current_product_when_no_asin_answers()
        {
            RecordedUs();
            var edition = FirmEditions.ScottBrickUs();
            edition.Asins = new List<string> { "B002PE36VO" };
            edition.Asin = null;

            _service.Resolve(new[] { edition }, FirmEditions.BookTitle, FirmEditions.Author);
            var facts = _service.GetCachedFacts(new[] { edition }, "us")[edition.Id];

            Assert.That(facts.Link.Method, Is.EqualTo(EditionAudibleLink.MethodSearch));
            Assert.That(facts.Link.Asin, Is.EqualTo("B002V5BSLC"));
            Assert.That(facts.Link.Confidence, Is.GreaterThanOrEqualTo(TalearrAudibleMatcher.SearchConfidenceThreshold));
        }

        [Test]
        public void throttling_leaves_no_negative_rows_behind()
        {
            _fake.On("api.audible.com", "", HttpStatusCode.TooManyRequests);

            var summary = _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            Assert.That(summary.Throttled, Is.True);
            Assert.That(_service.GetCachedFacts(FirmEditions.All(), "us").Values.All(f => f.Link == null), Is.True);
            Assert.That(_store.GetProduct("us", "B002PE36VO"), Is.Null);
        }

        [Test]
        public void audnexus_is_not_asked_unless_the_fallback_is_on()
        {
            _fake.On("api.audible.com", "", HttpStatusCode.ServiceUnavailable);
            _fake.On("api.audnex.us/books/B002V5BSLC", AudibleRecordings.AudnexusB002V5BSLC);
            _fake.On("api.audnex.us/books/", AudibleRecordings.AudnexusRegionUnavailable, HttpStatusCode.NotFound);

            _service.Resolve(new[] { FirmEditions.ScottBrickUs() }, FirmEditions.BookTitle, FirmEditions.Author);
            Assert.That(_fake.CountTo("audnex.us"), Is.EqualTo(0));
        }

        [Test]
        public void audnexus_fallback_answers_when_audible_is_throttling()
        {
            _settings.Save(new TalearrRecordingsSettings { AudnexusFallback = true });
            _fake.On("api.audible.com", "", HttpStatusCode.ServiceUnavailable);
            _fake.On("api.audnex.us/books/B002V5BSLC", AudibleRecordings.AudnexusB002V5BSLC);
            _fake.On("api.audnex.us/books/", AudibleRecordings.AudnexusRegionUnavailable, HttpStatusCode.NotFound);
            var edition = FirmEditions.ScottBrickUs();

            _service.Resolve(new[] { edition }, FirmEditions.BookTitle, FirmEditions.Author);
            var facts = _service.GetCachedFacts(new[] { edition }, "us")[edition.Id];

            Assert.That(facts.Product?.Source, Is.EqualTo(AudibleProduct.SourceAudnexus));
            Assert.That(facts.Product.Abridged, Is.EqualTo(AbridgedMarker.Unabridged));
            Assert.That(_store.GetProduct("us", "B002PE36VO").Status, Is.EqualTo(AudibleProductStatus.RegionUnavailable));
            Assert.That(_fake.CountTo("audnex.us"), Is.LessThanOrEqualTo(10), "Audnexus calls are capped");
        }

        [Test]
        public void provider_off_makes_no_requests()
        {
            _settings.Save(new TalearrRecordingsSettings { Provider = TalearrAudibleProvider.Off });

            _service.Resolve(FirmEditions.All(), FirmEditions.BookTitle, FirmEditions.Author);

            Assert.That(_fake.Requests, Is.Empty);
        }

        [Test]
        public void cached_facts_never_touch_the_network_and_skip_editions_without_identity()
        {
            var noId = new Edition { Id = 5, Title = "x" };

            var facts = _service.GetCachedFacts(new[] { noId, FirmEditions.ScottBrickUs() }, "us");

            Assert.That(_fake.Requests, Is.Empty);
            Assert.That(facts.Keys, Is.EquivalentTo(new[] { 5, 28701 }));
            Assert.That(facts.Values.All(f => !f.HasProduct), Is.True);
        }
    }
}
