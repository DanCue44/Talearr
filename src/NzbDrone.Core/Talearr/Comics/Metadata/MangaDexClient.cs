using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.Comics.Metadata
{
    public class MangaDexException : NzbDroneException
    {
        public MangaDexException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class MangaDexManga
    {
        public string Id { get; set; }
        public List<string> Titles { get; set; } = new List<string>();
        public string EnglishTitle { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public int? Year { get; set; }

        // safe, suggestive, erotica or pornographic.
        public string ContentRating { get; set; }

        // shounen, shoujo, seinen, josei or null.
        public string Demographic { get; set; }
        public string OriginalLanguage { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    public interface IMangaDexClient
    {
        List<MangaDexManga> Search(string title);
    }

    // Talearr: MangaDex (https://api.mangadex.org) as a fallback for manga facts when AniList is
    // unavailable or has no rating: titles, status, year, the content rating and demographic used
    // for the age rating. Metadata only; Talearr never downloads from MangaDex. MangaDex asks for a
    // real User-Agent and about 5 requests a second at most, so calls are spaced out.
    public class MangaDexClient : IMangaDexClient
    {
        public const string Endpoint = "https://api.mangadex.org";
        private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(300);
        private static readonly object Gate = new object();
        private static DateTime _lastRequest = DateTime.MinValue;

        private readonly IHttpClient _httpClient;

        public MangaDexClient(IHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public List<MangaDexManga> Search(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return new List<MangaDexManga>();
            }

            lock (Gate)
            {
                var wait = _lastRequest + MinimumGap - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }

                _lastRequest = DateTime.UtcNow;
            }

            // Every content rating is asked for: the point is to learn the rating, and the default
            // filter would hide exactly the series a kids library must refuse.
            var url = $"{Endpoint}/manga?title={Uri.EscapeDataString(title.Trim())}&limit=5&order[relevance]=desc" +
                      "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica&contentRating[]=pornographic";

            var request = new HttpRequestBuilder(url)
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", "Talearr (+https://talearr.com)")
                .Build();

            request.SuppressHttpError = true;
            request.RequestTimeout = TimeSpan.FromSeconds(30);

            var response = _httpClient.Execute(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new MangaDexException("MangaDex is busy (too many requests). Try again in a minute.");
            }

            if (response.HasHttpError)
            {
                throw new MangaDexException("MangaDex answered {0}", (int)response.StatusCode);
            }

            return Parse(response.Content);
        }

        internal static List<MangaDexManga> Parse(string json)
        {
            var body = JObject.Parse(json);
            var list = new List<MangaDexManga>();

            foreach (var item in body["data"] as JArray ?? new JArray())
            {
                var attributes = item["attributes"];
                if (attributes == null)
                {
                    continue;
                }

                var manga = new MangaDexManga
                {
                    Id = item.Value<string>("id"),
                    Status = attributes.Value<string>("status"),
                    Year = attributes.Value<int?>("year"),
                    ContentRating = attributes.Value<string>("contentRating"),
                    Demographic = attributes.Value<string>("publicationDemographic"),
                    OriginalLanguage = attributes.Value<string>("originalLanguage"),
                    Description = attributes["description"]?.Type == JTokenType.Object ? attributes["description"].Value<string>("en") : null
                };

                void AddTitles(JToken map)
                {
                    if (map?.Type != JTokenType.Object)
                    {
                        return;
                    }

                    foreach (var property in ((JObject)map).Properties())
                    {
                        var value = property.Value?.Value<string>();
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        if (property.Name == "en" && manga.EnglishTitle == null)
                        {
                            manga.EnglishTitle = value;
                        }

                        // Latin script only: the titles releases are named with.
                        if ((property.Name == "en" || property.Name.EndsWith("-ro", StringComparison.Ordinal)) && !manga.Titles.Contains(value))
                        {
                            manga.Titles.Add(value);
                        }
                    }
                }

                AddTitles(attributes["title"]);
                foreach (var alt in attributes["altTitles"] as JArray ?? new JArray())
                {
                    AddTitles(alt);
                }

                foreach (var tag in attributes["tags"] as JArray ?? new JArray())
                {
                    var name = tag["attributes"]?["name"]?.Value<string>("en");
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        manga.Tags.Add(name);
                    }
                }

                list.Add(manga);
            }

            return list;
        }

        // Conservative on purpose: MangaDex cannot tell a picture book from a teen adventure, so
        // its best rating is Teen, never Everyone. Anything suggestive is treated as Mature.
        public static string ToAgeRating(MangaDexManga manga)
        {
            if (manga == null || string.IsNullOrWhiteSpace(manga.ContentRating))
            {
                return ComicAgeRatings.Unknown;
            }

            switch (manga.ContentRating.ToLowerInvariant())
            {
                case "pornographic":
                case "erotica":
                    return ComicAgeRatings.AdultsOnly;
                case "suggestive":
                    return ComicAgeRatings.Mature17;
                case "safe":
                    var tags = new HashSet<string>(manga.Tags, StringComparer.OrdinalIgnoreCase);
                    return tags.Contains("Gore") || tags.Contains("Sexual Violence") ? ComicAgeRatings.Mature17 : ComicAgeRatings.Teen;
                default:
                    return ComicAgeRatings.Unknown;
            }
        }
    }
}
