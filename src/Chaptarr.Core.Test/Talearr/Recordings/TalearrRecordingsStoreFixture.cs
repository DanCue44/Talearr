using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrRecordingsStoreFixture
    {
        private TalearrTestFolder _folder;
        private TalearrRecordingsStore _store;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = _folder.CreateStore();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void should_round_trip_an_audible_product()
        {
            var fetched = new DateTime(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc);
            _store.UpsertProducts(new[]
            {
                new AudibleProduct
                {
                    Marketplace = "US",
                    Asin = "b002v5bslc",
                    Status = AudibleProductStatus.Ok,
                    Title = "The Firm",
                    FormatType = "Unabridged",
                    RuntimeMinutes = 1030,
                    Narrators = new List<string> { "Scott Brick" },
                    Authors = new List<string> { "John Grisham" },
                    Publisher = "Random House Audio",
                    ReleaseDate = "2009-09-16",
                    Language = "english",
                    Source = AudibleProduct.SourceAudible,
                    FetchedAt = fetched
                }
            });

            var product = _store.GetProduct("us", "B002V5BSLC");

            Assert.Multiple(() =>
            {
                Assert.That(product, Is.Not.Null);
                Assert.That(product.Marketplace, Is.EqualTo("us"));
                Assert.That(product.Asin, Is.EqualTo("B002V5BSLC"));
                Assert.That(product.FormatType, Is.EqualTo("unabridged"));
                Assert.That(product.Abridged, Is.EqualTo(AbridgedMarker.Unabridged));
                Assert.That(product.RuntimeMinutes, Is.EqualTo(1030));
                Assert.That(product.Narrators, Is.EqualTo(new[] { "Scott Brick" }));
                Assert.That(product.Authors, Is.EqualTo(new[] { "John Grisham" }));
                Assert.That(product.Publisher, Is.EqualTo("Random House Audio"));
                Assert.That(product.ReleaseDate, Is.EqualTo("2009-09-16"));
                Assert.That(product.FetchedAt, Is.EqualTo(fetched));
                Assert.That(product.FetchedAt.Kind, Is.EqualTo(DateTimeKind.Utc));
            });
        }

        [Test]
        public void should_keep_marketplaces_apart_and_replace_on_upsert()
        {
            _store.UpsertProducts(new[]
            {
                Product("us", "B00NIXU8GW", AudibleProductStatus.NotFound),
                Product("uk", "B00NIXU8GW", AudibleProductStatus.Ok)
            });

            Assert.That(_store.GetProduct("us", "B00NIXU8GW").Status, Is.EqualTo(AudibleProductStatus.NotFound));
            Assert.That(_store.GetProduct("gb", "B00NIXU8GW").Status, Is.EqualTo(AudibleProductStatus.Ok), "gb is an alias of uk");

            _store.UpsertProducts(new[] { Product("us", "B00NIXU8GW", AudibleProductStatus.RegionUnavailable) });

            Assert.That(_store.GetProduct("us", "B00NIXU8GW").Status, Is.EqualTo(AudibleProductStatus.RegionUnavailable));
        }

        [Test]
        public void should_read_many_products_in_one_call()
        {
            var asins = Enumerable.Range(0, 450).Select(i => $"B{i:D9}").ToList();
            _store.UpsertProducts(asins.Select(a => Product("us", a, AudibleProductStatus.Ok)));

            var found = _store.GetProducts("us", asins.Concat(new[] { "B999999999", null, " " }));

            Assert.That(found, Has.Count.EqualTo(450));
        }

        [Test]
        public void should_round_trip_edition_links_including_no_match()
        {
            var checkedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            _store.UpsertLink(new EditionAudibleLink
            {
                ForeignEditionId = "hc:edition:30583648-audiobook",
                Marketplace = "us",
                Asin = "B002V5BSLC",
                Method = EditionAudibleLink.MethodAsinsList,
                Confidence = 0.9,
                EditionIdHint = 28701,
                CheckedAt = checkedAt
            });
            _store.UpsertLink(new EditionAudibleLink
            {
                ForeignEditionId = "hc:edition:699892-audiobook",
                Marketplace = "us",
                Asin = null,
                Method = EditionAudibleLink.MethodSearch,
                Confidence = 0,
                CheckedAt = checkedAt
            });

            var links = _store.GetLinks(new[] { "hc:edition:30583648-audiobook", "hc:edition:699892-audiobook", "missing" }, "us");

            Assert.That(links, Has.Count.EqualTo(2));
            var matched = links.Single(l => l.Matched);
            Assert.Multiple(() =>
            {
                Assert.That(matched.Asin, Is.EqualTo("B002V5BSLC"));
                Assert.That(matched.Method, Is.EqualTo(EditionAudibleLink.MethodAsinsList));
                Assert.That(matched.Confidence, Is.EqualTo(0.9).Within(0.0001));
                Assert.That(matched.EditionIdHint, Is.EqualTo(28701));
                Assert.That(matched.CheckedAt, Is.EqualTo(checkedAt));
                Assert.That(links.Single(l => !l.Matched).Asin, Is.Null);
                Assert.That(_store.GetLink("hc:edition:30583648-audiobook", "uk"), Is.Null, "links are per marketplace");
            });
        }

        [Test]
        public void should_record_recording_requests()
        {
            var first = _store.AddRequest(new RecordingRequest { BaseBookId = "hc:378630", Kind = "unabridged", RequestedBy = "allseerr", ExternalRef = "req-812", CopyBookIdHint = 9001 });
            _store.AddRequest(new RecordingRequest { BaseBookId = "hc:378630", Kind = "narrator", RequestedBy = "ui" });
            _store.AddRequest(new RecordingRequest { BaseBookId = "hc:1", Kind = "edition" });

            var requests = _store.GetRequests("hc:378630");

            Assert.That(first.Id, Is.GreaterThan(0));
            Assert.That(requests.Select(r => r.Kind), Is.EqualTo(new[] { "unabridged", "narrator" }));
            Assert.That(requests[0].ExternalRef, Is.EqualTo("req-812"));
            Assert.That(requests[0].CopyBookIdHint, Is.EqualTo(9001));
        }

        [Test]
        public void should_prune_rows_older_than_the_cutoff()
        {
            var old = Product("us", "B000000001", AudibleProductStatus.Ok);
            old.FetchedAt = DateTime.UtcNow.AddDays(-400);
            _store.UpsertProducts(new[] { old, Product("us", "B000000002", AudibleProductStatus.Ok) });

            var removed = _store.PruneOlderThan(DateTime.UtcNow.AddDays(-180));

            Assert.That(removed, Is.EqualTo(1));
            Assert.That(_store.GetProduct("us", "B000000001"), Is.Null);
            Assert.That(_store.GetProduct("us", "B000000002"), Is.Not.Null);
        }

        private static AudibleProduct Product(string marketplace, string asin, AudibleProductStatus status)
        {
            return new AudibleProduct
            {
                Marketplace = marketplace,
                Asin = asin,
                Status = status,
                Title = status == AudibleProductStatus.Ok ? "Title" : null,
                FetchedAt = DateTime.UtcNow
            };
        }
    }
}
