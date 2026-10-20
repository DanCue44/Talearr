using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.Soulseek
{
    public interface ITalearrSlskdClient
    {
        SlskdApplicationState GetApplication(TalearrSoulseekSettings settings);
        SlskdSearch StartSearch(TalearrSoulseekSettings settings, string id, string searchText);
        SlskdSearch GetSearch(TalearrSoulseekSettings settings, string id);
        List<SlskdSearchResponse> GetSearchResponses(TalearrSoulseekSettings settings, string id);
        void DeleteSearch(TalearrSoulseekSettings settings, string id);
        SlskdEnqueueResult Enqueue(TalearrSoulseekSettings settings, string username, List<SlskdQueueFile> files, string batchId, string destination);
        List<SlskdTransfer> GetDownloads(TalearrSoulseekSettings settings, string username);
        void CancelDownload(TalearrSoulseekSettings settings, string username, string transferId, bool remove);
    }

    // Talearr: a small client for slskd's REST API (https://github.com/slskd/slskd, api/v0),
    // authenticated with an slskd API key (X-API-Key). Used only by the opt-in Soulseek feature.
    public class TalearrSlskdClient : ITalearrSlskdClient
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        internal static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public TalearrSlskdClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public SlskdApplicationState GetApplication(TalearrSoulseekSettings settings)
        {
            return Get<SlskdApplicationState>(settings, "/api/v0/application") ?? new SlskdApplicationState();
        }

        public SlskdSearch StartSearch(TalearrSoulseekSettings settings, string id, string searchText)
        {
            var body = new
            {
                id,
                searchText,
                searchTimeout = Math.Max(5, settings.SearchTimeoutSeconds) * 1000,
                filterResponses = true,
                minimumResponseFileCount = 1,
                maximumPeerQueueLength = settings.MaxPeerQueueLength > 0 ? settings.MaxPeerQueueLength : (int?)null,
                minimumPeerUploadSpeed = settings.MinUploadSpeedKbps > 0 ? settings.MinUploadSpeedKbps * 1024 : (int?)null,
                responseLimit = 200,
                fileLimit = 20000
            };

            var response = Send(settings, HttpMethod.Post, "/api/v0/searches", body);
            return Parse<SlskdSearch>(response.Content) ?? new SlskdSearch { Id = id };
        }

        public SlskdSearch GetSearch(TalearrSoulseekSettings settings, string id)
        {
            return Get<SlskdSearch>(settings, $"/api/v0/searches/{Uri.EscapeDataString(id)}");
        }

        public List<SlskdSearchResponse> GetSearchResponses(TalearrSoulseekSettings settings, string id)
        {
            return Get<List<SlskdSearchResponse>>(settings, $"/api/v0/searches/{Uri.EscapeDataString(id)}/responses") ?? new List<SlskdSearchResponse>();
        }

        public void DeleteSearch(TalearrSoulseekSettings settings, string id)
        {
            try
            {
                Send(settings, HttpMethod.Delete, $"/api/v0/searches/{Uri.EscapeDataString(id)}", null, suppressErrors: true);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not delete slskd search {0}", id);
            }
        }

        public SlskdEnqueueResult Enqueue(TalearrSoulseekSettings settings, string username, List<SlskdQueueFile> files, string batchId, string destination)
        {
            // slskd 0.26 and later: a batch with an explicit destination folder, so every grab gets
            // a folder of its own. Older versions answer 404/405 and get the plain enqueue call; the
            // files then land wherever slskd's own destination pattern puts them.
            if (destination.IsNotNullOrWhiteSpace())
            {
                var batch = new SlskdBatchRequest
                {
                    Id = batchId,
                    Username = username,
                    Files = files,
                    Options = new SlskdBatchOptions { Destination = destination, ExternalId = batchId }
                };

                var response = Send(settings, HttpMethod.Post, "/api/v0/transfers/downloads/batches", batch, suppressErrors: true);
                if (!response.HasHttpError)
                {
                    return new SlskdEnqueueResult { UsedBatchDestination = true, Destination = destination };
                }

                if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.MethodNotAllowed)
                {
                    throw new HttpException(response);
                }

                _logger.Debug("Talearr: slskd has no batch downloads (status {0}); using the plain enqueue call", (int)response.StatusCode);
            }

            Send(settings, HttpMethod.Post, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", files);
            return new SlskdEnqueueResult { UsedBatchDestination = false };
        }

        public List<SlskdTransfer> GetDownloads(TalearrSoulseekSettings settings, string username)
        {
            var response = Send(settings, HttpMethod.Get, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", null, suppressErrors: true);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new List<SlskdTransfer>();
            }

            if (response.HasHttpError)
            {
                throw new HttpException(response);
            }

            var user = Parse<SlskdUserTransfers>(response.Content);
            return (user?.Directories ?? new List<SlskdTransferDirectory>())
                .Where(d => d?.Files != null)
                .SelectMany(d => d.Files)
                .Where(f => f != null)
                .ToList();
        }

        public void CancelDownload(TalearrSoulseekSettings settings, string username, string transferId, bool remove)
        {
            Send(settings, HttpMethod.Delete, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}/{Uri.EscapeDataString(transferId)}?remove={(remove ? "true" : "false")}", null, suppressErrors: true);
        }

        private T Get<T>(TalearrSoulseekSettings settings, string resource)
            where T : class
        {
            var response = Send(settings, HttpMethod.Get, resource, null);
            return Parse<T>(response.Content);
        }

        private HttpResponse Send(TalearrSoulseekSettings settings, HttpMethod method, string resource, object body, bool suppressErrors = false)
        {
            if (settings == null || settings.Url.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException("slskd URL is not set");
            }

            var request = new HttpRequestBuilder(settings.Url.TrimEnd('/'))
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = method;
            request.RequestTimeout = RequestTimeout;
            request.SuppressHttpError = true;
            request.LogHttpError = false;
            request.Headers.Add("User-Agent", "Talearr");

            if (settings.ApiKey.IsNotNullOrWhiteSpace())
            {
                request.Headers.Add("X-API-Key", settings.ApiKey);
            }

            if (body != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(JsonSerializer.Serialize(body, JsonOptions));
            }

            var response = _httpClient.Execute(request);

            if (response.HasHttpError && !suppressErrors)
            {
                throw new HttpException(response);
            }

            return response;
        }

        internal static T Parse<T>(string json)
            where T : class
        {
            if (json.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
