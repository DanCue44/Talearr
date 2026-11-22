using System;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public interface ITalearrWishlistUrlFetcher
    {
        string Fetch(string url);
    }

    // Talearr: reads a URL list (TalearrWishlistKinds.UrlList). A plain GET, no sign-in; the
    // answer must be text and at most MaxLength characters.
    public class TalearrWishlistUrlFetcher : ITalearrWishlistUrlFetcher
    {
        public const int MaxLength = 2 * 1024 * 1024;

        private readonly IHttpClient _httpClient;

        public TalearrWishlistUrlFetcher(IHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public static bool IsValidUrl(string url)
        {
            return Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        public string Fetch(string url)
        {
            if (!IsValidUrl(url))
            {
                throw new TalearrWishlistException("Use an http or https URL for the list.");
            }

            HttpResponse response;
            try
            {
                var request = new HttpRequestBuilder(url.Trim())
                    .SetHeader("Accept", "text/plain, application/json, */*")
                    .Build();

                request.AllowAutoRedirect = true;
                request.SuppressHttpError = true;
                request.RequestTimeout = TimeSpan.FromSeconds(30);

                response = _httpClient.Execute(request);
            }
            catch (Exception ex) when (ex is HttpException || ex is System.Net.WebException || ex is System.Net.Http.HttpRequestException || ex is TimeoutException || ex is System.Threading.Tasks.TaskCanceledException)
            {
                throw new TalearrWishlistException("The list URL could not be read: " + ex.Message);
            }

            if (response.HasHttpError)
            {
                throw new TalearrWishlistException($"The list URL answered {(int)response.StatusCode}.");
            }

            var content = response.Content ?? string.Empty;
            if (content.Length > MaxLength)
            {
                throw new TalearrWishlistException("The list is larger than 2 MB.");
            }

            return content;
        }
    }
}
