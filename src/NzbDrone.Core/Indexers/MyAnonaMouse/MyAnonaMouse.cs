using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentValidation.Results;
using MonoTorrent;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouse : HttpIndexerBase<MyAnonaMouseSettings>
    {
        private readonly IMamUnsatisfiedSlotReservationRepository _slotReservationRepository;

        // TaleArr: session health, rotated cookies and the seedbox proxy list. Optional so the
        // existing fixtures that build the indexer by hand keep working.
        private readonly IMyAnonaMouseSessionMonitor _sessionMonitor;
        private readonly IProxyService _proxyService;

        public const string SessionExpiredMessage = "MAM session expired: paste a new mam_id";

        public override string Name => "MyAnonaMouse";

        public override DownloadProtocol Protocol => DownloadProtocol.Torrent;
        public override bool SupportsRss => true; // Uses MAM JSON search endpoint for RSS-style sync
        public override bool SupportsSearch => true;
        public override int PageSize => 100;

        public MyAnonaMouse(IIndexerHttpClientFactory httpClientFactory, IIndexerStatusService indexerStatusService, IConfigService configService, IParsingService parsingService,
                            IMamUnsatisfiedSlotReservationRepository slotReservationRepository, Logger logger,
                            IMyAnonaMouseSessionMonitor sessionMonitor = null, IProxyService proxyService = null)
            : base(httpClientFactory, indexerStatusService, configService, parsingService, logger)
        {
            _slotReservationRepository = slotReservationRepository;
            _sessionMonitor = sessionMonitor;
            _proxyService = proxyService;
        }

        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                var config = new MyAnonaMouseSettings
                {
                    SeedTimeHours = 72
                };

                yield return new IndexerDefinition
                {
                    Name = GetType().Name,
                    EnableRss = true, // Enabled by default to match synthetic RSS/recent support
                    EnableAutomaticSearch = true, // Always enabled by default
                    EnableInteractiveSearch = true, // Always enabled by default
                    Implementation = GetType().Name,
                    Settings = config
                };
            }
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new MyAnonaMouseRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new MyAnonaMouseJsonParser(Settings);
        }

        public override HttpRequest GetDownloadRequest(string link)
        {
            var uri = new Uri(link);
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var tid = query["tid"];
            var canUseWedge = string.Equals(query["canUseToken"], "true", StringComparison.OrdinalIgnoreCase);
            var isAudiobook = string.Equals(query["isAudiobook"], "true", StringComparison.OrdinalIgnoreCase);

            query.Remove("canUseToken");
            query.Remove("isAudiobook");
            query.Remove("fl");

            var applyWedge = Settings.UseFreeleechWedge == (int)MyAnonaMouseFreeleechWedgeAction.Preferred &&
                             canUseWedge &&
                             (!Settings.UseFreeleechOnlyForAudiobooks || isAudiobook);
            var queryString = query.ToString();
            var downloadLink = uri.GetLeftPart(UriPartial.Path);
            if (!string.IsNullOrWhiteSpace(queryString))
            {
                downloadLink += "?" + queryString;
            }

            if (applyWedge)
            {
                downloadLink += string.IsNullOrWhiteSpace(queryString) ? "?fl" : "&fl";
                _logger.Debug("Requesting a MAM personal freeleech wedge for torrent {0}", tid);
            }

            var request = new HttpRequest(downloadLink);
            request.Headers.Set("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}");
            var requestHostMatchesMam = Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var baseUri) &&
                                        string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase);

            // Ensure required session cookies are present for authenticated downloads, but never send
            // MAM session cookies to an off-domain redirect target.
            if (requestHostMatchesMam && !string.IsNullOrWhiteSpace(Settings.MamId))
            {
                request.Cookies["mam_id"] = Settings.MamId; // idempotent
            }
            // Include mam_ssl if configured (many sessions require it)
            if (requestHostMatchesMam && Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1"; // idempotent
            }
            // Persist cookies (request + response) so updated mam_id from MAM is kept
            request.StoreRequestCookie = true;
            request.StoreResponseCookie = true;
            // Add Referer for sites that verify flow
            if (!string.IsNullOrWhiteSpace(tid))
            {
                var referer = Settings.BaseUrl.TrimEnd('/') + "/t/" + tid;
                request.Headers.Set("Referer", referer);
                _logger.Debug("MAM_DOWNLOAD: Added Referer header to {0}", referer);
            }
            _logger.Debug("MAM_DOWNLOAD: Created download request for {0}", downloadLink);
            _logger.Trace("MAM_DOWNLOAD: Cookie configured - mam_id present: {0}, length: {1}",
                !string.IsNullOrWhiteSpace(Settings.MamId),
                Settings.MamId?.Length ?? 0);
            _logger.Trace("MAM_DOWNLOAD: mam_ssl enabled: {0}", Settings.MamSsl);

            // Force the request to not follow redirects so we can see what MAM returns
            request.AllowAutoRedirect = false;

            return request;
        }

        // TaleArr: watch search replies for an expired session and for a rotated mam_id.
        protected override async Task<IndexerResponse> FetchIndexerResponse(IndexerRequest request)
        {
            var sentMamId = Settings.MamId;
            IndexerResponse response;

            try
            {
                response = await base.FetchIndexerResponse(request);
            }
            catch (HttpException ex) when (ex.Response != null)
            {
                if (ObserveSessionResponse(ex.Response, sentMamId, MyAnonaMouseCookieField.MamId, "search", true))
                {
                    throw new IndexerException(new IndexerResponse(request, ex.Response), SessionExpiredMessage);
                }

                throw;
            }

            if (ObserveSessionResponse(response.HttpResponse, sentMamId, MyAnonaMouseCookieField.MamId, "search", true))
            {
                throw new IndexerException(response, SessionExpiredMessage);
            }

            return response;
        }

        public override async Task<HttpResponse> ExecuteDownloadRequestAsync(HttpRequest request)
        {
            var response = await ExecuteDownloadAndObserveAsync(request);
            if (!HasFreeleechParameter(request?.Url?.FullUri) || IsTorrentResponse(response))
            {
                ConfirmServedTorrent(request, response);
                return response;
            }

            _logger.Debug("MAM did not return a torrent for the preferred-wedge request; retrying the download without forcing a wedge");
            var fallbackRequest = GetDownloadRequest(RemoveFreeleechParameter(request.Url.FullUri));
            fallbackRequest.RateLimitKey = request.RateLimitKey;
            fallbackRequest.RateLimit = request.RateLimit;
            fallbackRequest.RequestTimeout = request.RequestTimeout;
            fallbackRequest.Headers.Accept = request.Headers.Accept;

            var fallbackResponse = await ExecuteDownloadAndObserveAsync(fallbackRequest);
            ConfirmServedTorrent(fallbackRequest, fallbackResponse);
            return fallbackResponse;
        }

        private async Task<HttpResponse> ExecuteDownloadAndObserveAsync(HttpRequest request)
        {
            var sentMamId = Settings.MamId;

            try
            {
                var response = await base.ExecuteDownloadRequestAsync(request);
                ObserveSessionResponse(response, sentMamId, MyAnonaMouseCookieField.MamId, "download", false);
                return response;
            }
            catch (HttpException ex) when (ex.Response != null)
            {
                ObserveSessionResponse(ex.Response, sentMamId, MyAnonaMouseCookieField.MamId, "download", false);
                throw;
            }
        }

        // TaleArr: returns true when MAM rejected the session. Records the outcome for the health
        // check and keeps a rotated mam_id (Set-Cookie) both in memory and in the saved settings.
        internal bool ObserveSessionResponse(HttpResponse response, string sentMamId, MyAnonaMouseCookieField field, string source, bool inspectBody)
        {
            if (response == null || string.IsNullOrWhiteSpace(sentMamId))
            {
                return false;
            }

            var indexerId = Definition?.Id ?? 0;

            if (MyAnonaMouseSessionDetector.IsSessionRejected(response, inspectBody))
            {
                _sessionMonitor?.RecordSessionRejected(indexerId, sentMamId, source);
                return true;
            }

            if (MyAnonaMouseSessionDetector.TryGetRotatedMamId(response, sentMamId, out var rotated))
            {
                if (field == MyAnonaMouseCookieField.SeedboxMamId)
                {
                    if (string.Equals(Settings.SeedboxMamId, sentMamId, StringComparison.Ordinal))
                    {
                        Settings.SeedboxMamId = rotated;
                    }
                }
                else if (string.Equals(Settings.MamId, sentMamId, StringComparison.Ordinal))
                {
                    Settings.MamId = rotated;
                }

                _logger.Debug("MAM returned a new {0} during {1}", field == MyAnonaMouseCookieField.SeedboxMamId ? "seedbox mam_id" : "mam_id", source);
                _sessionMonitor?.RecordRotatedCookie(indexerId, field, sentMamId, rotated);

                if (field == MyAnonaMouseCookieField.MamId && response.StatusCode == HttpStatusCode.OK)
                {
                    _sessionMonitor?.RecordSessionAccepted(indexerId, sentMamId);
                }

                return false;
            }

            if (field == MyAnonaMouseCookieField.MamId && response.StatusCode == HttpStatusCode.OK)
            {
                _sessionMonitor?.RecordSessionAccepted(indexerId, sentMamId);
            }

            return false;
        }

        // TaleArr: tell MAM the torrent client's current public IP. The request has to leave from
        // that IP, so it goes through the Seedbox Proxy (or the indexer's own proxy).
        public async Task<MyAnonaMouseSeedboxResult> UpdateDynamicSeedboxAsync()
        {
            var mamId = string.IsNullOrWhiteSpace(Settings.SeedboxMamId) ? Settings.MamId : Settings.SeedboxMamId;
            var field = string.IsNullOrWhiteSpace(Settings.SeedboxMamId) ? MyAnonaMouseCookieField.MamId : MyAnonaMouseCookieField.SeedboxMamId;
            var now = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(mamId))
            {
                return new MyAnonaMouseSeedboxResult
                {
                    Outcome = MyAnonaMouseSeedboxOutcome.InvalidSession,
                    Message = "No mam_id configured",
                    CheckedUtc = now
                };
            }

            var request = new HttpRequestBuilder(MyAnonaMouseDynamicSeedboxParser.Endpoint)
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}")
                .Build();

            request.Cookies["mam_id"] = mamId;
            request.SuppressHttpError = true;
            request.LogHttpError = false;
            request.AllowAutoRedirect = false;
            request.StoreResponseCookie = false;
            request.StoreRequestCookie = false;

            // Own cookie jar scope: the search jar may hold a .myanonamouse.net mam_id that would
            // otherwise ride along with the seedbox session's cookie.
            request.RateLimitKey = $"mam-dynamic-seedbox-{Definition?.Id ?? 0}";

            HttpResponse response;
            try
            {
                response = await GetSeedboxHttpClient().ExecuteAsync(request);
            }
            catch (Exception ex)
            {
                return new MyAnonaMouseSeedboxResult
                {
                    Outcome = MyAnonaMouseSeedboxOutcome.Failed,
                    Message = $"Request failed ({ex.GetType().Name}: {ex.Message})",
                    CheckedUtc = now
                };
            }

            var result = MyAnonaMouseDynamicSeedboxParser.Parse(response.StatusCode, SafeContent(response), now);

            if (result.Outcome != MyAnonaMouseSeedboxOutcome.InvalidSession &&
                MyAnonaMouseSessionDetector.TryGetRotatedMamId(response, mamId, out _))
            {
                // Only the rotation part applies here: a seedbox refusal is reported through the
                // seedbox result, not as an expired search session.
                ObserveSessionResponse(response, mamId, field, "dynamic seedbox update", false);
            }

            return result;
        }

        internal IIndexerHttpClient GetSeedboxHttpClient()
        {
            var proxyId = Settings.SeedboxProxyId;
            if (proxyId == 0)
            {
                return _httpClient;
            }

            return _httpClientFactory.GetClient(proxyId < 0 ? IndexerDefinition.NoProxyOverride : proxyId);
        }

        private static string SafeContent(HttpResponse response)
        {
            try
            {
                return response?.Content;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getSeedboxProxies")
            {
                var options = new List<object>
                {
                    new { Value = 0, Name = "Same As Indexer" },
                    new { Value = IndexerDefinition.NoProxyOverride, Name = "No Proxy (direct)" }
                };

                if (_proxyService != null)
                {
                    options.AddRange(_proxyService.All()
                        .OrderBy(p => p.Name)
                        .Select(p => (object)new { Value = p.Id, Name = $"{p.Name} ({p.Hostname}:{p.Port})" }));
                }

                return new { options };
            }

            return base.RequestAction(action, query);
        }

        protected override async Task<ValidationFailure> TestConnection()
        {
            try
            {
                // MAM_TRACE: Log test attempt
                _logger.Trace("MAM_TEST_START: Testing MAM indexer connection");

                var generator = GetRequestGenerator();
                var parser = GetParser();

                var searchCriteria = new IndexerSearch.Definitions.BookSearchCriteria
                {
                    Author = new Books.Author { Name = "Test" },
                    BookTitle = "Test"
                };

                var requests = generator.GetSearchRequests(searchCriteria);
                var firstRequest = requests.GetAllTiers().FirstOrDefault()?.FirstOrDefault();

                if (firstRequest == null)
                {
                    _logger.Trace("MAM_TEST_ERROR: No request generated");
                    return new ValidationFailure(string.Empty, "Failed to generate test request. Check your settings.");
                }

                _logger.Trace("MAM_TEST_REQUEST: Testing JSON API connectivity (should use configured proxy if set)");
                var releases = await FetchPage(firstRequest, parser);

                _logger.Trace("MAM_TEST_SUCCESS: JSON API test successful, found {0} results", releases.Count);

                // The test result must not present cached counts as if this refresh verified them.
                try
                {
                    await RefreshAccountStatus();
                    if (!MamUnsatisfiedSlotGuard.HasFreshStatus(Settings, DateTime.UtcNow))
                    {
                        throw new InvalidOperationException("MAM account status is stale");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("MAM account status is unavailable or stale during test for indexer '{0}' ({1})", Definition.Name, ex.GetType().Name);
                    if (Settings.ProtectUnsatisfiedSlots)
                    {
                        return new ValidationFailure(string.Empty, "MAM account status is unavailable or stale. Cannot verify unsatisfied-slot protection.");
                    }

                    Definition.Message = new ProviderMessage(
                        "Connection succeeded, but MAM account status is unavailable or stale. Cached counts are not shown.",
                        ProviderMessageType.Warning);
                    return null;
                }

                // Provide a non-fatal, informational success message for the test modal
                try
                {
                    var cls = string.IsNullOrWhiteSpace(Settings.UserClass) ? "Unknown" : Settings.UserClass;
                    var membership = Settings.IsVip
                        ? $"\u2713 VIP Member - Class: {cls}"
                        : $"User Class: {cls} (Standard member)";
                    var slots = Settings.UnsatisfiedCount.HasValue && Settings.UnsatisfiedLimit.HasValue
                        ? $"; unsatisfied torrents: {Settings.UnsatisfiedCount}/{Settings.UnsatisfiedLimit}"
                        : string.Empty;
                    var message = membership + slots + MyAnonaMouseAccountStatus.DescribeWallet(Settings.FreeleechWedges, Settings.BonusPoints);

                    // Attach to the temporary definition used during Test so the controller can surface it
                    Definition.Message = new ProviderMessage(message, ProviderMessageType.Info);
                }
                catch { }

                return null; // Success
            }
            catch (global::System.Exception ex)
            {
                _logger.Trace("MAM_TEST_EXCEPTION: Test failed with exception - {0}", ex.Message);
                _logger.Warn(ex, "MAM indexer test failed");

                if (ex.Message.Contains("403") || ex.Message.Contains("Forbidden"))
                {
                    return new ValidationFailure(string.Empty, "Authentication failed. Verify your mam_id is correct (found under Preferences > Security on MAM). If using a proxy, ensure it's configured in Settings > General > Proxy.");
                }

                return new ValidationFailure(string.Empty, $"Connection test failed: {ex.Message}");
            }
        }

        public async Task<MyAnonaMouseAccountStatus> RefreshAccountStatus()
        {
            var request = new HttpRequestBuilder($"{Settings.BaseUrl.TrimEnd('/')}/jsonLoad.php?snatch_summary&pretty")
                .Accept(HttpAccept.Json)
                .SetHeader("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}")
                .Build();

            request.RateLimitKey = Definition?.Id > 0 ? Definition.Id.ToString() : null;
            request.Cookies["mam_id"] = Settings.MamId;
            if (Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1";
            }

            var sentMamId = Settings.MamId;
            HttpResponse response;
            try
            {
                response = await _httpClient.ExecuteAsync(request);
            }
            catch (HttpException ex) when (ex.Response != null)
            {
                if (ObserveSessionResponse(ex.Response, sentMamId, MyAnonaMouseCookieField.MamId, "account status refresh", true))
                {
                    throw new InvalidOperationException(SessionExpiredMessage, ex);
                }

                throw;
            }

            // TaleArr: expired sessions and rotated cookies (see ObserveSessionResponse).
            if (ObserveSessionResponse(response, sentMamId, MyAnonaMouseCookieField.MamId, "account status refresh", true))
            {
                throw new InvalidOperationException(SessionExpiredMessage);
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Failed to fetch MAM user data: HTTP {(int)response.StatusCode} {response.StatusCode}");
            }

            var userData = JsonConvert.DeserializeObject<MyAnonaMouseUserDataResponse>(response.Content);
            var className = userData?.ClassName;

            if (string.IsNullOrWhiteSpace(className))
            {
                throw new InvalidOperationException("MAM user data response did not include a user class");
            }

            // TaleArr: wedge balance and bonus points sit next to classname, outside snatch_summary.
            var wedges = MyAnonaMouseUserDataResponse.ReadInt(userData.Wedges);
            var bonusPoints = MyAnonaMouseUserDataResponse.ReadNumber(userData.SeedBonus);

            userData = userData.SnatchSummary ?? userData;

            if (userData.Unsatisfied == null || userData.Unsatisfied.Count < 0 || userData.Unsatisfied.Limit <= 0 || userData.Created <= 0)
            {
                throw new InvalidOperationException("MAM user data response did not include a valid unsatisfied-torrent summary");
            }

            DateTime snapshotCreatedUtc;
            try
            {
                snapshotCreatedUtc = DateTimeOffset.FromUnixTimeSeconds(userData.Created).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new InvalidOperationException("MAM user data response included an invalid summary timestamp", ex);
            }

            var refreshedUtc = DateTime.UtcNow;
            var isVip = IsVipClass(className);
            Settings.UserClass = className;
            Settings.IsVip = isVip;
            Settings.UnsatisfiedCount = userData.Unsatisfied.Count;
            Settings.UnsatisfiedLimit = userData.Unsatisfied.Limit;
            Settings.UnsatisfiedSnapshotUtc = snapshotCreatedUtc;
            Settings.UnsatisfiedStatusRefreshedUtc = refreshedUtc;
            Settings.FreeleechWedges = wedges;
            Settings.BonusPoints = bonusPoints;

            _logger.Debug("MAM account status detected: {0} (VIP: {1}), unsatisfied {2}/{3}", className, isVip, userData.Unsatisfied.Count, userData.Unsatisfied.Limit);

            return new MyAnonaMouseAccountStatus
            {
                UserClass = className,
                IsVip = isVip,
                UnsatisfiedCount = userData.Unsatisfied.Count,
                UnsatisfiedLimit = userData.Unsatisfied.Limit,
                SnapshotCreatedUtc = snapshotCreatedUtc,
                RefreshedUtc = refreshedUtc,
                FreeleechWedges = wedges,
                BonusPoints = bonusPoints
            };
        }

        private static bool IsVipClass(string className)
        {
            return className.IndexOf("VIP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasFreeleechParameter(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)[0])
                .Any(key => string.Equals(Uri.UnescapeDataString(key), "fl", StringComparison.OrdinalIgnoreCase));
        }

        private static string RemoveFreeleechParameter(string url)
        {
            var uri = new Uri(url);
            var queryString = string.Join(
                "&",
                uri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(part => !string.Equals(
                        Uri.UnescapeDataString(part.Split('=', 2)[0]),
                        "fl",
                        StringComparison.OrdinalIgnoreCase)));

            return uri.GetLeftPart(UriPartial.Path) +
                   (string.IsNullOrWhiteSpace(queryString) ? string.Empty : "?" + queryString);
        }

        private void ConfirmServedTorrent(HttpRequest request, HttpResponse response)
        {
            if (_slotReservationRepository == null ||
                Definition?.Id <= 0 ||
                !IsTorrentResponse(response) ||
                !Uri.TryCreate(request?.Url?.FullUri, UriKind.Absolute, out var uri))
            {
                return;
            }

            var torrentId = System.Web.HttpUtility.ParseQueryString(uri.Query)["tid"];
            if (string.IsNullOrWhiteSpace(torrentId))
            {
                return;
            }

            var reservation = _slotReservationRepository.Find(Definition.Id, torrentId);
            if (reservation == null || reservation.ConfirmedUtc.HasValue)
            {
                return;
            }

            reservation.ConfirmedUtc = DateTime.UtcNow;
            _slotReservationRepository.Update(reservation);
            _logger.Debug(
                "Confirmed MAM slot reservation for torrent {0} on indexer '{1}' when MAM returned a valid torrent payload",
                torrentId,
                Definition.Name);
        }

        private static bool IsTorrentResponse(HttpResponse response)
        {
            if (response?.StatusCode != HttpStatusCode.OK || response.ResponseData?.Length == 0)
            {
                return false;
            }

            try
            {
                Torrent.Load(response.ResponseData);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public class MyAnonaMouseAccountStatus
    {
        public string UserClass { get; set; }
        public bool IsVip { get; set; }
        public int UnsatisfiedCount { get; set; }
        public int UnsatisfiedLimit { get; set; }
        public DateTime SnapshotCreatedUtc { get; set; }
        public DateTime RefreshedUtc { get; set; }

        // TaleArr: null when MAM did not send them.
        public int? FreeleechWedges { get; set; }
        public double? BonusPoints { get; set; }

        public static string DescribeWallet(int? wedges, double? bonusPoints)
        {
            var parts = new List<string>();
            if (wedges.HasValue)
            {
                parts.Add($"freeleech wedges: {wedges.Value}");
            }

            if (bonusPoints.HasValue)
            {
                parts.Add($"bonus points: {Math.Floor(bonusPoints.Value).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            return parts.Count == 0 ? string.Empty : "; " + string.Join(", ", parts);
        }
    }

    public class MyAnonaMouseUserDataResponse
    {
        [JsonProperty("snatch_summary")]
        public MyAnonaMouseUserDataResponse SnatchSummary { get; set; }

        [JsonProperty("classname")]
        public string ClassName { get; set; }

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("unsat")]
        public MyAnonaMouseUnsatisfiedSummary Unsatisfied { get; set; }

        // TaleArr: optional and loosely typed (number or string), never required.
        [JsonProperty("wedges")]
        public Newtonsoft.Json.Linq.JToken Wedges { get; set; }

        [JsonProperty("seedbonus")]
        public Newtonsoft.Json.Linq.JToken SeedBonus { get; set; }

        public static double? ReadNumber(Newtonsoft.Json.Linq.JToken token)
        {
            if (token == null || token.Type is Newtonsoft.Json.Linq.JTokenType.Null or Newtonsoft.Json.Linq.JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type is Newtonsoft.Json.Linq.JTokenType.Integer or Newtonsoft.Json.Linq.JTokenType.Float)
            {
                var value = token.ToObject<double>();
                return value >= 0 ? value : null;
            }

            if (token.Type == Newtonsoft.Json.Linq.JTokenType.String &&
                double.TryParse(token.ToObject<string>()?.Replace(",", string.Empty).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
                parsed >= 0)
            {
                return parsed;
            }

            return null;
        }

        public static int? ReadInt(Newtonsoft.Json.Linq.JToken token)
        {
            var value = ReadNumber(token);
            return value.HasValue && value.Value <= int.MaxValue ? (int)Math.Floor(value.Value) : null;
        }
    }

    public class MyAnonaMouseUnsatisfiedSummary
    {
        [JsonProperty("count", Required = Required.Always)]
        public int Count { get; set; }

        [JsonProperty("limit")]
        public int Limit { get; set; }
    }

}
