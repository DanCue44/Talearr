using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Comics.Metadata
{
    public class ComicHardcoverException : NzbDroneException
    {
        public ComicHardcoverException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class HardcoverComicSeries
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string AuthorName { get; set; }
        public int BooksCount { get; set; }
        public int PrimaryBooksCount { get; set; }
        public string CoverUrl { get; set; }
        public List<HardcoverComicVolume> Volumes { get; set; } = new List<HardcoverComicVolume>();
    }

    public class HardcoverComicVolume
    {
        public int BookId { get; set; }
        public decimal Number { get; set; }
        public string Title { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public int? Pages { get; set; }
        public string CoverUrl { get; set; }
    }

    public interface IComicHardcoverClient
    {
        bool IsConfigured { get; }
        List<HardcoverComicSeries> SearchSeries(string query);
        HardcoverComicSeries GetSeries(int id);
    }

    // Talearr: Hardcover (https://hardcover.app) lists manga volumes and comic collections as books
    // in a series, with release dates and covers: Phase 1's volume list. Uses the API token from
    // Settings > Metadata. Read only.
    public class ComicHardcoverClient : IComicHardcoverClient
    {
        public const string Endpoint = "https://api.hardcover.app/v1/graphql";

        private const string SearchQuery = @"
            query TalearrComicSeriesSearch($q: String!) {
                search(query: $q, query_type: ""Series"", per_page: 15, page: 1) {
                    results
                }
            }";

        // Search hits carry book titles only, no images: the first volumes' covers come from one
        // more query for all the hits at once.
        private const string CoversQuery = @"
            query TalearrComicSeriesCovers($ids: [Int!]) {
                series(where: { id: { _in: $ids } }) {
                    id
                    book_series(order_by: { position: asc }, limit: 10) {
                        book { image { url } }
                    }
                }
            }";

        private const string SeriesQuery = @"
            query TalearrComicSeries($id: Int!) {
                series(where: { id: { _eq: $id } }, limit: 1) {
                    id
                    name
                    description
                    books_count
                    primary_books_count
                    author { name }
                    book_series(order_by: { position: asc }, limit: 400) {
                        position
                        featured
                        compilation
                        book { id title release_date pages image { url } }
                    }
                }
            }";

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;

        public ComicHardcoverClient(IHttpClient httpClient, IConfigService configService)
        {
            _httpClient = httpClient;
            _configService = configService;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_configService.HardcoverApiToken);

        public List<HardcoverComicSeries> SearchSeries(string query)
        {
            var data = Execute(SearchQuery, new { q = query });
            var list = ParseSearch(data?["search"]?["results"]);

            var missing = list.Where(s => string.IsNullOrWhiteSpace(s.CoverUrl)).Select(s => s.Id).Distinct().ToList();
            if (missing.Count > 0)
            {
                // Covers are a nicety: a failed lookup leaves the results as they were.
                try
                {
                    var covers = ParseCovers(Execute(CoversQuery, new { ids = missing })?["series"]);
                    foreach (var series in list.Where(s => string.IsNullOrWhiteSpace(s.CoverUrl)))
                    {
                        series.CoverUrl = covers.TryGetValue(series.Id, out var url) ? url : null;
                    }
                }
                catch (ComicHardcoverException)
                {
                }
            }

            return list;
        }

        public HardcoverComicSeries GetSeries(int id)
        {
            var data = Execute(SeriesQuery, new { id });
            return ParseSeries((data?["series"] as JArray)?.FirstOrDefault());
        }

        internal static List<HardcoverComicSeries> ParseSearch(JToken results)
        {
            var list = new List<HardcoverComicSeries>();

            // "results" is the search engine's JSON; Hardcover returns it either as an object or a string.
            if (results?.Type == JTokenType.String)
            {
                results = JToken.Parse(results.Value<string>());
            }

            foreach (var hit in results?["hits"] as JArray ?? new JArray())
            {
                var document = hit["document"];
                if (document == null || !int.TryParse(document.Value<string>("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }

                // "books" holds titles (strings) in search hits; only objects carry an image.
                var cover = (document["books"] as JArray)?
                    .Select(b => b?.Type == JTokenType.Object && b["image"]?.Type == JTokenType.Object ? b["image"].Value<string>("url") : null)
                    .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

                list.Add(new HardcoverComicSeries
                {
                    Id = id,
                    Name = document.Value<string>("name"),
                    AuthorName = document.Value<string>("author_name"),
                    BooksCount = document.Value<int?>("books_count") ?? 0,
                    PrimaryBooksCount = document.Value<int?>("primary_books_count") ?? 0,
                    CoverUrl = cover
                });
            }

            return list;
        }

        /// <summary>Series id to the first cover among its first volumes.</summary>
        internal static Dictionary<int, string> ParseCovers(JToken series)
        {
            var covers = new Dictionary<int, string>();
            foreach (var entry in series as JArray ?? new JArray())
            {
                if (entry?.Type != JTokenType.Object || entry["id"] == null)
                {
                    continue;
                }

                var url = (entry["book_series"] as JArray ?? new JArray())
                    .Select(b => b?["book"]?["image"])
                    .Where(i => i?.Type == JTokenType.Object)
                    .Select(i => i.Value<string>("url"))
                    .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

                if (url != null)
                {
                    covers[entry.Value<int>("id")] = url;
                }
            }

            return covers;
        }

        internal static HardcoverComicSeries ParseSeries(JToken series)
        {
            if (series == null || series.Type != JTokenType.Object)
            {
                return null;
            }

            var result = new HardcoverComicSeries
            {
                Id = series.Value<int>("id"),
                Name = series.Value<string>("name"),
                Description = series.Value<string>("description"),
                AuthorName = series["author"]?.Type == JTokenType.Object ? series["author"].Value<string>("name") : null,
                BooksCount = series.Value<int?>("books_count") ?? 0,
                PrimaryBooksCount = series.Value<int?>("primary_books_count") ?? 0
            };

            // One volume per position. Hardcover files every edition of a volume under the same
            // position (2-in-1 omnibuses, French, German and Japanese editions), so compilations are
            // left out and, of the rest, the English book named after the series wins.
            var seriesName = ComicNormalize(result.Name);
            var collectsVolumes = seriesName.Contains("in 1", StringComparison.Ordinal) || seriesName.Contains("omnibus", StringComparison.Ordinal);
            var entries = (series["book_series"] as JArray ?? new JArray())
                .Where(e => e?["book"]?.Type == JTokenType.Object && (e["position"]?.Type == JTokenType.Float || e["position"]?.Type == JTokenType.Integer))
                .Where(e => !(e.Value<bool?>("compilation") ?? false))
                .Select(e => new
                {
                    Position = e.Value<decimal>("position"),
                    Featured = e.Value<bool?>("featured") ?? false,
                    Book = e["book"],
                    Title = e["book"].Value<string>("title") ?? string.Empty
                })
                .Where(e => e.Position >= 0 && e.Position < 10000)
                .Where(e => collectsVolumes || !IsCollectedEdition(e.Title))
                .GroupBy(e => e.Position)
                .Select(g => g
                    .OrderByDescending(e => (ComicNormalize(e.Title).StartsWith(seriesName, StringComparison.Ordinal) ? 8 : 0) +
                                            (IsLatin(e.Title) ? 4 : 0) +
                                            (e.Featured ? 2 : 0) +
                                            (e.Book["image"]?.Type == JTokenType.Object ? 1 : 0))
                    .First())
                .OrderBy(e => e.Position);

            foreach (var entry in entries)
            {
                var book = entry.Book;
                result.Volumes.Add(new HardcoverComicVolume
                {
                    BookId = book.Value<int>("id"),
                    Number = entry.Position,
                    Title = book.Value<string>("title"),
                    ReleaseDate = DateTime.TryParse(book.Value<string>("release_date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : null,
                    Pages = book.Value<int?>("pages"),
                    CoverUrl = book["image"]?.Type == JTokenType.Object ? book["image"].Value<string>("url") : null
                });
            }

            result.CoverUrl = result.Volumes.Select(v => v.CoverUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            return result;
        }

        private static bool IsCollectedEdition(string title)
        {
            var normalized = ComicNormalize(title);
            return normalized.Contains("in 1", StringComparison.Ordinal) || normalized.Contains("omnibus", StringComparison.Ordinal) || normalized.Contains("box set", StringComparison.Ordinal);
        }

        private static bool IsLatin(string title)
        {
            return !string.IsNullOrEmpty(title) && title.All(c => c < 0x2E80);
        }

        private static string ComicNormalize(string text)
        {
            return Parsing.ComicReleaseParser.NormalizeTitle(text);
        }

        private JToken Execute(string query, object variables)
        {
            var token = _configService.HardcoverApiToken?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ComicHardcoverException("Set the Hardcover API token in Settings > Metadata to look up volumes.");
            }

            var request = new HttpRequestBuilder(Endpoint)
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .Build();

            request.Method = HttpMethod.Post;
            request.SuppressHttpError = true;
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            request.Headers.Add("Authorization", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : "Bearer " + token);
            request.SetContent(JsonConvert.SerializeObject(new { query, variables }));

            var response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new ComicHardcoverException("Hardcover rejected the API token (401). Check it in Settings > Metadata.");
            }

            if (response.HasHttpError)
            {
                throw new ComicHardcoverException("Hardcover answered {0}", (int)response.StatusCode);
            }

            var body = JObject.Parse(response.Content);
            if (body["errors"] is JArray errors && errors.Count > 0)
            {
                throw new ComicHardcoverException("Hardcover: {0}", errors.First?.Value<string>("message") ?? "unknown error");
            }

            return body["data"];
        }
    }
}
