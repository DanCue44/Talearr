using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    public interface IOpenLibraryListClient
    {
        // Fetches a JSON document. Returns null for 404 (a private or missing list); throws for
        // other errors.
        JToken GetJson(string url);

        // The work key (OL123W) of an Open Library edition key (OL123M), or null.
        string WorkForEdition(string editionKey);

        // The work key (OL123W) for an ISBN-10 or ISBN-13, or null.
        string WorkForIsbn(string isbn);
    }

    // Talearr: small JSON client for the Open Library and Bookwyrm import lists. Remembers
    // edition and ISBN lookups, so a shelf with many editions of one work is cheap.
    public class OpenLibraryListClient : IOpenLibraryListClient
    {
        public const string OpenLibraryUrl = "https://openlibrary.org";

        private static readonly Regex WorkKey = new Regex(@"OL\d+W", RegexOptions.Compiled);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private readonly Dictionary<string, string> _workCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public OpenLibraryListClient(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public static string ExtractWorkKey(string value)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return null;
            }

            var match = WorkKey.Match(value);
            return match.Success ? match.Value : null;
        }

        public JToken GetJson(string url)
        {
            var request = new HttpRequest(url, new HttpAccept("application/activity+json, application/json"))
            {
                RequestTimeout = RequestTimeout,
                SuppressHttpErrorStatusCodes = new[] { HttpStatusCode.NotFound }
            };

            // Open Library asks API clients to identify themselves.
            request.Headers.Add("User-Agent", $"Talearr/{BuildInfo.Version} (+https://talearr.com)");

            var response = _httpClient.Execute(request);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            return JToken.Parse(response.Content);
        }

        public string WorkForEdition(string editionKey)
        {
            if (editionKey.IsNullOrWhiteSpace())
            {
                return null;
            }

            var key = editionKey.Trim().Split('/').Last();
            return Cached("edition:" + key, () => FirstWork(GetJson($"{OpenLibraryUrl}/books/{key}.json")));
        }

        public string WorkForIsbn(string isbn)
        {
            var digits = new string((isbn ?? string.Empty).Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray());

            if (digits.Length != 10 && digits.Length != 13)
            {
                return null;
            }

            return Cached("isbn:" + digits, () => FirstWork(GetJson($"{OpenLibraryUrl}/isbn/{digits}.json")));
        }

        private string Cached(string key, Func<string> resolve)
        {
            lock (_workCache)
            {
                if (_workCache.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                // One instance serves every sync; keep the cache from growing without bound.
                if (_workCache.Count > 20000)
                {
                    _workCache.Clear();
                }
            }

            string work = null;

            try
            {
                work = resolve();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Open Library lookup for {0} failed", key);
            }

            lock (_workCache)
            {
                _workCache[key] = work;
            }

            return work;
        }

        private static string FirstWork(JToken edition)
        {
            return ExtractWorkKey(edition?["works"]?.FirstOrDefault()?["key"]?.ToString());
        }
    }
}
