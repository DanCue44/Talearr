using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    public class OpenLibraryException : NzbDroneException
    {
        public OpenLibraryException(string message, params object[] args)
            : base(message, args)
        {
        }

        public OpenLibraryException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public interface IOpenLibraryClient
    {
        List<OpenLibraryAuthorDoc> SearchAuthors(string name, int limit = 10);
        OpenLibraryAuthorRecord GetAuthor(string authorKey);
        List<OpenLibraryWorkDoc> GetAuthorWorks(string authorKey, int limit = 100);
        List<OpenLibraryWorkDoc> SearchWorks(string query, int limit = 20);
    }

    // Talearr: keyless client for the public Open Library API (openlibrary.org), used only as the
    // fallback metadata source. Polite by design: an identifying User-Agent, at most one request
    // per second, a 15s timeout and a 6 hour in-memory cache of successful responses.
    public class OpenLibraryClient : IOpenLibraryClient
    {
        public const string BaseUrl = "https://openlibrary.org";
        public const string CoversUrl = "https://covers.openlibrary.org";

        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan RateLimit = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

        private const string WorkFields = "key,title,subtitle,first_publish_year,isbn,cover_i,cover_edition_key,edition_key,edition_count,language,author_key,author_name,publisher,number_of_pages_median,ratings_average,ratings_count,id_amazon";

        private readonly IHttpClient _httpClient;
        private readonly ICached<string> _cache;
        private readonly Logger _logger;

        public OpenLibraryClient(IHttpClient httpClient, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _cache = cacheManager.GetCache<string>(GetType(), "openlibrary");
            _logger = logger;
        }

        public static string UserAgent => $"Talearr/{BuildInfo.TalearrVersion ?? "0"} (metadata fallback)";

        public List<OpenLibraryAuthorDoc> SearchAuthors(string name, int limit = 10)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new List<OpenLibraryAuthorDoc>();
            }

            var url = $"{BaseUrl}/search/authors.json?q={Uri.EscapeDataString(name.Trim())}&limit={Clamp(limit, 1, 50)}";
            var json = GetJson(url);
            return Docs(json).Select(OpenLibraryAuthorDoc.Parse).Where(a => a != null).ToList();
        }

        public OpenLibraryAuthorRecord GetAuthor(string authorKey)
        {
            var key = OpenLibraryJson.BareKey(authorKey);
            if (key == null || !key.EndsWith("A", StringComparison.Ordinal))
            {
                return null;
            }

            var json = GetJson($"{BaseUrl}/authors/{key}.json");
            return json == null ? null : OpenLibraryAuthorRecord.Parse(json);
        }

        public List<OpenLibraryWorkDoc> GetAuthorWorks(string authorKey, int limit = 100)
        {
            var key = OpenLibraryJson.BareKey(authorKey);
            if (key == null || !key.EndsWith("A", StringComparison.Ordinal))
            {
                return new List<OpenLibraryWorkDoc>();
            }

            var url = $"{BaseUrl}/search.json?author_key={key}&sort=editions&limit={Clamp(limit, 1, 200)}&fields={WorkFields}";
            return Docs(GetJson(url)).Select(OpenLibraryWorkDoc.Parse).Where(w => w != null).ToList();
        }

        public List<OpenLibraryWorkDoc> SearchWorks(string query, int limit = 20)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<OpenLibraryWorkDoc>();
            }

            var url = $"{BaseUrl}/search.json?q={Uri.EscapeDataString(query.Trim())}&limit={Clamp(limit, 1, 50)}&fields={WorkFields}";
            return Docs(GetJson(url)).Select(OpenLibraryWorkDoc.Parse).Where(w => w != null).ToList();
        }

        public static string CoverUrl(long? coverId)
        {
            return coverId.HasValue && coverId.Value > 0 ? $"{CoversUrl}/b/id/{coverId.Value}-L.jpg" : null;
        }

        public static string AuthorPhotoUrl(long photoId)
        {
            return photoId > 0 ? $"{CoversUrl}/a/id/{photoId}-L.jpg" : null;
        }

        private static IEnumerable<JToken> Docs(JToken json)
        {
            return (json?["docs"] as JArray) ?? Enumerable.Empty<JToken>();
        }

        // Returns null for 404 (a legitimate "not found"); throws OpenLibraryException for
        // network errors, rate limiting and server errors.
        private JToken GetJson(string url)
        {
            var cached = _cache.Find(url);
            if (cached != null)
            {
                return Parse(cached, url);
            }

            var request = new HttpRequestBuilder(url)
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", UserAgent)
                .Build();

            request.RequestTimeout = RequestTimeout;
            request.RateLimit = RateLimit;
            request.RateLimitKey = "openlibrary";
            request.SuppressHttpError = true;
            request.AllowAutoRedirect = true;
            request.LogHttpError = false;

            HttpResponse response;
            try
            {
                response = _httpClient.Get(request);
            }
            catch (Exception ex)
            {
                throw new OpenLibraryException($"Open Library request failed: {ex.Message}", ex);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.HasHttpError || response.StatusCode != HttpStatusCode.OK)
            {
                throw new OpenLibraryException("Open Library returned {0} {1}", (int)response.StatusCode, response.StatusCode);
            }

            var token = Parse(response.Content, url);
            _cache.Set(url, response.Content, CacheLifetime);
            return token;
        }

        private JToken Parse(string content, string url)
        {
            try
            {
                return JToken.Parse(content ?? string.Empty);
            }
            catch (Exception ex)
            {
                _cache.Remove(url);
                throw new OpenLibraryException($"Open Library returned an unreadable response: {ex.Message}", ex);
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Min(max, Math.Max(min, value));
        }
    }
}
