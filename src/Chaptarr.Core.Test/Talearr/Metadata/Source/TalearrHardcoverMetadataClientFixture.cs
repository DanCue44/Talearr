using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrHardcoverMetadataClientFixture
    {
        [Test]
        public void reads_author_books_editions_and_series_with_one_root_field_per_request()
        {
            var http = new ScriptedHttpClient(HardcoverFixtures.Responder());
            var client = HardcoverClient.Create(http, out _);

            var catalog = client.GetAuthorCatalog(HardcoverFixtures.AuthorId);

            Assert.That(catalog.Author.Name, Is.EqualTo("Ursula Example"));
            Assert.That(catalog.Books.Select(b => b.Id), Is.EqualTo(new[] { 100, 101, 106 }));
            Assert.That(catalog.SkippedNotPrimary, Is.EqualTo(1), "the edited anthology is not the author's own work");
            Assert.That(catalog.SkippedDeleted, Is.EqualTo(1));
            Assert.That(catalog.SkippedPartial, Is.EqualTo(1));
            Assert.That(catalog.DuplicateBooks.Select(b => b.Id), Is.EqualTo(new[] { 104 }));
            Assert.That(catalog.Books.Single(b => b.Id == 100).Editions.Select(e => e.Id), Is.EqualTo(new[] { 501, 502, 503, 506 }));
            Assert.That(catalog.Series.Keys, Is.EqualTo(new[] { 7 }));

            // author, books (one short page plus the check page), editions (expected count reached), series
            Assert.That(http.Requests.Select(HardcoverFixtures.OperationOf),
                Is.EqualTo(new[] { "TalearrAuthor", "TalearrAuthorBooks", "TalearrAuthorBooks", "TalearrEditions", "TalearrSeries" }));
            Assert.That(catalog.RequestCount, Is.EqualTo(5));

            var request = http.Requests.First();
            Assert.That(request.Url.FullUri, Is.EqualTo(TalearrHardcoverMetadataClient.Endpoint));
            Assert.That(request.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer token"));
        }

        [Test]
        public void every_query_has_a_single_top_level_field()
        {
            foreach (var query in new[] { TalearrHardcoverMetadataClient.AuthorQuery, TalearrHardcoverMetadataClient.BooksQuery, TalearrHardcoverMetadataClient.EditionsQuery, TalearrHardcoverMetadataClient.SeriesQuery })
            {
                // The operation body opens one root selection: count roots at brace depth 1.
                var depth = 0;
                var roots = 0;
                var inArgs = 0;
                var body = query.Substring(query.IndexOf('{'));
                for (var i = 0; i < body.Length; i++)
                {
                    var c = body[i];
                    if (c == '(')
                    {
                        inArgs++;
                    }
                    else if (c == ')')
                    {
                        inArgs--;
                    }
                    else if (inArgs == 0 && c == '{')
                    {
                        depth++;
                        if (depth == 2)
                        {
                            roots++;
                        }
                    }
                    else if (inArgs == 0 && c == '}')
                    {
                        depth--;
                    }
                }

                Assert.That(roots, Is.EqualTo(1), query);
            }
        }

        [Test]
        public void second_read_comes_from_the_cache()
        {
            var http = new ScriptedHttpClient(HardcoverFixtures.Responder());
            var client = HardcoverClient.Create(http, out _);

            client.GetAuthorCatalog(HardcoverFixtures.AuthorId);
            var count = http.Requests.Count;
            client.GetAuthorCatalog(HardcoverFixtures.AuthorId);

            Assert.That(http.Requests.Count, Is.EqualTo(count));
            Assert.That(client.GetStatus().CachedAuthors, Is.EqualTo(1));

            client.GetAuthorCatalog(HardcoverFixtures.AuthorId, useCache: false);
            Assert.That(http.Requests.Count, Is.EqualTo(count * 2));
        }

        [Test]
        public void unknown_author_returns_null()
        {
            var http = new ScriptedHttpClient(HardcoverFixtures.Responder());
            var client = HardcoverClient.Create(http, out _);

            Assert.That(client.GetAuthorCatalog(9999), Is.Null);
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public void pages_by_id_until_a_short_page_once_the_limit_is_honored()
        {
            var pages = new List<int>();
            var rows = PageByKeyset(250, cap: null, pages);

            Assert.That(rows, Is.EqualTo(250));
            Assert.That(pages, Is.EqualTo(new[] { 0, 100, 200 }), "afterId of each request");
        }

        [Test]
        public void keeps_paging_when_the_server_caps_pages_below_the_requested_limit()
        {
            var pages = new List<int>();
            var rows = PageByKeyset(7, cap: 3, pages);

            Assert.That(rows, Is.EqualTo(7));
            Assert.That(pages, Is.EqualTo(new[] { 0, 3, 6, 7 }), "a short page is not trusted until the server is seen honoring the limit");
        }

        [Test]
        public void stops_paging_at_the_expected_total()
        {
            var pages = new List<int>();
            var rows = PageByKeyset(7, cap: 3, pages, expected: 7);

            Assert.That(rows, Is.EqualTo(7));
            Assert.That(pages, Is.EqualTo(new[] { 0, 3, 6 }));
        }

        [Test]
        public void paged_client_reads_everything_when_hardcover_caps_pages()
        {
            var http = new ScriptedHttpClient(HardcoverFixtures.Responder(pageCap: 2));
            var client = HardcoverClient.Create(http, out _);

            var catalog = client.GetAuthorCatalog(HardcoverFixtures.AuthorId);

            Assert.That(catalog.Books.Select(b => b.Id), Is.EqualTo(new[] { 100, 101, 106 }));
            Assert.That(catalog.Books.Sum(b => b.Editions.Count), Is.EqualTo(6));
            var afterIds = http.Requests
                .Where(r => HardcoverFixtures.OperationOf(r) == "TalearrAuthorBooks")
                .Select(r => HardcoverFixtures.VariablesOf(r).Value<int>("afterId"))
                .ToList();
            Assert.That(afterIds, Is.EqualTo(new[] { 0, 101, 103, 105, 106 }));
        }

        [Test]
        public void editions_are_chunked_by_expected_edition_count()
        {
            var counts = new Dictionary<int, int> { [1] = 60, [2] = 30, [3] = 20, [4] = 250, [5] = 1 };
            var chunks = TalearrHardcoverMetadataClient.ChunkByEditionCount(new List<int> { 1, 2, 3, 4, 5 }, counts);

            Assert.That(chunks.Select(c => string.Join(",", c)), Is.EqualTo(new[] { "1,2", "3", "4", "5" }));
        }

        [Test]
        public void waits_retry_after_on_429_and_then_succeeds()
        {
            var calls = 0;
            var http = new ScriptedHttpClient(request =>
            {
                calls++;
                if (calls == 1)
                {
                    var headers = new HttpHeader();
                    headers.Add("Retry-After", "7");
                    return new HttpResponse(request, headers, @"{""error"":""Too Many Requests""}", HttpStatusCode.TooManyRequests);
                }

                return HardcoverFixtures.Responder()(request);
            });

            var client = HardcoverClient.Create(http, out var sleeps);
            var catalog = client.GetAuthorCatalog(HardcoverFixtures.AuthorId);

            Assert.That(catalog, Is.Not.Null);
            Assert.That(sleeps.Sum(s => s.TotalSeconds), Is.GreaterThanOrEqualTo(7));
            Assert.That(HardcoverFixtures.OperationOf(http.Requests[0]), Is.EqualTo("TalearrAuthor"));
            Assert.That(HardcoverFixtures.OperationOf(http.Requests[1]), Is.EqualTo("TalearrAuthor"));
        }

        [Test]
        public void obeys_the_rate_limit_header_when_the_bucket_is_empty()
        {
            var http = new ScriptedHttpClient(request =>
            {
                var headers = new HttpHeader();
                headers.Add("RateLimit", @"""Free"";r=0;t=12, ""daily"";r=4231;t=51234");
                return HardcoverFixtures.Ok(request, HardcoverFixtures.Responder()(request).Content, headers);
            });

            var client = HardcoverClient.Create(http, out var sleeps);
            client.GetAuthorCatalog(HardcoverFixtures.AuthorId);

            Assert.That(sleeps.Count, Is.GreaterThan(0));
            Assert.That(sleeps.Max(), Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(client.GetStatus().DailyRemaining, Is.EqualTo(4231));
        }

        [Test]
        public void stops_before_the_daily_limit_runs_out()
        {
            var http = new ScriptedHttpClient(request =>
            {
                var headers = new HttpHeader();
                headers.Add("RateLimit", $@"""Free"";r=9;t=0, ""daily"";r={TalearrHardcoverMetadataClient.DailyReserve};t=500");
                return HardcoverFixtures.Ok(request, HardcoverFixtures.Responder()(request).Content, headers);
            });

            var client = HardcoverClient.Create(http, out _);

            Assert.Throws<TalearrHardcoverBudgetException>(() => client.GetAuthorCatalog(HardcoverFixtures.AuthorId));
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public void graphql_errors_are_reported()
        {
            var http = new ScriptedHttpClient(request => HardcoverFixtures.Ok(request, @"{""errors"":[{""message"":""field 'compilation' not found in type: 'books'""}]}"));
            var client = HardcoverClient.Create(http, out _);

            var ex = Assert.Throws<TalearrHardcoverException>(() => client.GetAuthorCatalog(HardcoverFixtures.AuthorId));
            Assert.That(ex.Message, Does.Contain("compilation"));
            Assert.That(client.GetStatus().LastError, Does.Contain("compilation"));
        }

        [Test]
        public void unauthorized_is_not_retried()
        {
            var http = new ScriptedHttpClient(request => new HttpResponse(request, new HttpHeader(), @"{""error"":""invalid_token""}", HttpStatusCode.Unauthorized));
            var client = HardcoverClient.Create(http, out _);

            Assert.Throws<TalearrHardcoverException>(() => client.GetAuthorCatalog(HardcoverFixtures.AuthorId));
            Assert.That(http.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public void server_errors_are_retried()
        {
            var calls = 0;
            var http = new ScriptedHttpClient(request =>
            {
                calls++;
                return calls < 3
                    ? new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.ServiceUnavailable)
                    : HardcoverFixtures.Responder()(request);
            });

            var client = HardcoverClient.Create(http, out _);

            Assert.That(client.GetAuthorCatalog(HardcoverFixtures.AuthorId), Is.Not.Null);
        }

        [Test]
        public void parses_editions_with_narrators_language_and_publisher()
        {
            var catalog = HardcoverFixtures.Catalog();
            var audio = catalog.Books.Single(b => b.Id == 100).Editions.Single(e => e.Id == 501);

            Assert.That(audio.ReadingFormatId, Is.EqualTo(2));
            Assert.That(audio.AudioSeconds, Is.EqualTo(36000));
            Assert.That(audio.LanguageCode3, Is.EqualTo("eng"));
            Assert.That(audio.Publisher, Is.EqualTo("Recorded Books"));
            Assert.That(audio.Contributors.Select(c => c.Contribution), Is.EqualTo(new[] { null, "Narrator" }));
            Assert.That(audio.ReleaseDate, Is.EqualTo("2012-03-01"));

            var book = catalog.Books.Single(b => b.Id == 100);
            Assert.That(book.Genres, Is.EqualTo(new[] { "Fantasy", "Classics" }));
            Assert.That(book.Moods, Is.EqualTo(new[] { "adventurous" }));
            Assert.That(book.UpdatedAt, Is.EqualTo(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)));
            Assert.That(catalog.Author.Identifiers["goodreads"], Is.EqualTo(new[] { "874602" }));
            Assert.That(catalog.Author.Identifiers["viaf"], Is.EqualTo(new[] { "71391324" }));
        }

        [Test]
        public void rate_limiter_allows_a_burst_then_spaces_requests()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var sleeps = new List<TimeSpan>();
            var limiter = new TalearrHardcoverRateLimiter(() => now, t =>
            {
                sleeps.Add(t);
                now += t;
            }, burst: 3, refillInterval: TimeSpan.FromSeconds(1));

            for (var i = 0; i < 5; i++)
            {
                limiter.Acquire();
            }

            Assert.That(sleeps.Count, Is.EqualTo(2));
            Assert.That(sleeps.Sum(s => s.TotalSeconds), Is.EqualTo(2).Within(0.01));
        }

        private static int PageByKeyset(int total, int? cap, List<int> afterIds, int? expected = null)
        {
            var all = Enumerable.Range(1, total).ToList();
            var rows = TalearrHardcoverMetadataClient.PageByKeyset((afterId, limit) =>
            {
                afterIds.Add(afterId);
                var take = cap.HasValue ? Math.Min(limit, cap.Value) : limit;
                return all.Where(id => id > afterId).Take(take).Select(id => (JToken)new JObject { ["id"] = id }).ToList();
            }, expected);

            return rows.Count;
        }
    }
}
