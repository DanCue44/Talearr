using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    // TaleArr: one Audible product as Audnexus describes it (only the fields TaleArr uses).
    public sealed class AudnexusBook
    {
        public string Asin { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public List<string> Authors { get; set; } = new List<string>();
        public List<string> Narrators { get; set; } = new List<string>();

        // "unabridged" / "abridged" as Audible reports it, or null.
        public string FormatType { get; set; }
        public int? RuntimeMinutes { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string Publisher { get; set; }
        public string Language { get; set; }
        public string ImageUrl { get; set; }

        public bool? Abridged => FormatType?.Trim().ToLowerInvariant() switch
        {
            "abridged" => true,
            "unabridged" => false,
            _ => null
        };

        public static AudnexusBook Parse(JToken json)
        {
            if (json == null || json.Type != JTokenType.Object)
            {
                return null;
            }

            var asin = OpenLibraryJson.String(json["asin"]);
            var title = OpenLibraryJson.String(json["title"]);
            if (!AudnexusClient.IsAudibleAsin(asin) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var runtime = OpenLibraryJson.Int(json["runtimeLengthMin"]);
            var released = ReadDate(json["releaseDate"]);

            return new AudnexusBook
            {
                Asin = asin.Trim().ToUpperInvariant(),
                Title = title.Trim(),
                Subtitle = OpenLibraryJson.String(json["subtitle"]),
                Authors = Names(json["authors"]),
                Narrators = Names(json["narrators"]),
                FormatType = OpenLibraryJson.String(json["formatType"]),
                RuntimeMinutes = runtime > 0 ? runtime : null,
                ReleaseDate = released,
                Publisher = OpenLibraryJson.String(json["publisherName"]),
                Language = OpenLibraryJson.String(json["language"]),
                ImageUrl = OpenLibraryJson.String(json["image"])
            };
        }

        // JSON.NET turns ISO strings into Date tokens; accept both.
        private static DateTime? ReadDate(JToken token)
        {
            if (token?.Type == JTokenType.Date)
            {
                return token.ToObject<DateTime>().ToUniversalTime();
            }

            var text = OpenLibraryJson.String(token);
            return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                ? date
                : null;
        }

        private static List<string> Names(JToken token)
        {
            return (token as JArray ?? new JArray())
                .Select(item => item.Type == JTokenType.Object ? OpenLibraryJson.String(item["name"]) : OpenLibraryJson.String(item))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => Regex.Replace(name.Trim(), @"\s+", " "))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public interface IAudnexusClient
    {
        // Null when Audnexus does not know the ASIN (or it is not an Audible ASIN). Throws
        // AudnexusException for network and server errors.
        AudnexusBook GetBook(string asin);
    }

    public class AudnexusException : Exception
    {
        public AudnexusException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }

    // TaleArr: keyless client for the public Audnexus API (api.audnex.us), used only to enrich
    // audiobook rows added from the Open Library fallback. Same manners as OpenLibraryClient: an
    // identifying User-Agent, at most one request per second, a 15s timeout, and a cache (a day
    // for found products, six hours for unknown ASINs).
    public class AudnexusClient : IAudnexusClient
    {
        public const string BaseUrl = "https://api.audnex.us";

        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan RateLimit = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
        public static readonly TimeSpan NotFoundCacheLifetime = TimeSpan.FromHours(6);

        private const string NotFoundMarker = "\u0000none";
        private static readonly Regex AsinPattern = new Regex(@"^B0[0-9A-Z]{8}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IHttpClient _httpClient;
        private readonly ICached<string> _cache;
        private readonly Logger _logger;

        public AudnexusClient(IHttpClient httpClient, ICacheManager cacheManager, Logger logger)
        {
            _httpClient = httpClient;
            _cache = cacheManager.GetCache<string>(GetType(), "audnexus");
            _logger = logger;
        }

        public static string UserAgent => OpenLibraryClient.UserAgent;

        // Audible products (as opposed to ISBN-10s Amazon also lists) are B0 + 8 characters.
        public static bool IsAudibleAsin(string asin)
        {
            return !string.IsNullOrWhiteSpace(asin) && AsinPattern.IsMatch(asin.Trim());
        }

        public AudnexusBook GetBook(string asin)
        {
            if (!IsAudibleAsin(asin))
            {
                return null;
            }

            var url = $"{BaseUrl}/books/{asin.Trim().ToUpperInvariant()}";
            var cached = _cache.Find(url);
            if (cached != null)
            {
                return cached == NotFoundMarker ? null : Parse(cached, url);
            }

            var request = new HttpRequestBuilder(url)
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", UserAgent)
                .Build();

            request.RequestTimeout = RequestTimeout;
            request.RateLimit = RateLimit;
            request.RateLimitKey = "audnexus";
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
                throw new AudnexusException($"Audnexus request failed: {ex.Message}", ex);
            }

            // Audnexus answers 400/404 for ASINs it cannot resolve (not an audiobook, other region).
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                _cache.Set(url, NotFoundMarker, NotFoundCacheLifetime);
                return null;
            }

            if (response.HasHttpError || response.StatusCode != HttpStatusCode.OK)
            {
                throw new AudnexusException($"Audnexus returned {(int)response.StatusCode} {response.StatusCode}");
            }

            var book = Parse(response.Content, url);
            _cache.Set(url, book == null ? NotFoundMarker : response.Content, book == null ? NotFoundCacheLifetime : CacheLifetime);
            return book;
        }

        private AudnexusBook Parse(string content, string url)
        {
            try
            {
                return AudnexusBook.Parse(JToken.Parse(content ?? string.Empty));
            }
            catch (Exception ex)
            {
                _cache.Remove(url);
                _logger.Debug(ex, "TaleArr: unreadable Audnexus response for {0}", url);
                return null;
            }
        }
    }
}
