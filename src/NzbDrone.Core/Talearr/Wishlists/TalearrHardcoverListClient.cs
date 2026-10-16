using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public interface ITalearrHardcoverListClient
    {
        // The books of a Hardcover list, by its URL. Read only.
        (string Name, List<TalearrWishlistRow> Rows) GetList(string url);
    }

    // TaleArr: reads one Hardcover list (https://hardcover.app/@user/lists/slug) with the API token
    // from Settings > Metadata. Upstream's Hardcover Library import list covers Owned, Want to Read,
    // Currently Reading and Read; this covers the user's other lists.
    public class TalearrHardcoverListClient : ITalearrHardcoverListClient
    {
        public const string Endpoint = "https://api.hardcover.app/v1/graphql";

        private static readonly Regex UrlRegex = new Regex(@"hardcover\.app/@(?<user>[^/\s]+)/lists/(?<slug>[^/?#\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private const string ListQuery = @"
            query TalearrList($username: citext!, $slug: String!) {
                lists(where: { slug: { _eq: $slug }, user: { username: { _eq: $username } } }, limit: 1) {
                    id
                    name
                    list_books(order_by: { position: asc }, limit: 1000) {
                        book {
                            id
                            title
                            subtitle
                            cached_contributors
                        }
                    }
                }
            }";

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;

        public TalearrHardcoverListClient(IHttpClient httpClient, IConfigService configService)
        {
            _httpClient = httpClient;
            _configService = configService;
        }

        public static (string User, string Slug) ParseUrl(string url)
        {
            var match = UrlRegex.Match(url ?? string.Empty);
            return match.Success ? (match.Groups["user"].Value, match.Groups["slug"].Value) : (null, null);
        }

        public (string Name, List<TalearrWishlistRow> Rows) GetList(string url)
        {
            var (user, slug) = ParseUrl(url);
            if (user == null)
            {
                throw new TalearrWishlistException("Not a Hardcover list URL. Use the list's address, like https://hardcover.app/@you/lists/my-list.");
            }

            var token = _configService.HardcoverApiToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new TalearrWishlistException("Set the Hardcover API token in Settings > Metadata to read Hardcover lists.");
            }

            token = token.Trim();
            var request = new HttpRequestBuilder(Endpoint)
                .SetHeader("Content-Type", "application/json")
                .SetHeader("Accept", "application/json")
                .SetHeader("User-Agent", ExternalImageRequestHeaders.GetRandomUserAgent())
                .Build();

            // GraphQL reads are POSTs.
            request.Method = HttpMethod.Post;
            request.Headers.Add("Authorization", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : "Bearer " + token);
            request.SetContent(JsonSerializer.Serialize(new { query = ListQuery, variables = new { username = user, slug } }));

            var response = _httpClient.Execute(request);
            if (response.HasHttpError)
            {
                throw new TalearrWishlistException($"Hardcover answered {(int)response.StatusCode}.");
            }

            return ParseResponse(response.Content, user, slug);
        }

        internal static (string Name, List<TalearrWishlistRow> Rows) ParseResponse(string json, string user, string slug)
        {
            var parsed = JsonSerializer.Deserialize<GraphQlResponse>(json, JsonOptions);
            if (parsed?.Errors?.Any() == true)
            {
                throw new TalearrWishlistException("Hardcover: " + string.Join("; ", parsed.Errors.Select(e => e.Message)));
            }

            var list = parsed?.Data?.Lists?.FirstOrDefault();
            if (list == null)
            {
                throw new TalearrWishlistException($"Hardcover has no list '{slug}' for @{user}, or it is private to another account.");
            }

            var rows = new List<TalearrWishlistRow>();
            foreach (var book in (list.ListBooks ?? new List<ListBook>()).Select(lb => lb.Book).Where(b => b != null && b.Id > 0))
            {
                var author = AuthorOf(book.CachedContributors);
                var row = new TalearrWishlistRow
                {
                    Title = book.Title,
                    Subtitle = book.Subtitle,
                    Author = author.Name,
                    BookProviderId = "hc:" + book.Id,
                    AuthorProviderId = author.Id > 0 ? "hc:" + author.Id : null
                };
                row.Key = TalearrWishlistCsv.RowKey(row);
                rows.Add(row);
            }

            return (list.Name, rows);
        }

        private static (int Id, string Name) AuthorOf(JsonElement contributors)
        {
            if (contributors.ValueKind != JsonValueKind.Array)
            {
                return (0, null);
            }

            foreach (var item in contributors.EnumerateArray())
            {
                var role = item.TryGetProperty("contribution", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (!string.IsNullOrWhiteSpace(role) && !string.Equals(role, "Author", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (item.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object)
                {
                    var id = author.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var value) ? value : 0;
                    var name = author.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    return (id, name);
                }
            }

            return (0, null);
        }

        private sealed class GraphQlResponse
        {
            public ListData Data { get; set; }
            public List<GraphQlError> Errors { get; set; }
        }

        private sealed class GraphQlError
        {
            public string Message { get; set; }
        }

        private sealed class ListData
        {
            public List<HardcoverList> Lists { get; set; }
        }

        private sealed class HardcoverList
        {
            public int Id { get; set; }
            public string Name { get; set; }

            [JsonPropertyName("list_books")]
            public List<ListBook> ListBooks { get; set; }
        }

        private sealed class ListBook
        {
            public ListBookBook Book { get; set; }
        }

        private sealed class ListBookBook
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }

            [JsonPropertyName("cached_contributors")]
            public JsonElement CachedContributors { get; set; }
        }
    }

    public class TalearrWishlistException : Exception
    {
        public TalearrWishlistException(string message)
            : base(message)
        {
        }
    }
}
