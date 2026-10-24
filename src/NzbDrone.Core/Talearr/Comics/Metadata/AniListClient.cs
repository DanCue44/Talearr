using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.Comics.Metadata
{
    public class AniListException : NzbDroneException
    {
        public AniListException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class AniListManga
    {
        public int Id { get; set; }
        public string RomajiTitle { get; set; }
        public string EnglishTitle { get; set; }
        public string NativeTitle { get; set; }
        public List<string> Synonyms { get; set; } = new List<string>();
        public string Format { get; set; }
        public string Status { get; set; }
        public int? Volumes { get; set; }
        public int? Chapters { get; set; }
        public bool IsAdult { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public List<(string Name, int Rank, bool IsAdult)> Tags { get; set; } = new List<(string, int, bool)>();
        public string CountryOfOrigin { get; set; }
        public int? StartYear { get; set; }
        public string CoverUrl { get; set; }

        // Only filled by GetPreview (the wide header image on the AniList page).
        public string BannerUrl { get; set; }
        public string Description { get; set; }
        public List<string> Authors { get; set; } = new List<string>();

        // The name English readers know: the licensed English title, else romaji.
        public string DisplayTitle => !string.IsNullOrWhiteSpace(EnglishTitle) ? EnglishTitle : RomajiTitle;
    }

    public interface IAniListClient
    {
        List<AniListManga> Search(string query);
        AniListManga Get(int id);

        // One series with the extra facts a detail page shows (banner image). Null when AniList
        // has no manga with that id.
        AniListManga GetPreview(int id);
        List<AniListManga> Browse(string sort, string status, int page, int perPage);
    }

    // Talearr: AniList's public GraphQL API (https://docs.anilist.co) for manga series facts:
    // titles in every script, status, volume count, the adult flag and tags used to guess an age
    // rating. No key needed. AniList allows 90 requests a minute (30 while degraded), so requests
    // are spaced out; Talearr only calls it when a series is searched, added or refreshed.
    public class AniListClient : IAniListClient
    {
        public const string Endpoint = "https://graphql.anilist.co";
        private static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(2.1);
        private static readonly object Gate = new object();
        private static DateTime _lastRequest = DateTime.MinValue;

        internal const string MediaFields = @"
            id
            title { romaji english native }
            synonyms
            format
            status
            volumes
            chapters
            isAdult
            genres
            countryOfOrigin
            startDate { year }
            coverImage { extraLarge large }
            description(asHtml: false)
            tags { name rank isAdult }
            staff(perPage: 4, sort: RELEVANCE) { edges { role node { name { full } } } }";

        private static readonly string SearchQuery = @"
            query TalearrMangaSearch($search: String) {
                Page(perPage: 15) {
                    media(search: $search, type: MANGA, sort: SEARCH_MATCH) {" + MediaFields + @"
                    }
                }
            }";

        private static readonly string GetQuery = @"
            query TalearrManga($id: Int) {
                Media(id: $id, type: MANGA) {" + MediaFields + @"
                }
            }";

        // The detail page of one series: the usual facts plus the banner image.
        private static readonly string PreviewQuery = @"
            query TalearrMangaPreview($id: Int) {
                Media(id: $id, type: MANGA) {" + MediaFields + @"
                    bannerImage
                }
            }";

        // Discover lists: one page of manga in AniList's own order (trending, popular), adult
        // entries and light novels left out. A null status is ignored by AniList.
        private static readonly string BrowseQuery = @"
            query TalearrMangaBrowse($page: Int, $perPage: Int, $sort: [MediaSort], $status: MediaStatus) {
                Page(page: $page, perPage: $perPage) {
                    media(type: MANGA, sort: $sort, status: $status, isAdult: false, format_not: NOVEL) {" + MediaFields + @"
                    }
                }
            }";

        private readonly IHttpClient _httpClient;

        public AniListClient(IHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public List<AniListManga> Search(string query)
        {
            var data = Execute(SearchQuery, new { search = query });
            return (data?["Page"]?["media"] as JArray ?? new JArray())
                .Select(ParseMedia)
                .Where(m => m != null && m.Format != "NOVEL")
                .ToList();
        }

        public AniListManga Get(int id)
        {
            var data = Execute(GetQuery, new { id });
            return ParseMedia(data?["Media"]);
        }

        public AniListManga GetPreview(int id)
        {
            var data = Execute(PreviewQuery, new { id });
            return ParseMedia(data?["Media"]);
        }

        public List<AniListManga> Browse(string sort, string status, int page, int perPage)
        {
            var data = Execute(BrowseQuery, new { page = Math.Max(1, page), perPage, sort = new[] { sort }, status });
            return ParsePage(data);
        }

        // The media of a Page answer, in AniList's order, novels and adult entries dropped.
        internal static List<AniListManga> ParsePage(JToken data)
        {
            return (data?["Page"]?["media"] as JArray ?? new JArray())
                .Select(ParseMedia)
                .Where(m => m != null && m.Format != "NOVEL" && !m.IsAdult)
                .ToList();
        }

        internal static AniListManga ParseMedia(JToken media)
        {
            if (media == null || media.Type != JTokenType.Object)
            {
                return null;
            }

            var manga = new AniListManga
            {
                Id = media.Value<int>("id"),
                RomajiTitle = media["title"]?.Value<string>("romaji"),
                EnglishTitle = media["title"]?.Value<string>("english"),
                NativeTitle = media["title"]?.Value<string>("native"),
                Synonyms = (media["synonyms"] as JArray)?.Values<string>().Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>(),
                Format = media.Value<string>("format"),
                Status = media.Value<string>("status"),
                Volumes = media.Value<int?>("volumes"),
                Chapters = media.Value<int?>("chapters"),
                IsAdult = media.Value<bool?>("isAdult") ?? false,
                Genres = (media["genres"] as JArray)?.Values<string>().ToList() ?? new List<string>(),
                CountryOfOrigin = media.Value<string>("countryOfOrigin"),
                StartYear = media["startDate"]?.Value<int?>("year"),
                CoverUrl = media["coverImage"]?.Value<string>("extraLarge") ?? media["coverImage"]?.Value<string>("large"),
                BannerUrl = media.Value<string>("bannerImage"),
                Description = CleanDescription(media.Value<string>("description"))
            };

            foreach (var tag in media["tags"] as JArray ?? new JArray())
            {
                manga.Tags.Add((tag.Value<string>("name"), tag.Value<int?>("rank") ?? 0, tag.Value<bool?>("isAdult") ?? false));
            }

            foreach (var edge in media["staff"]?["edges"] as JArray ?? new JArray())
            {
                var role = edge.Value<string>("role") ?? string.Empty;
                var name = edge["node"]?["name"]?.Value<string>("full");
                if (!string.IsNullOrWhiteSpace(name) && (role.Contains("Story", StringComparison.OrdinalIgnoreCase) || role.Contains("Art", StringComparison.OrdinalIgnoreCase)) && !manga.Authors.Contains(name))
                {
                    manga.Authors.Add(name);
                }
            }

            return manga;
        }

        // Plain text from AniList's description: line breaks kept, tags (<br>, <i>, <b>) and the
        // "(Source: ...)" credit dropped, entities decoded, runs of blank lines squeezed to one.
        internal static string CleanDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return null;
            }

            var text = Regex.Replace(description, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", string.Empty);
            text = Regex.Replace(text, "\\(Source: [^)]*\\)", string.Empty, RegexOptions.IgnoreCase);
            text = WebUtility.HtmlDecode(text).Replace("\r\n", "\n");
            text = Regex.Replace(text, "[ \\t]+\n", "\n");
            text = Regex.Replace(text, "\n{3,}", "\n\n").Trim();
            return text.Length == 0 ? null : text;
        }

        private JToken Execute(string query, object variables)
        {
            lock (Gate)
            {
                var wait = _lastRequest + MinimumGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }

                _lastRequest = DateTime.UtcNow;
            }

            var request = new HttpRequestBuilder(Endpoint)
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", "Talearr (+https://talearr.com)")
                .Build();

            request.Method = HttpMethod.Post;
            request.SuppressHttpError = true;
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            request.SetContent(JsonConvert.SerializeObject(new { query, variables }));

            var response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new AniListException("AniList is busy (too many requests). Try again in a minute.");
            }

            JObject body = null;
            try
            {
                body = JObject.Parse(response.Content ?? string.Empty);
            }
            catch (JsonException)
            {
            }

            if (response.HasHttpError && response.StatusCode != HttpStatusCode.NotFound)
            {
                // AniList explains outages in the body ("temporarily disabled ...").
                var reason = (body?["errors"] as JArray)?.First?.Value<string>("message");
                throw new AniListException("AniList is unavailable ({0}){1}", (int)response.StatusCode, string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason);
            }

            if (body == null)
            {
                throw new AniListException("AniList sent an unreadable answer");
            }
            var errors = body["errors"] as JArray;
            if (errors != null && errors.Count > 0 && body["data"]?.Type != JTokenType.Object)
            {
                var message = errors.First?.Value<string>("message") ?? "unknown error";
                if (message.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                throw new AniListException("AniList: {0}", message);
            }

            return body["data"];
        }
    }
}
