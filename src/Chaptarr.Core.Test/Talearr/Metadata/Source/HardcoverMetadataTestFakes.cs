using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // Canned Hardcover GraphQL answers. The row shapes follow the field lists in
    // docs.hardcover.app (schemas Books, Editions, Authors, Series, BookSeries, read 2026-10-20);
    // they have not been captured from a live token yet.
    internal static class HardcoverFixtures
    {
        public const int AuthorId = 42;

        public const string Author = @"{""data"":{""authors"":[{
            ""id"":42,""name"":""Ursula Example"",""slug"":""ursula-example"",""bio"":""Wrote about wind."",
            ""born_date"":""1929-10-21"",""death_date"":""2018-01-22"",""canonical_id"":null,
            ""books_count"":7,""users_count"":5000,""updated_at"":""2026-09-01T10:00:00+00:00"",
            ""alternate_names"":[""U. Example""],
            ""identifiers"":{""goodreads"":[""874602""],""viaf"":""71391324"",""isni"":[""0000000121441570""]},
            ""image"":{""url"":""https://assets.hardcover.app/author/42.jpg""}}]}}";

        public const string Books = @"{""data"":{""books"":[
            {""id"":100,""title"":""The Wind Book"",""subtitle"":null,""slug"":""the-wind-book"",""description"":""A wizard school."",
             ""release_date"":""1968-01-01"",""release_year"":1968,""pages"":205,""audio_seconds"":null,""rating"":4.1,""ratings_count"":900,
             ""users_count"":4000,""compilation"":false,""book_category_id"":1,""literary_type_id"":1,""book_status_id"":1,""canonical_id"":null,
             ""is_partial_book"":false,""editions_count"":4,""default_audio_edition_id"":501,""default_ebook_edition_id"":502,""default_physical_edition_id"":503,
             ""updated_at"":""2026-08-01T00:00:00+00:00"",""cached_tags"":{""Genre"":[{""tag"":""Fantasy"",""count"":40},{""tag"":""Classics"",""count"":10}],""Mood"":[""adventurous""]},
             ""image"":{""url"":""https://assets.hardcover.app/book/100.jpg""},
             ""contributions"":[{""contribution"":null}],
             ""book_series"":[{""series_id"":7,""position"":1,""details"":""1"",""featured"":true,""compilation"":false}]},
            {""id"":101,""title"":""The Wind Trilogy"",""slug"":""the-wind-trilogy"",""release_date"":""1990-05-01"",""rating"":4.5,""ratings_count"":50,
             ""users_count"":100,""compilation"":true,""book_status_id"":1,""is_partial_book"":false,""editions_count"":1,
             ""contributions"":[{""contribution"":""Author""}],
             ""book_series"":[{""series_id"":7,""position"":1,""details"":""1-3"",""featured"":false,""compilation"":true}]},
            {""id"":102,""title"":""Edited Anthology"",""slug"":""edited-anthology"",""book_status_id"":1,""editions_count"":1,
             ""contributions"":[{""contribution"":""Editor""}],""book_series"":[]},
            {""id"":103,""title"":""Deleted Row"",""book_status_id"":3,""editions_count"":1,""contributions"":[{""contribution"":null}],""book_series"":[]},
            {""id"":104,""title"":""The Wind Book (duplicate)"",""book_status_id"":4,""canonical_id"":100,""editions_count"":1,""contributions"":[{""contribution"":null}],""book_series"":[]},
            {""id"":105,""title"":""The Wind Book, Part 1"",""book_status_id"":1,""is_partial_book"":true,""editions_count"":1,""contributions"":[{""contribution"":null}],""book_series"":[]},
            {""id"":106,""title"":""A Wind Novella"",""slug"":""a-wind-novella"",""book_status_id"":1,""editions_count"":1,""rating"":3.9,""ratings_count"":20,
             ""contributions"":[{""contribution"":""""}],
             ""book_series"":[{""series_id"":7,""position"":1.5,""details"":""1.5"",""featured"":true,""compilation"":false}]}
        ]}}";

        public const string Editions = @"{""data"":{""editions"":[
            {""id"":501,""book_id"":100,""title"":""The Wind Book"",""isbn_10"":null,""isbn_13"":null,""asin"":""b00audio01"",""reading_format_id"":2,
             ""edition_format"":""Audible Audio"",""release_date"":""2012-03-01"",""pages"":null,""audio_seconds"":36000,""rating"":4.2,""users_count"":300,
             ""canonical_id"":null,""updated_at"":""2026-07-01T00:00:00+00:00"",
             ""cached_contributors"":[{""author"":{""id"":42,""name"":""Ursula Example""},""contribution"":null},{""author"":{""id"":900,""name"":""Nora Narrator""},""contribution"":""Narrator""}],
             ""language"":{""code2"":""en"",""code3"":""eng"",""language"":""English""},""publisher"":{""name"":""Recorded Books""},""image"":{""url"":""https://assets.hardcover.app/edition/501.jpg""}},
            {""id"":502,""book_id"":100,""title"":""The Wind Book"",""isbn_13"":""978-0-547-77374-2"",""asin"":""B0KINDLE01"",""reading_format_id"":4,
             ""edition_format"":""Kindle Edition"",""release_date"":""2012-09-11"",""pages"":264,""users_count"":800,""cached_contributors"":[],
             ""language"":{""code2"":""en"",""code3"":""eng"",""language"":""English""},""publisher"":{""name"":""HMH Books""},""image"":null},
            {""id"":503,""book_id"":100,""title"":""The Wind Book"",""isbn_10"":""0553383043"",""isbn_13"":""9780553383041"",""reading_format_id"":1,
             ""edition_format"":""Paperback"",""release_date"":""2004-09-28"",""pages"":183,""users_count"":1500,""cached_contributors"":[],
             ""language"":{""code2"":""en"",""code3"":""eng"",""language"":""English""},""publisher"":{""name"":""Bantam""}},
            {""id"":504,""book_id"":101,""title"":""The Wind Trilogy"",""isbn_13"":""9780140348231"",""reading_format_id"":4,""edition_format"":""ebook"",
             ""users_count"":40,""cached_contributors"":[],""language"":{""code2"":""en"",""code3"":""eng"",""language"":""English""}},
            {""id"":505,""book_id"":106,""title"":""A Wind Novella"",""asin"":""B0NOVELLA1"",""reading_format_id"":2,""audio_seconds"":7200,
             ""users_count"":10,""cached_contributors"":[{""author"":{""id"":901,""name"":""Ned Voice""},""contribution"":""Narrator""}],
             ""language"":{""code2"":""en"",""code3"":""eng"",""language"":""English""}},
            {""id"":506,""book_id"":100,""title"":""The Wind Book"",""reading_format_id"":1,""canonical_id"":503,""users_count"":1,""cached_contributors"":[]}
        ]}}";

        public const string Series = @"{""data"":{""series"":[
            {""id"":7,""name"":""Wind Cycle"",""slug"":""wind-cycle"",""description"":""Islands and wizards."",""books_count"":6,""primary_books_count"":5,""is_completed"":true,""canonical_id"":null}
        ]}}";

        public static string Empty(string root) => $@"{{""data"":{{""{root}"":[]}}}}";

        // Answers the four queries the client sends, by operation name. Keyset paging is honored:
        // the canned rows after the request's afterId, up to its limit.
        public static Func<HttpRequest, HttpResponse> Responder(int? pageCap = null)
        {
            return request =>
            {
                var body = JObject.Parse(Encoding.UTF8.GetString(request.ContentData));
                var query = body.Value<string>("query");
                var variables = (JObject)body["variables"];

                if (query.Contains("query TalearrAuthor("))
                {
                    return Ok(request, variables.Value<int>("id") == AuthorId ? Author : Empty("authors"));
                }

                if (query.Contains("query TalearrAuthorBooks("))
                {
                    return Ok(request, Page(Books, "books", variables, pageCap));
                }

                if (query.Contains("query TalearrEditions("))
                {
                    var ids = variables["bookIds"].Values<int>().ToHashSet();
                    return Ok(request, Page(Editions, "editions", variables, pageCap, row => ids.Contains(row.Value<int>("book_id"))));
                }

                if (query.Contains("query TalearrSeries("))
                {
                    return Ok(request, Series);
                }

                return new HttpResponse(request, new HttpHeader(), @"{""errors"":[{""message"":""unexpected query""}]}", HttpStatusCode.OK);
            };
        }

        public static string Page(string json, string root, JObject variables, int? pageCap, Func<JToken, bool> filter = null)
        {
            var afterId = variables.Value<int>("afterId");
            var limit = variables.Value<int>("limit");
            if (pageCap.HasValue)
            {
                limit = Math.Min(limit, pageCap.Value);
            }

            var rows = ((JArray)JObject.Parse(json)["data"][root])
                .Where(r => r.Value<int>("id") > afterId)
                .Where(r => filter == null || filter(r))
                .OrderBy(r => r.Value<int>("id"))
                .Take(limit)
                .ToList();

            return new JObject { ["data"] = new JObject { [root] = new JArray(rows) } }.ToString();
        }

        public static HttpResponse Ok(HttpRequest request, string content, HttpHeader headers = null)
        {
            return new HttpResponse(request, headers ?? new HttpHeader(), content, HttpStatusCode.OK);
        }

        public static string OperationOf(HttpRequest request)
        {
            var query = JObject.Parse(Encoding.UTF8.GetString(request.ContentData)).Value<string>("query");
            var start = query.IndexOf("query ", StringComparison.Ordinal) + 6;
            var end = query.IndexOf('(', start);
            return query.Substring(start, end - start).Trim();
        }

        public static JObject VariablesOf(HttpRequest request)
        {
            return (JObject)JObject.Parse(Encoding.UTF8.GetString(request.ContentData))["variables"];
        }

        public static HardcoverAuthorCatalog Catalog()
        {
            var http = new ScriptedHttpClient(Responder());
            var client = HardcoverClient.Create(http, out _);
            return client.GetAuthorCatalog(AuthorId);
        }
    }

    internal static class HardcoverClient
    {
        public static TalearrHardcoverMetadataClient Create(IHttpClient http, out List<TimeSpan> sleeps)
        {
            var slept = new List<TimeSpan>();
            sleeps = slept;
            var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

            // The fake clock moves forward by whatever the limiter sleeps.
            var limiter = new TalearrHardcoverRateLimiter(() => now, t =>
            {
                slept.Add(t);
                now += t;
            }, burst: 5, refillInterval: TimeSpan.FromMilliseconds(1100));

            return new TalearrHardcoverMetadataClient(http,
                global::Chaptarr.Core.Test.Talearr.MetadataFallback.HardcoverConfigProxy.Create(),
                LogManager.GetLogger("hardcover-test"),
                limiter,
                () => now);
        }
    }

    internal sealed class ScriptedHttpClient : IHttpClient
    {
        private readonly Func<HttpRequest, HttpResponse> _handler;

        public ScriptedHttpClient(Func<HttpRequest, HttpResponse> handler)
        {
            _handler = handler;
        }

        public List<HttpRequest> Requests { get; } = new();

        public HttpResponse Execute(HttpRequest request)
        {
            Requests.Add(request);
            return _handler(request);
        }

        public HttpResponse Get(HttpRequest request) => Execute(request);
        public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
        public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
        public HttpResponse Post(HttpRequest request) => Execute(request);
        public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public Task<HttpResponse> ExecuteAsync(HttpRequest request) => Task.FromResult(Execute(request));
        public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
        public Task<HttpResponse> GetAsync(HttpRequest request) => Task.FromResult(Execute(request));
        public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
        public Task<HttpResponse> PostAsync(HttpRequest request) => Task.FromResult(Execute(request));
        public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
    }
}
