using System;
using System.Collections.Generic;
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

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: the two MAM calls the freeleech seeding filler (Talearr/MamFiller) makes. Both go
    // through this indexer's HTTP client (so its proxy applies) and its session handling (an
    // expired session is reported, a rotated mam_id is saved), exactly like normal searches and
    // grabs. Only MAM's documented automation endpoints are used: the JSON torrent search and
    // download.php. The download never asks for a freeleech wedge.
    public partial class MyAnonaMouse
    {
        public const int FillerPageSize = 100;

        // MAM's JSON search "searchType" for freeleech torrents only (the same values Jackett and
        // Prowlarr send: all, active, inactive, fl, fl-VIP, VIP, nVIP). The filler re-checks the
        // freeleech flags on every result anyway.
        public const string FreeleechSearchType = "fl";

        // Talearr: categoryIds limits the search to those MAM site categories (the filler's
        // preferred categories); null or empty searches all audiobooks and ebooks.
        public async Task<List<JToken>> SearchFillerFreeleechAsync(int page, IReadOnlyCollection<int> categoryIds = null)
        {
            var request = new IndexerRequest(BuildFillerSearchRequest(page, categoryIds));
            var response = await FetchIndexerResponse(request);
            var http = response.HttpResponse;

            if (http.StatusCode != HttpStatusCode.OK)
            {
                throw new IndexerException(response, $"MAM freeleech search returned HTTP {(int)http.StatusCode}");
            }

            JObject json;
            try
            {
                json = JsonConvert.DeserializeObject<JObject>(http.Content);
            }
            catch (JsonException ex)
            {
                throw new IndexerException(response, "MAM freeleech search did not return JSON: " + ex.Message);
            }

            if (json == null)
            {
                throw new IndexerException(response, "MAM freeleech search returned an empty reply");
            }

            if (json["data"] is JArray data)
            {
                return data.ToList();
            }

            // MAM answers {"error":"Nothing returned, out of 0"} when nothing matches.
            var error = json.Value<string>("error");
            if (!string.IsNullOrWhiteSpace(error) && error.IndexOf("Nothing returned", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new IndexerException(response, "MAM freeleech search failed: " + error);
            }

            return new List<JToken>();
        }

        // The download link has no canUseToken flag, so GetDownloadRequest never adds "fl".
        public static string BuildFillerDownloadLink(string baseUrl, string torrentId)
        {
            return $"{baseUrl.Trim().TrimEnd('/')}/tor/download.php?tid={Uri.EscapeDataString(torrentId)}";
        }

        public async Task<byte[]> DownloadFillerTorrentAsync(string torrentId)
        {
            var request = GetDownloadRequest(BuildFillerDownloadLink(Settings.BaseUrl, torrentId));
            request.RateLimitKey = Definition?.Id > 0 ? Definition.Id.ToString() : null;
            request.RateLimit = RateLimit;
            request.Headers.Accept = "application/x-bittorrent";

            var response = await ExecuteDownloadRequestAsync(request);

            if (response.StatusCode != HttpStatusCode.OK || response.ResponseData == null || response.ResponseData.Length == 0)
            {
                throw new InvalidOperationException($"MAM did not return a torrent for {torrentId} (HTTP {(int)response.StatusCode})");
            }

            return response.ResponseData;
        }

        internal HttpRequest BuildFillerSearchRequest(int page, IReadOnlyCollection<int> categoryIds = null)
        {
            var url = $"{Settings.BaseUrl.Trim().TrimEnd('/')}/tor/js/loadSearchJSONbasic.php";
            var payload = BuildFillerSearchPayload(page, categoryIds);

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

        internal static Dictionary<string, object> BuildFillerSearchPayload(int page, IReadOnlyCollection<int> categoryIds = null)
        {
            var tor = new Dictionary<string, object>
            {
                ["text"] = string.Empty,
                ["srchIn"] = new[] { "title", "author", "narrator", "series", "tags" },
                ["searchType"] = FreeleechSearchType,
                ["searchIn"] = "torrents",
                ["cat"] = new[] { "0" },
                ["main_cat"] = new[] { "13", "14" },
                ["browse_lang"] = new[] { "1" },
                ["browseFlagsHideVsShow"] = "0",
                ["startDate"] = string.Empty,
                ["endDate"] = string.Empty,
                ["hash"] = string.Empty,
                ["sortType"] = "default",
                ["startNumber"] = (Math.Max(0, page) * FillerPageSize).ToString()
            };

            // Talearr: MAM's JSON search takes site category ids in "cat" ("0" means all). The
            // main_cat filter is left out so it cannot widen the result again; the filler still
            // checks the main category of every result.
            var ids = (categoryIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
            if (ids.Count > 0)
            {
                tor["cat"] = ids.Select(id => id.ToString()).ToArray();
                tor.Remove("main_cat");
            }

            return new Dictionary<string, object>
            {
                ["tor"] = tor,
                ["perpage"] = FillerPageSize.ToString()
            };
        }
    }
}
