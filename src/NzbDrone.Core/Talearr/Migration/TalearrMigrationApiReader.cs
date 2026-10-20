using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Talearr.Migration
{
    public interface ITalearrMigrationApiReader
    {
        TalearrMigrationSnapshot Read(TalearrMigrationRequest request);
    }

    // Talearr: reads a running Readarr, Bookshelf or LazyLibrarian over its API. Only GET requests;
    // nothing in the source is changed.
    public class TalearrMigrationApiReader : ITalearrMigrationApiReader
    {
        // Readarr's edition endpoint takes several bookId values; keep the URLs short.
        private const int EditionBatch = 50;

        private readonly IHttpClient _httpClient;

        public TalearrMigrationApiReader(IHttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public TalearrMigrationSnapshot Read(TalearrMigrationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Url) || string.IsNullOrWhiteSpace(request.ApiKey))
            {
                throw new TalearrMigrationException("Give the source's URL and API key.");
            }

            var baseUrl = request.Url.Trim().TrimEnd('/');
            Func<string, string> fetch = resource => Get(baseUrl, resource, request.ApiKey, request.Source);

            return request.Source == TalearrMigrationSources.LazyLibrarian
                ? ReadLazyLibrarian(fetch, request.ApiKey)
                : ReadReadarr(fetch, request.Source);
        }

        private string Get(string baseUrl, string resource, string apiKey, string source)
        {
            var builder = new HttpRequestBuilder(baseUrl).Resource(resource).Accept(HttpAccept.Json);
            if (source != TalearrMigrationSources.LazyLibrarian)
            {
                builder.SetHeader("X-Api-Key", apiKey);
            }

            var httpRequest = builder.Build();
            httpRequest.AllowAutoRedirect = false;
            httpRequest.SuppressHttpError = true;

            HttpResponse response;
            try
            {
                response = _httpClient.Get(httpRequest);
            }
            catch (Exception ex)
            {
                throw new TalearrMigrationException($"Could not reach {baseUrl}: {ex.Message}", ex);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new TalearrMigrationException("The source rejected the API key.");
            }

            if ((int)response.StatusCode >= 300)
            {
                throw new TalearrMigrationException($"{resource.Split('?')[0]} returned HTTP {(int)response.StatusCode}. Check the URL (and its URL base).");
            }

            return response.Content;
        }

        // Readarr and Bookshelf (/api/v1). The fetch function maps a resource path to its JSON, so
        // tests can replay recorded responses.
        public static TalearrMigrationSnapshot ReadReadarr(Func<string, string> fetch, string source)
        {
            var snapshot = new TalearrMigrationSnapshot { Source = source ?? TalearrMigrationSources.Readarr };

            var status = ParseObject(fetch("/api/v1/system/status"));
            snapshot.AppName = Str(status, "appName") ?? "Readarr";
            snapshot.Version = Str(status, "version");

            foreach (var root in ParseArray(fetch("/api/v1/rootfolder")))
            {
                snapshot.RootFolders.Add(new TalearrSourceRootFolder
                {
                    Path = Str(root, "path"),
                    Name = Str(root, "name"),
                    QualityProfileId = Int(root, "defaultQualityProfileId"),
                    MetadataProfileId = Int(root, "defaultMetadataProfileId"),
                    NewItemMonitor = Str(root, "defaultNewItemMonitorOption"),
                    Tags = Ints(root["defaultTags"])
                });
            }

            snapshot.QualityProfiles = ParseArray(fetch("/api/v1/qualityprofile")).Select(Profile).ToList();
            snapshot.MetadataProfiles = ParseArray(fetch("/api/v1/metadataprofile")).Select(Profile).ToList();
            snapshot.Tags = ParseArray(fetch("/api/v1/tag")).Select(t => new TalearrSourceTag { Id = Int(t, "id") ?? 0, Label = Str(t, "label") }).ToList();
            snapshot.DownloadClients = ParseArray(fetch("/api/v1/downloadclient")).Select(ApiProvider).ToList();
            snapshot.Indexers = ParseArray(fetch("/api/v1/indexer")).Select(ApiProvider).ToList();

            foreach (var author in ParseArray(fetch("/api/v1/author")))
            {
                snapshot.Authors.Add(new TalearrSourceAuthor
                {
                    Id = Int(author, "id") ?? 0,
                    Name = Str(author, "authorName"),
                    ForeignAuthorId = Str(author, "foreignAuthorId"),
                    Monitored = Bool(author, "monitored"),
                    MonitorNewItems = Str(author, "monitorNewItems"),
                    Path = Str(author, "path"),
                    RootFolderPath = Str(author, "rootFolderPath"),
                    QualityProfileId = Int(author, "qualityProfileId") ?? 0,
                    MetadataProfileId = Int(author, "metadataProfileId") ?? 0,
                    Tags = Ints(author["tags"])
                });
            }

            var books = new List<(TalearrSourceBook Book, JObject Raw)>();
            foreach (var book in ParseArray(fetch("/api/v1/book")))
            {
                var statistics = book["statistics"] as JObject;
                books.Add((new TalearrSourceBook
                {
                    Id = Int(book, "id") ?? 0,
                    AuthorId = Int(book, "authorId") ?? 0,
                    Title = Str(book, "title"),
                    ForeignBookId = Str(book, "foreignBookId"),
                    ForeignEditionId = Str(book, "foreignEditionId"),
                    Monitored = Bool(book, "monitored"),
                    HasFile = (Int(statistics, "bookFileCount") ?? 0) > 0
                }, book));
            }

            // Editions carry the ISBN and ASIN. Some versions embed them in /book; otherwise ask the
            // edition endpoint, only for the books that will be looked up (monitored or with files).
            var editions = new Dictionary<int, List<JObject>>();
            foreach (var (book, raw) in books)
            {
                if (raw["editions"] is JArray embedded && embedded.Count > 0)
                {
                    editions[book.Id] = embedded.OfType<JObject>().ToList();
                }
            }

            var missing = books.Where(b => !editions.ContainsKey(b.Book.Id) && (b.Book.Monitored || b.Book.HasFile)).Select(b => b.Book.Id).ToList();
            for (var i = 0; i < missing.Count; i += EditionBatch)
            {
                var ids = missing.Skip(i).Take(EditionBatch);
                var query = string.Join("&", ids.Select(id => "bookId=" + id));
                string json;
                try
                {
                    json = fetch("/api/v1/edition?" + query);
                }
                catch (TalearrMigrationException)
                {
                    // Older versions have no edition list; title and author still work.
                    break;
                }

                foreach (var edition in ParseArray(json))
                {
                    var bookId = Int(edition, "bookId") ?? 0;
                    if (!editions.TryGetValue(bookId, out var list))
                    {
                        editions[bookId] = list = new List<JObject>();
                    }

                    list.Add(edition);
                }
            }

            foreach (var (book, _) in books)
            {
                if (editions.TryGetValue(book.Id, out var list))
                {
                    ApplyEdition(book, list.FirstOrDefault(e => Bool(e, "monitored")) ?? list.FirstOrDefault());
                }

                snapshot.Books.Add(book);
            }

            return snapshot;
        }

        // LazyLibrarian: getIndex lists the authors, getAllBooks the books with an ebook Status and
        // an AudioStatus. Wanted, Have, Open, Snatched and Processed count as monitored.
        public static TalearrMigrationSnapshot ReadLazyLibrarian(Func<string, string> fetch, string apiKey)
        {
            var snapshot = new TalearrMigrationSnapshot { Source = TalearrMigrationSources.LazyLibrarian, AppName = "LazyLibrarian" };
            var key = Uri.EscapeDataString(apiKey ?? string.Empty);

            var authorIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var author in ParseArray(fetch("/api?cmd=getIndex&apikey=" + key)))
            {
                var id = Str(author, "AuthorID");
                if (string.IsNullOrWhiteSpace(id) || authorIds.ContainsKey(id))
                {
                    continue;
                }

                authorIds[id] = authorIds.Count + 1;
                var status = Str(author, "Status");
                snapshot.Authors.Add(new TalearrSourceAuthor
                {
                    Id = authorIds[id],
                    Name = Str(author, "AuthorName"),
                    Monitored = !string.Equals(status, "Ignored", StringComparison.OrdinalIgnoreCase) && !string.Equals(status, "Paused", StringComparison.OrdinalIgnoreCase),
                    MonitorNewItems = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Wanted", StringComparison.OrdinalIgnoreCase) ? "new" : "none"
                });
            }

            var bookNumber = 0;
            foreach (var book in ParseArray(fetch("/api?cmd=getAllBooks&apikey=" + key)))
            {
                var authorKey = Str(book, "AuthorID");
                if (string.IsNullOrWhiteSpace(authorKey) || !authorIds.TryGetValue(authorKey, out var authorId))
                {
                    authorId = authorIds.Count + 1;
                    authorIds[authorKey ?? "name:" + Str(book, "AuthorName")] = authorId;
                    snapshot.Authors.Add(new TalearrSourceAuthor { Id = authorId, Name = Str(book, "AuthorName"), Monitored = true, MonitorNewItems = "none" });
                }

                var isbn = Str(book, "BookIsbn");
                var bookId = Str(book, "BookID");
                snapshot.Books.Add(new TalearrSourceBook
                {
                    Id = ++bookNumber,
                    AuthorId = authorId,
                    Title = Str(book, "BookName"),
                    ForeignEditionId = bookId != null && bookId.All(char.IsDigit) ? bookId : null,
                    Isbn13 = isbn != null && isbn.Length == 13 ? isbn : null,
                    Monitored = LazyLibrarianWanted(Str(book, "Status")),
                    AudiobookMonitored = LazyLibrarianWanted(Str(book, "AudioStatus")),
                    HasFile = !string.IsNullOrWhiteSpace(Str(book, "BookFile")) || !string.IsNullOrWhiteSpace(Str(book, "AudioFile"))
                });
            }

            return snapshot;
        }

        internal static bool LazyLibrarianWanted(string status)
        {
            switch ((status ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "wanted":
                case "have":
                case "open":
                case "snatched":
                case "processed":
                    return true;
                default:
                    return false;
            }
        }

        private static void ApplyEdition(TalearrSourceBook book, JObject edition)
        {
            if (edition == null)
            {
                return;
            }

            book.Isbn13 = Str(edition, "isbn13") ?? book.Isbn13;
            book.Asin = Str(edition, "asin") ?? book.Asin;
            book.ForeignEditionId ??= Str(edition, "foreignEditionId");
        }

        // A provider resource's "fields" array turned back into the settings object Readarr
        // stores. Dotted names ("seedCriteria.seedRatio") become nested objects.
        internal static TalearrSourceProvider ApiProvider(JObject resource)
        {
            var settings = new JObject();
            foreach (var field in (resource["fields"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var name = Str(field, "name");
                if (string.IsNullOrWhiteSpace(name) || !field.TryGetValue("value", out var value))
                {
                    continue;
                }

                var parts = name.Split('.');
                var target = settings;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    if (target[parts[i]] is not JObject child)
                    {
                        child = new JObject();
                        target[parts[i]] = child;
                    }

                    target = child;
                }

                target[parts[parts.Length - 1]] = value.DeepClone();
            }

            return new TalearrSourceProvider
            {
                Name = Str(resource, "name"),
                Implementation = Str(resource, "implementation"),
                ConfigContract = Str(resource, "configContract"),
                Settings = settings.ToString(Newtonsoft.Json.Formatting.None),
                Enable = Bool(resource, "enable"),
                EnableRss = Bool(resource, "enableRss"),
                EnableAutomaticSearch = Bool(resource, "enableAutomaticSearch"),
                EnableInteractiveSearch = Bool(resource, "enableInteractiveSearch"),
                Priority = Int(resource, "priority") ?? 0,
                Tags = Ints(resource["tags"])
            };
        }

        private static TalearrSourceProfile Profile(JObject profile)
        {
            return new TalearrSourceProfile { Id = Int(profile, "id") ?? 0, Name = Str(profile, "name") };
        }

        internal static JObject ParseObject(string json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? new JObject() : JToken.Parse(json) as JObject ?? new JObject();
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new TalearrMigrationException("The source did not answer with JSON. Is this the right URL?", ex);
            }
        }

        internal static List<JObject> ParseArray(string json)
        {
            try
            {
                var token = string.IsNullOrWhiteSpace(json) ? new JArray() : JToken.Parse(json);
                return (token as JArray ?? new JArray()).OfType<JObject>().ToList();
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new TalearrMigrationException("The source did not answer with JSON. Is this the right URL?", ex);
            }
        }

        internal static string Str(JObject obj, string name)
        {
            var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            var value = token.Type == JTokenType.String ? (string)token : token.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        internal static int? Int(JObject obj, string name)
        {
            var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            return int.TryParse(token.ToString(), out var value) ? value : null;
        }

        internal static bool Bool(JObject obj, string name)
        {
            var token = obj?.GetValue(name, StringComparison.OrdinalIgnoreCase);
            return token != null && token.Type != JTokenType.Null && bool.TryParse(token.ToString(), out var value) && value;
        }

        internal static List<int> Ints(JToken token)
        {
            return (token as JArray ?? new JArray())
                .Select(t => int.TryParse(t.ToString(), out var v) ? v : (int?)null)
                .Where(v => v.HasValue)
                .Select(v => v.Value)
                .ToList();
        }
    }
}
