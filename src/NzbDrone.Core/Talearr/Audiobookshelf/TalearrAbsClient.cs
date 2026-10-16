using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    public interface ITalearrAbsClient
    {
        // GET /api/libraries/{id}/items, every page, minified. Read only.
        List<AbsLibraryItem> GetLibraryItems(AudioBookShelfSettings settings, string libraryId);
    }

    // TaleArr: reads an Audiobookshelf library. Only GET requests, with the connection's own API
    // key; nothing is written to Audiobookshelf (scans after import stay upstream's job).
    public class TalearrAbsClient : ITalearrAbsClient
    {
        public const int PageSize = 200;

        // 200 x 1000 = 200,000 items: far beyond a real library, only a guard against a server
        // that never reports its total.
        public const int MaxPages = 1000;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public TalearrAbsClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public List<AbsLibraryItem> GetLibraryItems(AudioBookShelfSettings settings, string libraryId)
        {
            if (libraryId.IsNullOrWhiteSpace())
            {
                return new List<AbsLibraryItem>();
            }

            var items = new List<AbsLibraryItem>();
            for (var page = 0; page < MaxPages; page++)
            {
                var request = BuildRequest(settings, $"/api/libraries/{Uri.EscapeDataString(libraryId)}/items", page);
                var response = _httpClient.Execute(request);

                if (response.HasHttpError)
                {
                    throw new HttpException(response);
                }

                var body = Json.Deserialize<AbsLibraryItemsPage>(response.Content) ?? new AbsLibraryItemsPage();
                var results = body.Results ?? new List<AbsLibraryItem>();
                items.AddRange(results.Where(r => r != null));

                if (results.Count == 0 || results.Count < PageSize || (body.Total > 0 && items.Count >= body.Total))
                {
                    break;
                }
            }

            _logger.Debug("TaleArr: read {0} items from Audiobookshelf library {1}", items.Count, libraryId);
            return items;
        }

        // The Test button: with Sync Library on, check that each library TaleArr would read lets
        // this API key list its items (one item per library, GET only). Null when all is well.
        public static ValidationFailure TestLibraryRead(IHttpClient httpClient, AudioBookShelfSettings settings)
        {
            if (settings == null || !settings.TalearrSyncLibrary)
            {
                return null;
            }

            try
            {
                var librariesRequest = new HttpRequestBuilder(BuildBaseUrl(settings)).Resource("/api/libraries").Build();
                librariesRequest.RequestTimeout = RequestTimeout;
                librariesRequest.Headers.Add("User-Agent", "TaleArr");
                librariesRequest.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");

                var librariesResponse = httpClient.Execute(librariesRequest);
                if (librariesResponse.HasHttpError)
                {
                    throw new HttpException(librariesResponse);
                }

                var libraries = Json.Deserialize<AbsLibrariesResponse>(librariesResponse.Content)?.Libraries ?? new List<AudioBookShelfLibrary>();
                var books = libraries.Where(l => l != null && string.Equals(l.MediaType, "book", StringComparison.OrdinalIgnoreCase)).ToList();
                var selected = TalearrAbsSyncService.SelectLibraries(settings, books);

                if (selected.Count == 0)
                {
                    return new ValidationFailure(nameof(AudioBookShelfSettings.TalearrSyncLibrary), "TaleArr found no Audiobookshelf book library to read. Map a library below or turn off Sync Library");
                }

                foreach (var library in selected)
                {
                    var request = BuildRequest(settings, $"/api/libraries/{Uri.EscapeDataString(library.Id)}/items", 0, 1);
                    var response = httpClient.Execute(request);
                    if (response.HasHttpError)
                    {
                        return new ValidationFailure(nameof(AudioBookShelfSettings.TalearrSyncLibrary), $"TaleArr cannot read the items of Audiobookshelf library '{library.Name}' (status {(int)response.StatusCode}). The API key's user needs access to it");
                    }
                }

                return null;
            }
            catch (HttpException ex)
            {
                return new ValidationFailure(nameof(AudioBookShelfSettings.TalearrSyncLibrary), $"TaleArr cannot read the Audiobookshelf library (status {(int)ex.Response.StatusCode})");
            }
            catch (Exception ex)
            {
                return new ValidationFailure(nameof(AudioBookShelfSettings.TalearrSyncLibrary), $"TaleArr cannot read the Audiobookshelf library: {ex.Message}");
            }
        }

        public static string BuildBaseUrl(AudioBookShelfSettings settings)
        {
            if (settings == null || settings.Host.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("Audiobookshelf Host must be configured", nameof(settings));
            }

            return HttpRequestBuilder.BuildBaseUrl(settings.UseSsl, settings.Host.ToUrlHost(), settings.Port, settings.UrlBase);
        }

        private static HttpRequest BuildRequest(AudioBookShelfSettings settings, string resource, int page, int limit = PageSize)
        {
            if (settings.ApiKey.IsNullOrWhiteSpace())
            {
                throw new ArgumentException("Audiobookshelf API Key must be configured", nameof(settings));
            }

            var builder = new HttpRequestBuilder(BuildBaseUrl(settings))
                .Resource(resource)
                .AddQueryParam("limit", limit)
                .AddQueryParam("page", page)
                .AddQueryParam("minified", 1);

            builder.HttpAccept = HttpAccept.Json;

            var request = builder.Build();

            request.RequestTimeout = RequestTimeout;
            request.Headers.Add("User-Agent", "TaleArr");
            request.Headers.Add("Authorization", $"Bearer {settings.ApiKey}");

            return request;
        }
    }

    // Audiobookshelf API shapes (2.x, GET /api/libraries/{id}/items?minified=1). Only the fields
    // TaleArr reads; anything else in the response is ignored.
    public class AbsLibrariesResponse
    {
        public List<AudioBookShelfLibrary> Libraries { get; set; } = new List<AudioBookShelfLibrary>();
    }

    public class AbsLibraryItemsPage
    {
        public List<AbsLibraryItem> Results { get; set; } = new List<AbsLibraryItem>();
        public int Total { get; set; }
        public int Limit { get; set; }
        public int Page { get; set; }
    }

    public class AbsLibraryItem
    {
        public string Id { get; set; }
        public string LibraryId { get; set; }
        public string FolderId { get; set; }
        public string Path { get; set; }
        public string RelPath { get; set; }
        public bool IsFile { get; set; }
        public bool IsMissing { get; set; }
        public bool IsInvalid { get; set; }
        public string MediaType { get; set; }
        public long? AddedAt { get; set; }
        public long? UpdatedAt { get; set; }
        public AbsBookMedia Media { get; set; }
    }

    public class AbsBookMedia
    {
        public AbsBookMetadata Metadata { get; set; }
        public int NumTracks { get; set; }
        public int NumAudioFiles { get; set; }
        public double? Duration { get; set; }
        public string EbookFormat { get; set; }
    }

    public class AbsBookMetadata
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string AuthorName { get; set; }
        public string NarratorName { get; set; }
        public string SeriesName { get; set; }
        public string Isbn { get; set; }
        public string Asin { get; set; }
        public string Language { get; set; }
        public bool? Abridged { get; set; }
    }
}
