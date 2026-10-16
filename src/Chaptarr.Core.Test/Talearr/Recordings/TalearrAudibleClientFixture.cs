using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.Audible;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrAudibleClientFixture
    {
        private static readonly string[] FirmUsAsins =
        {
            "0739366203", "B002PE36VO", "B002V5BSLC", "B004FU0XQ2", "B004UZWZ18", "B008QT29CE",
            "B00FG9FPRI", "B072BTP7BD", "B079VB1PFQ", "B079VH2X33", "B07FB4HFD8", "B0BTYTFG28"
        };

        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        }

        private TalearrAudibleClient Client(FakeAudibleHttp fake, NzbDrone.Common.Http.IHttpClient http)
        {
            return new TalearrAudibleClient(http, LogManager.GetCurrentClassLogger(), () => _now);
        }

        [Test]
        public void should_look_up_many_asins_in_one_request_and_mark_stubs_not_found()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("api.audible.com/1.0/catalog/products?asins=", AudibleRecordings.UsBatch);

            var result = Client(fake, http).GetProducts("us", FirmUsAsins);

            Assert.That(fake.Requests, Has.Count.EqualTo(1));
            Assert.That(result.Answered, Is.True);

            var current = result.Products.Single(p => p.Asin == "B002V5BSLC");
            Assert.Multiple(() =>
            {
                Assert.That(current.Status, Is.EqualTo(AudibleProductStatus.Ok));
                Assert.That(current.Abridged, Is.EqualTo(AbridgedMarker.Unabridged));
                Assert.That(current.RuntimeMinutes, Is.EqualTo(1030));
                Assert.That(current.Narrators, Is.EqualTo(new[] { "Scott Brick" }));
                Assert.That(current.Publisher, Is.EqualTo("Random House Audio"));
                Assert.That(current.ReleaseDate, Is.EqualTo("2009-09-16"));
                Assert.That(current.Marketplace, Is.EqualTo("us"));
                Assert.That(current.FetchedAt, Is.EqualTo(_now));

                // The stored ASIN of the monitored edition is stale in the US store.
                Assert.That(result.Products.Single(p => p.Asin == "B002PE36VO").Status, Is.EqualTo(AudibleProductStatus.NotFound));
                Assert.That(result.Products, Has.Count.EqualTo(FirmUsAsins.Length));
            });
        }

        [Test]
        public void should_split_large_lookups_into_batches()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("catalog/products?asins=", "{\"products\":[]}");
            var asins = Enumerable.Range(0, 60).Select(i => $"B{i:D9}").ToList();

            var result = Client(fake, http).GetProducts("us", asins);

            Assert.That(fake.Requests, Has.Count.EqualTo(3), "25 per request");
            Assert.That(result.Products.All(p => p.Status == AudibleProductStatus.NotFound), Is.True);
        }

        [Test]
        public void should_use_the_marketplace_host_and_headers()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("api.audible.co.uk/1.0/catalog/products?asins=", AudibleRecordings.UkBatch);

            var result = Client(fake, http).GetProducts("uk", new[] { "B00NIXGHQ2", "B00NIXKS5S", "B00NIXU8GW", "B07BC4WMF8" });

            var request = fake.Requests.Single();
            Assert.Multiple(() =>
            {
                Assert.That(request.Url.Host, Is.EqualTo("api.audible.co.uk"));
                Assert.That(request.Headers.GetSingleValue("Accept-Language"), Does.StartWith("en-GB"));
                Assert.That(request.Headers.GetSingleValue("Referer"), Is.EqualTo("https://www.audible.co.uk/"));
                Assert.That(AudibleCatalogProxy.UserAgents, Does.Contain(request.Headers.GetSingleValue("User-Agent")), "same agents as upstream's Audible calls");
                Assert.That(request.RateLimit, Is.EqualTo(TimeSpan.FromSeconds(1)), "at most one request per second");
                Assert.That(result.Products.Single(p => p.Found).Asin, Is.EqualTo("B00NIXU8GW"));
                Assert.That(result.Products.Single(p => p.Found).Publisher, Is.EqualTo("Hodder & Stoughton"));
            });
        }

        [Test]
        public void should_keep_one_user_agent_for_the_process()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("catalog/products", "{\"products\":[]}");
            var client = Client(fake, http);

            client.GetProducts("us", new[] { "B002V5BSLC" });
            client.Search("us", "The Firm", "John Grisham");
            client.GetProducts("de", new[] { "B002V5BSLC" });

            Assert.That(fake.Requests.Select(r => r.Headers.GetSingleValue("User-Agent")).Distinct().Count(), Is.EqualTo(1));
        }

        [Test]
        public void should_search_by_title_and_author()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("catalog/products?title=", AudibleRecordings.UsSearchTheFirm);

            var result = Client(fake, http).Search("us", "The Firm", "John Grisham");

            Assert.That(fake.Requests.Single().Url.FullUri, Does.Contain("title=The%20Firm").And.Contain("author=John%20Grisham"));
            Assert.That(result.Products, Has.Count.EqualTo(7));
            Assert.That(result.Products.Single(p => p.Asin == "B00TWO6SJM").Abridged, Is.EqualTo(AbridgedMarker.Abridged));
        }

        [TestCase(HttpStatusCode.TooManyRequests)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        public void should_back_off_after_throttling_and_not_retry_until_it_expires(HttpStatusCode status)
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("catalog/products", "slow down", status);
            var client = Client(fake, http);

            var first = client.GetProducts("us", new[] { "B002V5BSLC" });
            var second = client.GetProducts("us", new[] { "B002V5BSLC" });

            Assert.That(first.Throttled, Is.True);
            Assert.That(first.Products, Is.Empty, "a throttled answer must not be cached as not found");
            Assert.That(second.Throttled, Is.True);
            Assert.That(fake.Requests, Has.Count.EqualTo(1), "no request while backing off");
            Assert.That(client.IsBackingOff("us"), Is.True);
            Assert.That(client.IsBackingOff("uk"), Is.False, "back-off is per host");

            _now = _now.AddMinutes(1).AddSeconds(1);
            client.GetProducts("us", new[] { "B002V5BSLC" });
            Assert.That(fake.Requests, Has.Count.EqualTo(2));

            // Second throttle in a row doubles the pause.
            _now = _now.AddMinutes(1).AddSeconds(1);
            client.GetProducts("us", new[] { "B002V5BSLC" });
            Assert.That(fake.Requests, Has.Count.EqualTo(2));
            _now = _now.AddMinutes(1);
            client.GetProducts("us", new[] { "B002V5BSLC" });
            Assert.That(fake.Requests, Has.Count.EqualTo(3));
        }

        [Test]
        public void should_follow_retry_after_and_clear_the_back_off_on_success()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.Routes.Add(("catalog/products", HttpStatusCode.TooManyRequests, "", new Dictionary<string, string> { ["Retry-After"] = "600" }));
            var client = Client(fake, http);

            client.GetProducts("us", new[] { "B002V5BSLC" });
            _now = _now.AddMinutes(5);
            Assert.That(client.IsBackingOff("us"), Is.True);

            _now = _now.AddMinutes(6);
            fake.Routes.Clear();
            fake.On("catalog/products", AudibleRecordings.UsBatch);
            var result = client.GetProducts("us", new[] { "B002V5BSLC" });

            Assert.That(result.Answered, Is.True);
            Assert.That(client.IsBackingOff("us"), Is.False);
        }

        [Test]
        public void should_report_failures_without_throwing()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("catalog/products", "<html>oops</html>", HttpStatusCode.InternalServerError);

            var result = Client(fake, http).GetProducts("us", new[] { "B002V5BSLC" });

            Assert.That(result.Failed, Is.True);
            Assert.That(result.Products, Is.Empty);
        }

        [Test]
        public void should_skip_values_that_are_not_asins()
        {
            var (http, fake) = FakeAudibleHttp.Create();

            var result = Client(fake, http).GetProducts("us", new[] { null, "", "9780739366202", "short" });

            Assert.That(fake.Requests, Is.Empty);
            Assert.That(result.Products, Is.Empty);
        }

        [Test]
        public void should_read_audnexus_products_and_region_errors()
        {
            var (http, fake) = FakeAudibleHttp.Create();
            fake.On("api.audnex.us/books/B002V5BSLC?region=us", AudibleRecordings.AudnexusB002V5BSLC);
            fake.On("api.audnex.us/books/B002PE36VO?region=us", AudibleRecordings.AudnexusRegionUnavailable, HttpStatusCode.NotFound);
            var client = Client(fake, http);

            var ok = client.GetFromAudnexus("us", "B002V5BSLC").Products.Single();
            var region = client.GetFromAudnexus("us", "B002PE36VO").Products.Single();

            Assert.Multiple(() =>
            {
                Assert.That(ok.Source, Is.EqualTo(AudibleProduct.SourceAudnexus));
                Assert.That(ok.Abridged, Is.EqualTo(AbridgedMarker.Unabridged));
                Assert.That(ok.RuntimeMinutes, Is.EqualTo(1030));
                Assert.That(ok.Narrators, Is.EqualTo(new[] { "Scott Brick" }));
                Assert.That(ok.ReleaseDate, Is.EqualTo("2009-09-16"));
                Assert.That(region.Status, Is.EqualTo(AudibleProductStatus.RegionUnavailable));
                Assert.That(fake.Requests.All(r => r.Headers.GetSingleValue("Origin") == null), Is.True, "no browser headers to Audnexus");
            });
        }
    }
}
