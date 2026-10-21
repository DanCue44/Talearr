using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class GrimmoryLibrary
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public bool Watch { get; set; }
        public List<GrimmoryLibraryPath> Paths { get; set; } = new List<GrimmoryLibraryPath>();
        public List<string> AllowedFormats { get; set; } = new List<string>();
    }

    public class GrimmoryLibraryPath
    {
        public long Id { get; set; }
        public string Path { get; set; }
    }

    public interface IGrimmoryProxy
    {
        List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings);
        void RefreshLibrary(GrimmorySettings settings, long libraryId);
    }

    // Talearr: Grimmory REST API (https://grimmory.org/api/). Login returns a short-lived access
    // token and a refresh token; both are kept in memory per server and user, refreshed on a 401,
    // and a fresh login is the last resort.
    public class GrimmoryProxy : IGrimmoryProxy
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        private static readonly ConcurrentDictionary<string, GrimmoryTokens> Tokens = new ConcurrentDictionary<string, GrimmoryTokens>();

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public GrimmoryProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<GrimmoryLibrary> GetLibraries(GrimmorySettings settings)
        {
            var response = Execute(settings, HttpMethod.Get, "api/v1/libraries", null);
            return Json.Deserialize<List<GrimmoryLibrary>>(response.Content) ?? new List<GrimmoryLibrary>();
        }

        public void RefreshLibrary(GrimmorySettings settings, long libraryId)
        {
            Execute(settings, HttpMethod.Put, $"api/v1/libraries/{libraryId}/refresh", null);
            _logger.Debug("Grimmory: rescan of library {0} requested", libraryId);
        }

        private HttpResponse Execute(GrimmorySettings settings, HttpMethod method, string resource, object body)
        {
            var key = TokenKey(settings);

            var tokens = Tokens.GetOrAdd(key, _ => Login(settings));

            try
            {
                return Send(settings, method, resource, body, tokens.AccessToken);
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.Unauthorized || ex.Response.StatusCode == HttpStatusCode.Forbidden)
            {
                tokens = Renew(settings, tokens);
                Tokens[key] = tokens;
                return Send(settings, method, resource, body, tokens.AccessToken);
            }
        }

        private GrimmoryTokens Renew(GrimmorySettings settings, GrimmoryTokens tokens)
        {
            if (!string.IsNullOrWhiteSpace(tokens?.RefreshToken))
            {
                try
                {
                    var response = Send(settings, HttpMethod.Post, "api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken }, null);
                    var refreshed = Json.Deserialize<GrimmoryTokens>(response.Content);

                    if (!string.IsNullOrWhiteSpace(refreshed?.AccessToken))
                    {
                        return refreshed;
                    }
                }
                catch (HttpException ex)
                {
                    _logger.Debug(ex, "Grimmory: token refresh failed, signing in again");
                }
            }

            return Login(settings);
        }

        private GrimmoryTokens Login(GrimmorySettings settings)
        {
            try
            {
                var response = Send(settings, HttpMethod.Post, "api/v1/auth/login", new { username = settings.Username, password = settings.Password }, null);
                var tokens = Json.Deserialize<GrimmoryTokens>(response.Content);

                if (string.IsNullOrWhiteSpace(tokens?.AccessToken))
                {
                    throw new GrimmoryException("Grimmory did not return an access token");
                }

                return tokens;
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.Unauthorized || ex.Response.StatusCode == HttpStatusCode.Forbidden || ex.Response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new GrimmoryException("Grimmory did not accept the username or password", ex);
            }
        }

        private HttpResponse Send(GrimmorySettings settings, HttpMethod method, string resource, object body, string accessToken)
        {
            var request = new HttpRequestBuilder(settings.Url.Trim().TrimEnd('/'))
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = method;
            request.RequestTimeout = RequestTimeout;
            request.Headers.Add("User-Agent", "Talearr");

            if (accessToken != null)
            {
                request.Headers.Add("Authorization", $"Bearer {accessToken}");
            }

            if (body != null)
            {
                request.Headers.ContentType = "application/json";
                request.SetContent(body.ToJson());
            }

            return _httpClient.Execute(request);
        }

        private static string TokenKey(GrimmorySettings settings)
        {
            return $"{settings.Url?.Trim().TrimEnd('/').ToLowerInvariant()}|{settings.Username}|{settings.Password?.GetHashCode()}";
        }

        private class GrimmoryTokens
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
        }
    }

    public class GrimmoryException : Exception
    {
        public GrimmoryException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }
}
