using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.MetadataSource.Audible;
using NzbDrone.Core.MetadataSource.Audible.Resources;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Result of one client call. Throttled means "no answer now, try later": nothing is cached.
    public sealed class AudibleLookupResult
    {
        public List<AudibleProduct> Products { get; } = new List<AudibleProduct>();
        public bool Throttled { get; set; }
        public bool Failed { get; set; }

        public bool Answered => !Throttled && !Failed;
    }

    public interface ITalearrAudibleClient
    {
        // One request per batch of ASINs. Every asked ASIN comes back, as Ok or NotFound.
        AudibleLookupResult GetProducts(string marketplace, IReadOnlyCollection<string> asins);

        // Catalog search by title and author. Found products only.
        AudibleLookupResult Search(string marketplace, string title, string author);

        // One page (0 based) of an author's catalog, newest first, with series. Used by the
        // Audible fill of Talearr's metadata source.
        AudibleCatalogPage SearchAuthor(string marketplace, string author, int page);

        // Audnexus, one ASIN per call. Ok, NotFound or RegionUnavailable.
        AudibleLookupResult GetFromAudnexus(string marketplace, string asin);

        // True while a host is backing off after 429/503 answers.
        bool IsBackingOff(string marketplace);
    }

    // Talearr: direct Audible catalog client for recording facts, marketplace aware.
    //
    // Politeness: one request per second per host (the shared upstream rate limiter), batch
    // lookups of up to BatchSize ASINs per request, and a back-off on 429 or 503 that starts at
    // one minute and doubles up to 30 minutes (or follows Retry-After). The user agent and
    // headers are the browser-style ones upstream's AudibleCatalogProxy already sends, but one
    // agent is kept for the whole process rather than rotated per request. Caching is the
    // caller's job (TalearrAudibleFactsService keeps results in talearr.db).
    public class TalearrAudibleClient : ITalearrAudibleClient
    {
        public const int BatchSize = 25;
        public const string ResponseGroups = "product_desc,contributors,product_attrs";
        public const string AuthorResponseGroups = "product_desc,contributors,product_attrs,series,media";
        public const int AuthorPageSize = 50;
        public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        private const string AudnexusBaseUrl = "https://api.audnex.us";
        private static readonly Regex AsinRegex = new Regex(@"^[A-Z0-9]{10}$", RegexOptions.Compiled);

        private static readonly string ProcessUserAgent = PickUserAgent();

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, BackoffState> _backoff = new Dictionary<string, BackoffState>(StringComparer.OrdinalIgnoreCase);

        public TalearrAudibleClient(IHttpClient httpClient, Logger logger)
            : this(httpClient, logger, () => DateTime.UtcNow)
        {
        }

        internal TalearrAudibleClient(IHttpClient httpClient, Logger logger, Func<DateTime> clock)
        {
            _httpClient = httpClient;
            _logger = logger;
            _clock = clock;
        }

        public static string UserAgent => ProcessUserAgent;

        public static bool LooksLikeAsin(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && AsinRegex.IsMatch(value.Trim().ToUpperInvariant());
        }

        public bool IsBackingOff(string marketplace)
        {
            var host = TalearrAudibleMarketplaces.Get(marketplace).ApiBaseUrl;
            return BackoffUntil(host) > _clock();
        }

        public AudibleLookupResult GetProducts(string marketplace, IReadOnlyCollection<string> asins)
        {
            var market = TalearrAudibleMarketplaces.Get(marketplace);
            var result = new AudibleLookupResult();
            var wanted = (asins ?? Array.Empty<string>())
                .Select(TalearrRecordingsStore.NormalizeAsin)
                .Where(LooksLikeAsin)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var chunk in wanted.Chunk(BatchSize))
            {
                var url = $"{market.ApiBaseUrl}/1.0/catalog/products?asins={string.Join(",", chunk)}&response_groups={ResponseGroups}";
                var content = Fetch(market.ApiBaseUrl, url, market, result);
                if (content == null)
                {
                    return result;
                }

                var response = Deserialize<AudibleCatalogProductsResponse>(content, result);
                if (response == null)
                {
                    return result;
                }

                var now = _clock();
                var byAsin = (response.Products ?? new List<AudibleCatalogProductResource>())
                    .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Asin))
                    .GroupBy(p => p.Asin.Trim().ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var asin in chunk)
                {
                    // Audible answers unknown or other-store ASINs with a bare { asin } stub.
                    if (byAsin.TryGetValue(asin, out var product) && !string.IsNullOrWhiteSpace(product.Title))
                    {
                        result.Products.Add(Map(product, market.Code, now));
                    }
                    else
                    {
                        result.Products.Add(AudibleProduct.Missing(market.Code, asin, AudibleProductStatus.NotFound, AudibleProduct.SourceAudible, now));
                    }
                }
            }

            return result;
        }

        public AudibleLookupResult Search(string marketplace, string title, string author)
        {
            var market = TalearrAudibleMarketplaces.Get(marketplace);
            var result = new AudibleLookupResult();

            if (string.IsNullOrWhiteSpace(title))
            {
                return result;
            }

            var url = $"{market.ApiBaseUrl}/1.0/catalog/products?title={Uri.EscapeDataString(title.Trim())}" +
                      (string.IsNullOrWhiteSpace(author) ? string.Empty : $"&author={Uri.EscapeDataString(author.Trim())}") +
                      $"&num_results=20&products_sort_by=Relevance&response_groups={ResponseGroups}";

            var content = Fetch(market.ApiBaseUrl, url, market, result);
            if (content == null)
            {
                return result;
            }

            var response = Deserialize<AudibleCatalogProductsResponse>(content, result);
            var now = _clock();
            foreach (var product in response?.Products ?? new List<AudibleCatalogProductResource>())
            {
                if (product != null && !string.IsNullOrWhiteSpace(product.Asin) && !string.IsNullOrWhiteSpace(product.Title))
                {
                    result.Products.Add(Map(product, market.Code, now));
                }
            }

            return result;
        }

        public AudibleCatalogPage SearchAuthor(string marketplace, string author, int page)
        {
            var market = TalearrAudibleMarketplaces.Get(marketplace);
            var result = new AudibleCatalogPage();
            if (string.IsNullOrWhiteSpace(author))
            {
                return result;
            }

            var lookup = new AudibleLookupResult();
            var url = $"{market.ApiBaseUrl}/1.0/catalog/products?author={Uri.EscapeDataString(author.Trim())}" +
                      $"&num_results={AuthorPageSize}&page={Math.Max(0, page)}&products_sort_by=-ReleaseDate&response_groups={AuthorResponseGroups}";

            var content = Fetch(market.ApiBaseUrl, url, market, lookup);
            var response = content == null ? null : Deserialize<AudibleAuthorCatalogResponse>(content, lookup);
            result.Throttled = lookup.Throttled;
            result.Failed = lookup.Failed;
            if (response == null)
            {
                return result;
            }

            result.TotalResults = response.TotalResults;
            foreach (var product in response.Products ?? new List<AudibleAuthorCatalogProduct>())
            {
                if (product == null || !LooksLikeAsin(product.Asin) || string.IsNullOrWhiteSpace(product.Title))
                {
                    continue;
                }

                result.Items.Add(new AudibleCatalogItem
                {
                    Asin = product.Asin.Trim().ToUpperInvariant(),
                    Title = product.Title.Trim(),
                    Subtitle = string.IsNullOrWhiteSpace(product.Subtitle) ? null : product.Subtitle.Trim(),
                    Authors = (product.Authors ?? new List<AudibleAuthorCatalogName>()).Select(a => a?.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList(),
                    Narrators = (product.Narrators ?? new List<AudibleAuthorCatalogName>()).Select(a => a?.Name?.Trim()).Where(n => !string.IsNullOrEmpty(n)).ToList(),
                    Series = (product.Series ?? new List<AudibleAuthorCatalogSeries>())
                        .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Title))
                        .Select(s => new AudibleCatalogSeries
                        {
                            Asin = LooksLikeAsin(s.Asin) ? s.Asin.Trim().ToUpperInvariant() : null,
                            Title = s.Title.Trim(),
                            Sequence = string.IsNullOrWhiteSpace(s.Sequence) ? null : s.Sequence.Trim()
                        })
                        .ToList(),
                    RuntimeMinutes = product.RuntimeLengthMin > 0 ? product.RuntimeLengthMin : null,
                    ReleaseDate = IsoDate(product.ReleaseDate),
                    Language = product.Language?.Trim().ToLowerInvariant(),
                    FormatType = product.FormatType?.Trim().ToLowerInvariant(),
                    DeliveryType = product.ContentDeliveryType?.Trim(),
                    Publisher = product.PublisherName,
                    Summary = product.MerchandisingSummary,
                    ImageUrl = BestImage(product.ProductImages)
                });
            }

            return result;
        }

        private static string BestImage(Dictionary<string, string> images)
        {
            return (images ?? new Dictionary<string, string>())
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .OrderByDescending(kvp => int.TryParse(kvp.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0)
                .Select(kvp => kvp.Value)
                .FirstOrDefault();
        }

        public AudibleLookupResult GetFromAudnexus(string marketplace, string asin)
        {
            var market = TalearrAudibleMarketplaces.Get(marketplace);
            var result = new AudibleLookupResult();
            var normalized = TalearrRecordingsStore.NormalizeAsin(asin);
            if (!LooksLikeAsin(normalized))
            {
                return result;
            }

            var url = $"{AudnexusBaseUrl}/books/{normalized}?region={market.Code}";
            var request = BuildRequest(AudnexusBaseUrl, url, market, browserHeaders: false);
            var response = Send(AudnexusBaseUrl, request, result);
            if (response == null)
            {
                return result;
            }

            var now = _clock();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                var status = response.Content?.Contains("REGION_UNAVAILABLE", StringComparison.OrdinalIgnoreCase) == true
                    ? AudibleProductStatus.RegionUnavailable
                    : AudibleProductStatus.NotFound;
                result.Products.Add(AudibleProduct.Missing(market.Code, normalized, status, AudibleProduct.SourceAudnexus, now));
                return result;
            }

            if (response.HasHttpError)
            {
                result.Failed = true;
                _logger.Debug("Talearr: Audnexus answered HTTP {0} for {1}", (int)response.StatusCode, normalized);
                return result;
            }

            var book = Deserialize<AudnexusBookResource>(response.Content, result);
            if (book == null || string.IsNullOrWhiteSpace(book.Title))
            {
                result.Products.Add(AudibleProduct.Missing(market.Code, normalized, AudibleProductStatus.NotFound, AudibleProduct.SourceAudnexus, now));
                return result;
            }

            result.Products.Add(new AudibleProduct
            {
                Marketplace = market.Code,
                Asin = normalized,
                Status = AudibleProductStatus.Ok,
                Title = book.Title,
                Subtitle = book.Subtitle,
                FormatType = NormalizeFormat(book.FormatType),
                RuntimeMinutes = book.RuntimeLengthMin > 0 ? book.RuntimeLengthMin : null,
                Narrators = Names(book.Narrators),
                Authors = Names(book.Authors),
                Publisher = book.PublisherName,
                ReleaseDate = IsoDate(book.ReleaseDate),
                Language = book.Language,
                Source = AudibleProduct.SourceAudnexus,
                FetchedAt = now
            });

            return result;
        }

        private string Fetch(string host, string url, TalearrAudibleMarketplace market, AudibleLookupResult result)
        {
            var response = Send(host, BuildRequest(host, url, market, browserHeaders: true), result);
            if (response == null)
            {
                return null;
            }

            if (response.HasHttpError)
            {
                result.Failed = true;
                _logger.Debug("Talearr: Audible {0} answered HTTP {1}", market.Code, (int)response.StatusCode);
                return null;
            }

            return response.Content;
        }

        private HttpResponse Send(string host, HttpRequest request, AudibleLookupResult result)
        {
            if (BackoffUntil(host) > _clock())
            {
                result.Throttled = true;
                return null;
            }

            HttpResponse response;
            try
            {
                response = _httpClient.Execute(request);
            }
            catch (Exception ex)
            {
                result.Failed = true;
                _logger.Debug(ex, "Talearr: request to {0} failed", host);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                StartBackoff(host, response);
                result.Throttled = true;
                return null;
            }

            ClearBackoff(host);
            return response;
        }

        private static HttpRequest BuildRequest(string host, string url, TalearrAudibleMarketplace market, bool browserHeaders)
        {
            var request = new HttpRequest(url, HttpAccept.Json)
            {
                SuppressHttpError = true,
                AllowAutoRedirect = true,
                LogHttpError = false,
                RequestTimeout = TimeSpan.FromSeconds(20),
                RateLimit = MinimumInterval,
                RateLimitKey = "talearr-recordings"
            };

            if (browserHeaders)
            {
                request.Headers.Add("User-Agent", ProcessUserAgent);
                request.Headers.Add("Accept-Language", market.AcceptLanguage);
                request.Headers.Add("Origin", $"https://www.{market.Domain}");
                request.Headers.Add("Referer", $"https://www.{market.Domain}/");
            }

            return request;
        }

        private DateTime BackoffUntil(string host)
        {
            lock (_backoff)
            {
                return _backoff.TryGetValue(host, out var state) ? state.Until : DateTime.MinValue;
            }
        }

        private void StartBackoff(string host, HttpResponse response)
        {
            lock (_backoff)
            {
                _backoff.TryGetValue(host, out var state);
                var next = state == null ? FirstBackoff : TimeSpan.FromTicks(Math.Min(state.Length.Ticks * 2, MaxBackoff.Ticks));

                var retryAfter = response.Headers?.GetSingleValue("Retry-After");
                if (int.TryParse(retryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    next = TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
                }

                _backoff[host] = new BackoffState { Length = next, Until = _clock() + next };
                _logger.Warn("Talearr: {0} is throttling (HTTP {1}); pausing Audible lookups for {2} minutes", host, (int)response.StatusCode, Math.Ceiling(next.TotalMinutes));
            }
        }

        private void ClearBackoff(string host)
        {
            lock (_backoff)
            {
                _backoff.Remove(host);
            }
        }

        private T Deserialize<T>(string content, AudibleLookupResult result)
            where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(content ?? string.Empty);
            }
            catch (JsonException ex)
            {
                result.Failed = true;
                _logger.Debug(ex, "Talearr: unreadable Audible response");
                return null;
            }
        }

        private static AudibleProduct Map(AudibleCatalogProductResource product, string marketplace, DateTime now)
        {
            return new AudibleProduct
            {
                Marketplace = marketplace,
                Asin = product.Asin.Trim().ToUpperInvariant(),
                Status = AudibleProductStatus.Ok,
                Title = product.Title,
                Subtitle = product.Subtitle,
                FormatType = NormalizeFormat(product.FormatType),
                RuntimeMinutes = product.RuntimeLengthMin > 0 ? product.RuntimeLengthMin : null,
                Narrators = (product.Narrators ?? new List<AudibleCatalogContributorResource>()).Select(n => n?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                Authors = (product.Authors ?? new List<AudibleCatalogContributorResource>()).Select(n => n?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList(),
                Publisher = product.PublisherName,
                ReleaseDate = IsoDate(product.ReleaseDate ?? product.IssueDate ?? product.PublicationDateTime),
                Language = product.Language,
                Source = AudibleProduct.SourceAudible,
                FetchedAt = now
            };
        }

        private static string NormalizeFormat(string formatType)
        {
            if (string.IsNullOrWhiteSpace(formatType))
            {
                return null;
            }

            var value = formatType.Trim().ToLowerInvariant();
            return value == "unabridged" || value == "abridged" ? value : null;
        }

        private static string IsoDate(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        }

        private static List<string> Names(List<AudnexusPersonResource> people)
        {
            return (people ?? new List<AudnexusPersonResource>()).Select(p => p?.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        }

        private static string PickUserAgent()
        {
            var agents = AudibleCatalogProxy.UserAgents;
            return agents[new Random().Next(agents.Count)];
        }

        private sealed class BackoffState
        {
            public TimeSpan Length { get; set; }
            public DateTime Until { get; set; }
        }

        private sealed class AudnexusBookResource
        {
            [JsonProperty("asin")]
            public string Asin { get; set; }

            [JsonProperty("title")]
            public string Title { get; set; }

            [JsonProperty("subtitle")]
            public string Subtitle { get; set; }

            [JsonProperty("formatType")]
            public string FormatType { get; set; }

            [JsonProperty("runtimeLengthMin")]
            public int? RuntimeLengthMin { get; set; }

            [JsonProperty("narrators")]
            public List<AudnexusPersonResource> Narrators { get; set; }

            [JsonProperty("authors")]
            public List<AudnexusPersonResource> Authors { get; set; }

            [JsonProperty("publisherName")]
            public string PublisherName { get; set; }

            [JsonProperty("releaseDate")]
            public DateTime? ReleaseDate { get; set; }

            [JsonProperty("language")]
            public string Language { get; set; }
        }

        private sealed class AudnexusPersonResource
        {
            [JsonProperty("name")]
            public string Name { get; set; }
        }
    }
}
