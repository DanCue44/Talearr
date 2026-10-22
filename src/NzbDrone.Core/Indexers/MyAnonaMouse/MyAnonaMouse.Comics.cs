using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: one MAM result for a comics search, with the fields the comic release parser
    // needs that are not in ReleaseInfo (MAM's own series and volume number, and its tags).
    public class MamComicResult
    {
        public ReleaseInfo Release { get; set; }
        public int? CategoryId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesNumber { get; set; }
        public string Tags { get; set; }
        public string LanguageCode { get; set; }
    }

    // Talearr: comics and manga searches (Talearr/Comics). They use the same documented JSON
    // search endpoint, session handling and rate limit as book searches, limited to MAM's comics
    // site category. MAM files manga under "Ebooks - Comics/Graphic novels" too.
    public partial class MyAnonaMouse
    {
        public const int ComicsCategoryId = 61;
        public const int ComicsPageSize = 100;

        public async Task<List<MamComicResult>> SearchComicsAsync(string query, IReadOnlyCollection<int> categoryIds, string languageIso)
        {
            var request = new IndexerRequest(BuildComicSearchRequest(query, categoryIds, languageIso));
            var response = await FetchIndexerResponse(request);
            var http = response.HttpResponse;

            if (http.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(response, $"MAM comics search returned HTTP {(int)http.StatusCode}");
            }

            return ParseComicResults(response, Settings);
        }

        internal static List<MamComicResult> ParseComicResults(IndexerResponse response, MyAnonaMouseSettings settings)
        {
            JObject json;
            try
            {
                json = JsonConvert.DeserializeObject<JObject>(response.Content);
            }
            catch (JsonException ex)
            {
                throw new IndexerException(response, "MAM comics search did not return JSON: " + ex.Message);
            }

            if (json == null)
            {
                throw new IndexerException(response, "MAM comics search returned an empty reply");
            }

            if (json["data"] is not JArray data)
            {
                // MAM answers {"error":"Nothing returned, out of 0"} when nothing matches.
                var error = json.Value<string>("error");
                if (!string.IsNullOrWhiteSpace(error) && error.IndexOf("Nothing returned", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new IndexerException(response, "MAM comics search failed: " + error);
                }

                return new List<MamComicResult>();
            }

            var releases = new MyAnonaMouseJsonParser(settings).ParseResponse(response)
                .ToDictionary(r => r.Guid, StringComparer.OrdinalIgnoreCase);
            var results = new List<MamComicResult>();

            foreach (var torrent in data.OfType<JObject>())
            {
                var id = torrent.Value<int?>("id")?.ToString(CultureInfo.InvariantCulture);
                if (id == null || !releases.TryGetValue("MAM-" + id, out var release))
                {
                    continue;
                }

                var (seriesName, seriesNumber) = ReadSeriesInfo(torrent.Value<string>("series_info"));

                results.Add(new MamComicResult
                {
                    Release = release,
                    CategoryId = int.TryParse(torrent.Value<string>("category"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var category) ? category : null,
                    SeriesName = seriesName,
                    SeriesNumber = seriesNumber,
                    Tags = torrent.Value<string>("tags"),
                    LanguageCode = torrent.Value<string>("lang_code")
                });
            }

            return results;
        }

        // MAM sends series_info as a JSON string: {"<series id>": ["Series name", "8", 8]}. The
        // first series is the one the torrent is filed under.
        internal static (string Name, string Number) ReadSeriesInfo(string seriesInfo)
        {
            if (string.IsNullOrWhiteSpace(seriesInfo))
            {
                return (null, null);
            }

            try
            {
                if (JsonConvert.DeserializeObject<JToken>(seriesInfo) is not JObject series)
                {
                    return (null, null);
                }

                foreach (var property in series.Properties())
                {
                    if (property.Value is JArray values && values.Count > 0)
                    {
                        var name = WebUtility.HtmlDecode(values[0]?.ToString() ?? string.Empty).Trim();
                        var number = values.Count > 1 ? values[1]?.ToString().Trim() : null;
                        return (name.Length > 0 ? name : null, string.IsNullOrWhiteSpace(number) ? null : number);
                    }
                }
            }
            catch (JsonException)
            {
                // Not every torrent has well formed series info; the title still parses.
            }

            return (null, null);
        }

        internal HttpRequest BuildComicSearchRequest(string query, IReadOnlyCollection<int> categoryIds, string languageIso)
        {
            var url = $"{Settings.BaseUrl.Trim().TrimEnd('/')}/tor/js/loadSearchJSONbasic.php";
            var payload = BuildComicSearchPayload(query, categoryIds, languageIso, Settings.FreeleechOnly, Settings.MinimumSeeders);

            var request = new HttpRequest(url)
            {
                Method = HttpMethod.Post,
                ContentData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload))
            };

            request.Headers.ContentType = "application/json";
            request.Headers.Accept = "application/json";
            request.Headers.Set("User-Agent", $"{BuildInfo.AppName}/{BuildInfo.Version}");

            if (!string.IsNullOrWhiteSpace(Settings.MamId))
            {
                request.Cookies["mam_id"] = Settings.MamId;
            }

            if (Settings.MamSsl)
            {
                request.Cookies["mam_ssl"] = "1";
            }

            request.StoreRequestCookie = true;
            request.StoreResponseCookie = true;

            return request;
        }

        internal static Dictionary<string, object> BuildComicSearchPayload(string query, IReadOnlyCollection<int> categoryIds, string languageIso, bool freeleechOnly, int minimumSeeders)
        {
            var categories = (categoryIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
            if (categories.Count == 0)
            {
                categories.Add(ComicsCategoryId);
            }

            string searchType;
            if (freeleechOnly)
            {
                searchType = FreeleechSearchType;
            }
            else
            {
                searchType = minimumSeeders > 0 ? "active" : "all";
            }

            var tor = new Dictionary<string, object>
            {
                ["text"] = query ?? string.Empty,
                ["srchIn"] = new[] { "title", "author", "series", "tags" },
                ["searchType"] = searchType,
                ["searchIn"] = "torrents",

                // Site categories only: main_cat would widen the search back to every ebook.
                ["cat"] = categories.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray(),
                ["browseFlagsHideVsShow"] = "0",
                ["startDate"] = string.Empty,
                ["endDate"] = string.Empty,
                ["hash"] = string.Empty,
                ["sortType"] = "default",
                ["startNumber"] = "0"
            };

            var language = string.IsNullOrWhiteSpace(languageIso) ? "eng" : languageIso.Trim();
            if (language.Length == 2 && string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            {
                language = "eng";
            }

            if (MyAnonaMouseLanguageMapper.TryGetBrowseLanguageId(language, out var languageId))
            {
                tor["browse_lang"] = new[] { languageId.ToString(CultureInfo.InvariantCulture) };
            }

            return new Dictionary<string, object>
            {
                ["tor"] = tor,
                ["perpage"] = ComicsPageSize.ToString(CultureInfo.InvariantCulture)
            };
        }
    }
}
