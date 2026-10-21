using System.Collections.Generic;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Notifications.MediaBrowser
{
    public interface IMediaBrowserProxy
    {
        string GetServerName(MediaBrowserSettings settings);
        void UpdateFolders(MediaBrowserSettings settings, IEnumerable<string> paths, string updateType);
    }

    public class MediaBrowserProxy : IMediaBrowserProxy
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public MediaBrowserProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public string GetServerName(MediaBrowserSettings settings)
        {
            var request = BuildRequest(settings, "System/Info", HttpMethod.Get).Build();
            var response = _httpClient.Execute(request);
            var info = Json.Deserialize<Dictionary<string, object>>(response.Content);

            return info != null && info.TryGetValue("ServerName", out var name) ? name?.ToString() : null;
        }

        // Library/Media/Updated rescans only the given folders, on Emby and Jellyfin alike.
        public void UpdateFolders(MediaBrowserSettings settings, IEnumerable<string> paths, string updateType)
        {
            var updates = new List<object>();

            foreach (var path in paths)
            {
                updates.Add(new { Path = path, UpdateType = updateType });
            }

            if (updates.Count == 0)
            {
                return;
            }

            var request = BuildRequest(settings, "Library/Media/Updated", HttpMethod.Post);
            request.Headers.ContentType = "application/json";

            var built = request.Build();
            built.SetContent(new { Updates = updates }.ToJson());

            _httpClient.Execute(built);
            _logger.Debug("Asked {0} to rescan {1} folder(s)", settings.Host, updates.Count);
        }

        private static HttpRequestBuilder BuildRequest(MediaBrowserSettings settings, string resource, HttpMethod method)
        {
            var baseUrl = HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host, settings.Port, settings.UrlBase ?? string.Empty);
            var builder = new HttpRequestBuilder(baseUrl).Resource(resource);
            builder.Method = method;
            builder.Headers["X-MediaBrowser-Token"] = settings.ApiKey;
            builder.Accept(HttpAccept.Json);

            return builder;
        }
    }
}
