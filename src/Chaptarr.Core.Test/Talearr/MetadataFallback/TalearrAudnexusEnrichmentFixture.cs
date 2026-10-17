using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace Chaptarr.Core.Test.Talearr.MetadataFallback
{
    [TestFixture]
    public class TalearrAudnexusEnrichmentFixture
    {
        // Recorded from api.audnex.us/books/{asin} (trimmed to the fields TaleArr reads plus a few others).
        private const string InColdBlood = @"{""asin"":""B002V1BPPW"",""authors"":[{""asin"":""B000APJ0OO"",""name"":""Truman Capote""}],
            ""description"":""On November 15, 1959..."",""formatType"":""unabridged"",""genres"":[{""asin"":""18574426011"",""name"":""History"",""type"":""genre""}],
            ""image"":""https://m.media-amazon.com/images/I/51-in-cold-blood.jpg"",""isAdult"":false,""language"":""english"",""literatureType"":""nonfiction"",
            ""narrators"":[{""name"":""Scott  Brick""}],""publisherName"":""Random House Audio"",""rating"":""4.5"",""region"":""us"",
            ""releaseDate"":""2006-01-03T00:00:00.000Z"",""runtimeLengthMin"":868,""subtitle"":null,""title"":""In Cold Blood""}";

        private const string BreakfastAbridged = @"{""asin"":""B00ABRIDG1"",""authors"":[{""name"":""Truman Capote""}],""formatType"":""abridged"",
            ""narrators"":[{""name"":""Michael C. Hall""}],""runtimeLengthMin"":200,""title"":""Breakfast at Tiffany's"",""releaseDate"":""2012-05-01T00:00:00.000Z""}";

        private const string WrongAuthor = @"{""asin"":""B00WRONG01"",""authors"":[{""name"":""Somebody Else""}],""formatType"":""unabridged"",""title"":""In Cold Blood"",""runtimeLengthMin"":100}";

        private sealed class FakeAudnexus : IAudnexusClient
        {
            public Dictionary<string, string> Products { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> Calls { get; } = new();
            public bool Fail { get; set; }

            public AudnexusBook GetBook(string asin)
            {
                Calls.Add(asin);
                if (Fail)
                {
                    throw new AudnexusException("Audnexus returned 503 ServiceUnavailable");
                }

                return Products.TryGetValue(asin, out var json) ? AudnexusBook.Parse(JToken.Parse(json)) : null;
            }
        }

        private static List<OpenLibraryWorkDoc> Works(params (string Key, string Title, string[] Amazon)[] works)
        {
            return works.Select(w => new OpenLibraryWorkDoc
            {
                Key = w.Key,
                Title = w.Title,
                EditionKeys = new List<string> { "OL1M" },
                Languages = new List<string> { "eng" },
                AuthorKeys = new List<string> { "OL239800A" },
                AmazonIds = w.Amazon.ToList()
            }).ToList();
        }

        private static NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorResponse Build(List<OpenLibraryWorkDoc> works)
        {
            return TalearrOpenLibraryMapper.BuildAuthorResponse(OpenLibraryFixtures.AuthorRecord(), works, "ol:OL239800A");
        }

        [Test]
        public void audnexus_json_is_parsed()
        {
            var book = AudnexusBook.Parse(JToken.Parse(InColdBlood));

            Assert.Multiple(() =>
            {
                Assert.That(book.Asin, Is.EqualTo("B002V1BPPW"));
                Assert.That(book.Narrators, Is.EqualTo(new[] { "Scott Brick" }), "whitespace collapsed");
                Assert.That(book.RuntimeMinutes, Is.EqualTo(868));
                Assert.That(book.Abridged, Is.False);
                Assert.That(book.ReleaseDate, Is.EqualTo(new DateTime(2006, 1, 3, 0, 0, 0, DateTimeKind.Utc)));
                Assert.That(AudnexusBook.Parse(JToken.Parse(BreakfastAbridged)).Abridged, Is.True);
                Assert.That(AudnexusBook.Parse(JToken.Parse(@"{""asin"":""0451154460"",""title"":""x""}")), Is.Null, "not an Audible ASIN");
            });
        }

        [Test]
        public void open_library_amazon_ids_are_read_from_search_docs()
        {
            var doc = OpenLibraryWorkDoc.Parse(JToken.Parse(@"{""key"":""/works/OL1992198W"",""title"":""In Cold Blood"",""id_amazon"":[""0451154460"",""B002V1BPPW""]}"));

            Assert.That(doc.AmazonIds, Is.EqualTo(new[] { "0451154460", "B002V1BPPW" }));
        }

        [Test]
        public void audiobook_rows_get_narrator_runtime_and_abridgement_from_audnexus()
        {
            var works = Works(("OL1992198W", "In Cold Blood", new[] { "0451154460", "B002V1BPPW" }),
                              ("OL1992167W", "Breakfast at Tiffany's", new[] { "B00ABRIDG1" }));
            var response = Build(works);
            var audnexus = new FakeAudnexus();
            audnexus.Products["B002V1BPPW"] = InColdBlood;
            audnexus.Products["B00ABRIDG1"] = BreakfastAbridged;

            var added = TalearrAudnexusEnricher.Enrich(response, works, audnexus);

            var blood = response.Books.Single(b => b.Title == "In Cold Blood");
            var audio = blood.Editions.Single(e => e.IsAudiobook);
            var print = blood.Editions.Single(e => e.IsPhysical);

            Assert.Multiple(() =>
            {
                Assert.That(added, Is.EqualTo(2));
                Assert.That(audnexus.Calls, Is.EqualTo(new[] { "B002V1BPPW", "B00ABRIDG1" }), "ISBN-10 amazon ids are never sent");
                Assert.That(audio.Id, Is.EqualTo("az:B002V1BPPW"));
                Assert.That(audio.Asin, Is.EqualTo("B002V1BPPW"));
                Assert.That(audio.NarratorNames, Is.EqualTo(new[] { "Scott Brick" }));
                Assert.That(audio.DurationSeconds, Is.EqualTo(868 * 60));
                Assert.That(audio.EditionInfo, Is.EqualTo("Unabridged"));
                Assert.That(audio.LanguageCode, Is.EqualTo(print.LanguageCode), "same language bucket as the print edition");
                Assert.That(blood.HasAudiobook, Is.True);
                Assert.That(print.Id, Does.StartWith("ol:"), "the print edition is untouched");
                Assert.That(response.Books.Single(b => b.Title == "Breakfast at Tiffany's").Editions.Single(e => e.IsAudiobook).EditionInfo, Is.EqualTo("Abridged"));
            });
        }

        [Test]
        public void a_product_with_another_title_or_author_is_ignored()
        {
            var works = Works(("OL1992198W", "In Cold Blood", new[] { "B00WRONG01" }), ("OL1992167W", "Breakfast at Tiffany's", new[] { "B002V1BPPW" }));
            var response = Build(works);
            var audnexus = new FakeAudnexus();
            audnexus.Products["B00WRONG01"] = WrongAuthor;
            audnexus.Products["B002V1BPPW"] = InColdBlood;

            Assert.That(TalearrAudnexusEnricher.Enrich(response, works, audnexus), Is.Zero);
            Assert.That(response.Books.SelectMany(b => b.Editions).Any(e => e.IsAudiobook), Is.False);
        }

        [Test]
        public void an_audnexus_outage_stops_enrichment_without_failing_the_add()
        {
            var works = Works(("OL1992198W", "In Cold Blood", new[] { "B002V1BPPW" }), ("OL1992167W", "Breakfast at Tiffany's", new[] { "B00ABRIDG1" }));
            var response = Build(works);
            var audnexus = new FakeAudnexus { Fail = true };

            Assert.That(TalearrAudnexusEnricher.Enrich(response, works, audnexus), Is.Zero);
            Assert.That(audnexus.Calls, Has.Count.EqualTo(1), "one failure stops the rest");
            Assert.That(response.Books, Has.Count.EqualTo(2));
        }

        [Test]
        public void lookups_are_capped_per_author()
        {
            var works = Enumerable.Range(0, 30)
                .Select(i => ($"OL{100 + i}W", $"Book {i}", new[] { $"B0{i:D8}" }))
                .ToArray();
            var docs = Works(works);
            var response = Build(docs);
            var audnexus = new FakeAudnexus();

            TalearrAudnexusEnricher.Enrich(response, docs, audnexus);

            Assert.That(audnexus.Calls, Has.Count.EqualTo(TalearrAudnexusEnricher.MaxLookupsPerAuthor));
        }

        [Test]
        public void fallback_add_uses_audnexus_when_given()
        {
            var openLibrary = new FakeOpenLibraryClient();
            var audnexus = new FakeAudnexus();
            var service = new TalearrMetadataFallbackService(new FakeFallbackSettings(), openLibrary, new FakeProbe(), null, null, LogManager.GetCurrentClassLogger(), audnexus);

            var response = service.GetAuthor("ol:OL239800A", null);

            Assert.That(response.Books, Is.Not.Empty);
            Assert.That(response.Books.SelectMany(b => b.Editions).Any(e => e.IsAudiobook), Is.False, "the fixture works list no Audible ASIN, so nothing is called");
            Assert.That(audnexus.Calls, Is.Empty);
        }

        [Test]
        public void client_is_polite_caches_and_treats_unknown_asins_as_not_found()
        {
            var ok = new StatusHttpClient(HttpStatusCode.OK, InColdBlood);
            var client = new AudnexusClient(ok, new CacheManager(), LogManager.GetCurrentClassLogger());

            var first = client.GetBook("b002v1bppw");
            var second = client.GetBook("B002V1BPPW");

            Assert.Multiple(() =>
            {
                Assert.That(first.Title, Is.EqualTo("In Cold Blood"));
                Assert.That(second.Title, Is.EqualTo("In Cold Blood"));
                Assert.That(ok.Requests, Has.Count.EqualTo(1), "served from the cache");
                Assert.That(ok.Requests[0].Url.FullUri, Is.EqualTo("https://api.audnex.us/books/B002V1BPPW"));
                Assert.That(ok.Requests[0].Headers.GetSingleValue("User-Agent"), Does.StartWith("TaleArr/"));
                Assert.That(ok.Requests[0].RateLimit, Is.EqualTo(TimeSpan.FromSeconds(1)));
                Assert.That(client.GetBook("0451154460"), Is.Null, "never asks for an ISBN-10");
                Assert.That(ok.Requests, Has.Count.EqualTo(1));
            });

            var notFound = new StatusHttpClient(HttpStatusCode.NotFound);
            var missing = new AudnexusClient(notFound, new CacheManager(), LogManager.GetCurrentClassLogger());
            Assert.That(missing.GetBook("B000000000"), Is.Null);
            Assert.That(missing.GetBook("B000000000"), Is.Null);
            Assert.That(notFound.Requests, Has.Count.EqualTo(1), "unknown ASINs are cached too");

            Assert.Throws<AudnexusException>(() => new AudnexusClient(new StatusHttpClient(HttpStatusCode.ServiceUnavailable), new CacheManager(), LogManager.GetCurrentClassLogger()).GetBook("B002V1BPPW"));
        }
    }
}
