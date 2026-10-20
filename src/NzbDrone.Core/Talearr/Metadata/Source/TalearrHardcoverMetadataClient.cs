using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource.Hardcover;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public class TalearrHardcoverException : NzbDroneException
    {
        public TalearrHardcoverException(string message, params object[] args)
            : base(message, args)
        {
        }

        public TalearrHardcoverException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    // Thrown before a request when Hardcover says too few requests are left today.
    public class TalearrHardcoverBudgetException : TalearrHardcoverException
    {
        public TalearrHardcoverBudgetException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class TalearrHardcoverClientStatus
    {
        public bool Configured { get; set; }
        public int RequestsSinceStart { get; set; }
        public int? DailyRemaining { get; set; }
        public int CachedAuthors { get; set; }
        public string LastError { get; set; }
        public DateTime? LastErrorAt { get; set; }
    }

    public interface ITalearrHardcoverMetadataClient
    {
        bool IsConfigured { get; }

        // Null when Hardcover has no author with that id.
        HardcoverAuthorCatalog GetAuthorCatalog(int hardcoverAuthorId, bool useCache = true);

        // Cheap change check for refresh: books_count and updated_at per author, one request per
        // 100 authors. Authors Hardcover does not know are missing from the result.
        Dictionary<int, HardcoverAuthorSignature> GetAuthorSignatures(IReadOnlyCollection<int> hardcoverAuthorIds);

        // The authors credited on a book (primary roles first), for single-book lookups. Null
        // when Hardcover has no such book; a deduped book answers for its canonical book.
        HardcoverBookAuthors GetBookAuthors(int hardcoverBookId);

        // The book an edition belongs to, or null.
        int? GetEditionBookId(int hardcoverEditionId);

        TalearrHardcoverClientStatus GetStatus();
    }

    // Talearr: reads one author's full catalog from Hardcover's GraphQL API
    // (https://api.hardcover.app/v1/graphql) for Talearr's own metadata source (Phase 3,
    // docs/TALEARR_METADATA_SOURCE.md). Read only: no mutations.
    //
    // Request plan for one author (each request has exactly one top-level field, so it costs
    // one rate-limit token, and no selection is nested more than two levels below the root,
    // inside Hardcover's announced query depth limit of 3):
    //   1. authors(where id) ............................ 1 request
    //   2. books(where contributions.author_id), keyset paged by id, PageSize per page
    //   3. editions(where book_id _in [...]), books chunked so each chunk holds about PageSize
    //      editions (from books.editions_count), keyset paged by id
    //   4. series(where id _in [...]), SeriesChunk ids per request
    // Results are cached in memory for CacheLifetime.
    public class TalearrHardcoverMetadataClient : ITalearrHardcoverMetadataClient
    {
        public const string Endpoint = "https://api.hardcover.app/v1/graphql";
        public const int PageSize = 100;
        public const int SeriesChunk = 100;
        public const int MaxBookIdsPerEditionChunk = 50;
        public const int MaxPagesPerQuery = 200;

        // Stop before Hardcover's daily limit runs out so the user's other Hardcover features
        // (search, lists, import lists) keep working.
        public const int DailyReserve = 200;

        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);
        public const int MaxCachedAuthors = 200;
        private const int MaxAttempts = 4;

        internal const string AuthorSignaturesQuery = @"
            query TalearrAuthorSignatures($ids: [Int!]!, $limit: Int!) {
                authors(where: { id: { _in: $ids } }, limit: $limit) {
                    id
                    books_count
                    updated_at
                }
            }";

        internal const string BookAuthorsQuery = @"
            query TalearrBookAuthors($id: Int!) {
                books(where: { id: { _eq: $id } }, limit: 1) {
                    id
                    canonical_id
                    contributions {
                        author_id
                        contribution
                    }
                }
            }";

        internal const string EditionBookQuery = @"
            query TalearrEditionBook($id: Int!) {
                editions(where: { id: { _eq: $id } }, limit: 1) {
                    id
                    book_id
                }
            }";

        internal const string AuthorQuery = @"
            query TalearrAuthor($id: Int!) {
                authors(where: { id: { _eq: $id } }, limit: 1) {
                    id
                    name
                    slug
                    bio
                    born_date
                    death_date
                    canonical_id
                    books_count
                    users_count
                    updated_at
                    alternate_names
                    identifiers
                    image { url }
                }
            }";

        internal const string BooksQuery = @"
            query TalearrAuthorBooks($authorId: Int!, $limit: Int!, $afterId: Int!) {
                books(
                    where: { contributions: { author_id: { _eq: $authorId } }, id: { _gt: $afterId } }
                    order_by: { id: asc }
                    limit: $limit
                ) {
                    id
                    title
                    subtitle
                    slug
                    description
                    release_date
                    release_year
                    pages
                    audio_seconds
                    rating
                    ratings_count
                    users_count
                    compilation
                    book_category_id
                    literary_type_id
                    book_status_id
                    canonical_id
                    is_partial_book
                    editions_count
                    default_audio_edition_id
                    default_ebook_edition_id
                    default_physical_edition_id
                    updated_at
                    cached_tags
                    image { url }
                    contributions(where: { author_id: { _eq: $authorId } }) { contribution }
                    book_series { series_id position details featured compilation }
                }
            }";

        internal const string EditionsQuery = @"
            query TalearrEditions($bookIds: [Int!]!, $limit: Int!, $afterId: Int!) {
                editions(
                    where: { book_id: { _in: $bookIds }, id: { _gt: $afterId } }
                    order_by: { id: asc }
                    limit: $limit
                ) {
                    id
                    book_id
                    title
                    subtitle
                    isbn_10
                    isbn_13
                    asin
                    reading_format_id
                    edition_format
                    edition_information
                    release_date
                    pages
                    audio_seconds
                    rating
                    users_count
                    canonical_id
                    updated_at
                    cached_contributors
                    language { code2 code3 language }
                    publisher { name }
                    image { url }
                }
            }";

        internal const string SeriesQuery = @"
            query TalearrSeries($ids: [Int!]!, $limit: Int!) {
                series(where: { id: { _in: $ids } }, limit: $limit) {
                    id
                    name
                    slug
                    description
                    books_count
                    primary_books_count
                    is_completed
                    canonical_id
                }
            }";

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly TalearrHardcoverRateLimiter _limiter;
        private readonly Func<DateTime> _clock;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, (DateTime FetchedAt, HardcoverAuthorCatalog Catalog)> _cache = new Dictionary<int, (DateTime, HardcoverAuthorCatalog)>();
        private int _requests;
        private string _lastError;
        private DateTime? _lastErrorAt;

        public TalearrHardcoverMetadataClient(IHttpClient httpClient, IConfigService configService, Logger logger)
            : this(httpClient, configService, logger, new TalearrHardcoverRateLimiter(), () => DateTime.UtcNow)
        {
        }

        internal TalearrHardcoverMetadataClient(IHttpClient httpClient,
                                                IConfigService configService,
                                                Logger logger,
                                                TalearrHardcoverRateLimiter limiter,
                                                Func<DateTime> clock)
        {
            _httpClient = httpClient;
            _configService = configService;
            _logger = logger;
            _limiter = limiter;
            _clock = clock;
        }

        public static string UserAgent => $"Talearr/{BuildInfo.TalearrVersion ?? "0"} (metadata source; github.com/DanCue44/Talearr)";

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_configService?.HardcoverApiToken);

        public TalearrHardcoverClientStatus GetStatus()
        {
            lock (_cacheLock)
            {
                return new TalearrHardcoverClientStatus
                {
                    Configured = IsConfigured,
                    RequestsSinceStart = _requests,
                    DailyRemaining = _limiter.DailyRemaining,
                    CachedAuthors = _cache.Count,
                    LastError = _lastError,
                    LastErrorAt = _lastErrorAt
                };
            }
        }

        public HardcoverAuthorCatalog GetAuthorCatalog(int hardcoverAuthorId, bool useCache = true)
        {
            if (hardcoverAuthorId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(hardcoverAuthorId));
            }

            if (!IsConfigured)
            {
                throw new TalearrHardcoverException("Set the Hardcover API token in Settings > Metadata to use Hardcover as a metadata source.");
            }

            if (useCache)
            {
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(hardcoverAuthorId, out var cached) && _clock() - cached.FetchedAt < CacheLifetime)
                    {
                        return cached.Catalog;
                    }
                }
            }

            try
            {
                var catalog = FetchCatalog(hardcoverAuthorId);
                lock (_cacheLock)
                {
                    if (_cache.Count >= MaxCachedAuthors && !_cache.ContainsKey(hardcoverAuthorId))
                    {
                        var oldest = _cache.OrderBy(kvp => kvp.Value.FetchedAt).First().Key;
                        _cache.Remove(oldest);
                    }

                    _cache[hardcoverAuthorId] = (_clock(), catalog);
                }

                return catalog;
            }
            catch (Exception ex)
            {
                lock (_cacheLock)
                {
                    _lastError = ex.Message;
                    _lastErrorAt = _clock();
                }

                throw;
            }
        }

        public Dictionary<int, HardcoverAuthorSignature> GetAuthorSignatures(IReadOnlyCollection<int> hardcoverAuthorIds)
        {
            RequireToken();

            var result = new Dictionary<int, HardcoverAuthorSignature>();
            var ids = (hardcoverAuthorIds ?? Array.Empty<int>()).Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
            foreach (var chunk in ids.Chunk(PageSize))
            {
                foreach (var row in Query(AuthorSignaturesQuery, new { ids = chunk, limit = chunk.Length }, "authors"))
                {
                    var id = row.Value<int?>("id") ?? 0;
                    if (id > 0)
                    {
                        result[id] = new HardcoverAuthorSignature
                        {
                            AuthorId = id,
                            BooksCount = row.Value<int?>("books_count") ?? 0,
                            UpdatedAt = Date(row, "updated_at")
                        };
                    }
                }
            }

            return result;
        }

        public HardcoverBookAuthors GetBookAuthors(int hardcoverBookId)
        {
            RequireToken();

            var bookId = hardcoverBookId;
            for (var hop = 0; hop < 3 && bookId > 0; hop++)
            {
                var row = Query(BookAuthorsQuery, new { id = bookId }, "books").FirstOrDefault();
                if (row == null)
                {
                    return null;
                }

                var canonical = row.Value<int?>("canonical_id");
                if (canonical.HasValue && canonical.Value > 0 && canonical.Value != bookId)
                {
                    bookId = canonical.Value;
                    continue;
                }

                var credits = (row["contributions"] as JArray ?? new JArray())
                    .Select(c => (AuthorId: c.Value<int?>("author_id") ?? 0, Role: c.Value<string>("contribution")))
                    .Where(c => c.AuthorId > 0)
                    .ToList();

                return new HardcoverBookAuthors
                {
                    BookId = bookId,
                    AuthorIds = credits
                        .OrderByDescending(c => string.IsNullOrWhiteSpace(c.Role) || HardcoverContributionRoles.IsPrimaryAuthor(c.Role))
                        .Select(c => c.AuthorId)
                        .Distinct()
                        .ToList()
                };
            }

            return null;
        }

        public int? GetEditionBookId(int hardcoverEditionId)
        {
            RequireToken();

            var row = Query(EditionBookQuery, new { id = hardcoverEditionId }, "editions").FirstOrDefault();
            var bookId = row?.Value<int?>("book_id");
            return bookId > 0 ? bookId : null;
        }

        private void RequireToken()
        {
            if (!IsConfigured)
            {
                throw new TalearrHardcoverException("Set the Hardcover API token in Settings > Metadata to use Hardcover as a metadata source.");
            }
        }

        private HardcoverAuthorCatalog FetchCatalog(int authorId)
        {
            var requestsBefore = _requests;
            var authorRows = Query(AuthorQuery, new { id = authorId }, "authors");
            var authorRow = authorRows.FirstOrDefault();
            if (authorRow == null)
            {
                return null;
            }

            var catalog = new HardcoverAuthorCatalog
            {
                Author = ParseAuthor(authorRow),
                FetchedAt = _clock()
            };

            // Books: every book with any contribution by this author, then keep the ones where
            // the author has a primary role (HardcoverContributionRoles, same rule as search).
            var editionCounts = new Dictionary<int, int>();
            var bookRows = PageByKeyset((afterId, limit) => Query(BooksQuery, new { authorId, limit, afterId }, "books"));
            foreach (var row in bookRows)
            {
                var book = ParseBook(row);
                if (book == null)
                {
                    continue;
                }

                if (!book.AuthorRoles.Any(HardcoverContributionRoles.IsPrimaryAuthor) && book.AuthorRoles.Count > 0)
                {
                    catalog.SkippedNotPrimary++;
                    continue;
                }

                if (book.BookStatusId == 3)
                {
                    catalog.SkippedDeleted++;
                    continue;
                }

                if (book.BookStatusId == 4 || (book.CanonicalId.HasValue && book.CanonicalId.Value != book.Id))
                {
                    catalog.DuplicateBooks.Add(book);
                    continue;
                }

                if (book.IsPartialBook)
                {
                    catalog.SkippedPartial++;
                    continue;
                }

                catalog.Books.Add(book);
                editionCounts[book.Id] = row.Value<int?>("editions_count") ?? 1;
            }

            // Editions, chunked by expected size so most chunks need one request.
            var byId = catalog.Books.ToDictionary(b => b.Id);
            foreach (var chunk in ChunkByEditionCount(catalog.Books.Select(b => b.Id).ToList(), editionCounts))
            {
                var expected = chunk.Sum(id => editionCounts.TryGetValue(id, out var c) ? c : 1);
                var editionRows = PageByKeyset((afterId, limit) => Query(EditionsQuery, new { bookIds = chunk, limit, afterId }, "editions"), expected);
                foreach (var row in editionRows)
                {
                    var edition = ParseEdition(row);
                    if (edition != null && byId.TryGetValue(edition.BookId, out var owner))
                    {
                        owner.Editions.Add(edition);
                    }
                }
            }

            // Series named by the books' book_series rows.
            var seriesIds = catalog.Books
                .SelectMany(b => b.Series)
                .Select(s => s.SeriesId)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            foreach (var chunk in seriesIds.Chunk(SeriesChunk))
            {
                foreach (var row in Query(SeriesQuery, new { ids = chunk, limit = chunk.Length }, "series"))
                {
                    var series = ParseSeries(row);
                    if (series != null)
                    {
                        catalog.Series[series.Id] = series;
                    }
                }
            }

            catalog.RequestCount = _requests - requestsBefore;
            _logger.Debug("Hardcover catalog for author hc:{0}: {1} books, {2} editions, {3} series, {4} duplicates, {5} requests",
                authorId,
                catalog.Books.Count,
                catalog.Books.Sum(b => b.Editions.Count),
                catalog.Series.Count,
                catalog.DuplicateBooks.Count,
                catalog.RequestCount);

            return catalog;
        }

        // Keyset paging (id > last id). Hardcover does not document a maximum page size, so the
        // client does not trust a short page to be the last one until it has seen the server
        // honor the requested limit once, or the expected total (when known) is reached. A short
        // first page is followed by one more request; a non-empty answer means the server caps
        // pages below PageSize and paging continues.
        internal static List<JToken> PageByKeyset(Func<int, int, List<JToken>> fetch, int? expectedTotal = null)
        {
            var all = new List<JToken>();
            var afterId = 0;
            var limitHonored = false;

            for (var page = 0; page < MaxPagesPerQuery; page++)
            {
                var rows = fetch(afterId, PageSize) ?? new List<JToken>();
                if (rows.Count == 0)
                {
                    break;
                }

                all.AddRange(rows);
                var lastId = rows.Max(r => r.Value<int?>("id") ?? 0);
                if (lastId <= afterId)
                {
                    // Not moving forward; stop rather than loop.
                    break;
                }

                afterId = lastId;

                if (rows.Count >= PageSize)
                {
                    limitHonored = true;
                    continue;
                }

                if (limitHonored || (expectedTotal.HasValue && all.Count >= expectedTotal.Value))
                {
                    break;
                }
            }

            return all;
        }

        internal static List<List<int>> ChunkByEditionCount(List<int> bookIds, IReadOnlyDictionary<int, int> editionCounts)
        {
            var chunks = new List<List<int>>();
            var current = new List<int>();
            var size = 0;

            foreach (var id in bookIds)
            {
                var count = Math.Max(1, editionCounts != null && editionCounts.TryGetValue(id, out var c) ? c : 1);
                if (current.Count > 0 && (size + count > PageSize || current.Count >= MaxBookIdsPerEditionChunk))
                {
                    chunks.Add(current);
                    current = new List<int>();
                    size = 0;
                }

                current.Add(id);
                size += count;
            }

            if (current.Count > 0)
            {
                chunks.Add(current);
            }

            return chunks;
        }

        private List<JToken> Query(string query, object variables, string root)
        {
            var body = JsonConvert.SerializeObject(new { query, variables });

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (_limiter.DailyRemaining.HasValue && _limiter.DailyRemaining.Value <= DailyReserve)
                {
                    throw new TalearrHardcoverBudgetException("Hardcover has {0} requests left today; Talearr keeps the last {1} for search and lists", _limiter.DailyRemaining.Value, DailyReserve);
                }

                _limiter.Acquire();

                var request = new HttpRequestBuilder(Endpoint)
                    .SetHeader("Content-Type", "application/json")
                    .SetHeader("Accept", "application/json")
                    .SetHeader("User-Agent", UserAgent)
                    .Build();

                request.Method = HttpMethod.Post;
                request.SuppressHttpError = true;
                request.LogHttpError = false;
                request.RequestTimeout = RequestTimeout;
                var token = _configService.HardcoverApiToken.Trim();
                request.Headers.Add("Authorization", token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? token : "Bearer " + token);
                request.SetContent(body);

                HttpResponse response;
                try
                {
                    _requests++;
                    response = _httpClient.Execute(request);
                }
                catch (HttpException ex) when (ex.Response != null)
                {
                    response = ex.Response;
                }
                catch (Exception ex) when (attempt < MaxAttempts && ex is not TalearrHardcoverException)
                {
                    _logger.Debug(ex, "Hardcover request failed (attempt {0}/{1})", attempt, MaxAttempts);
                    continue;
                }

                _limiter.ObserveRateLimitHeader(response.Headers?.GetSingleValue("RateLimit"));

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _limiter.ObserveTooManyRequests(response.Headers?.GetSingleValue("Retry-After"));
                    _logger.Debug("Hardcover answered 429 (attempt {0}/{1})", attempt, MaxAttempts);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new TalearrHardcoverException("Hardcover rejected the API token (401). Check it in Settings > Metadata.");
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new TalearrHardcoverException("Hardcover refused the request (403): {0}", Truncate(response.Content));
                }

                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    _logger.Debug("Hardcover answered {0} (attempt {1}/{2})", (int)response.StatusCode, attempt, MaxAttempts);
                    if (attempt < MaxAttempts)
                    {
                        continue;
                    }

                    throw new TalearrHardcoverException("Hardcover answered {0}", (int)response.StatusCode);
                }

                if (response.HasHttpError)
                {
                    throw new TalearrHardcoverException("Hardcover answered {0}: {1}", (int)response.StatusCode, Truncate(response.Content));
                }

                return ParseRows(response.Content, root);
            }

            throw new TalearrHardcoverException("Hardcover did not answer after {0} attempts", MaxAttempts);
        }

        internal static List<JToken> ParseRows(string content, string root)
        {
            JObject parsed;
            try
            {
                // Dates stay strings; the parsers below read them explicitly.
                using var reader = new JsonTextReader(new StringReader(content ?? string.Empty)) { DateParseHandling = DateParseHandling.None };
                parsed = JObject.Load(reader);
            }
            catch (JsonException ex)
            {
                throw new TalearrHardcoverException("Hardcover sent a response that is not JSON", ex);
            }

            if (parsed["errors"] is JArray errors && errors.Count > 0)
            {
                var messages = errors.Select(e => e.Type == JTokenType.Object ? e.Value<string>("message") : e.ToString());
                throw new TalearrHardcoverException("Hardcover GraphQL error: {0}", string.Join("; ", messages));
            }

            if (parsed["data"]?[root] is not JArray rows)
            {
                throw new TalearrHardcoverException("Hardcover response has no data.{0}", root);
            }

            return rows.ToList();
        }

        internal static HardcoverAuthorRecord ParseAuthor(JToken row)
        {
            var author = new HardcoverAuthorRecord
            {
                Id = row.Value<int?>("id") ?? 0,
                Name = Str(row, "name"),
                Slug = Str(row, "slug"),
                Bio = Str(row, "bio"),
                BornDate = Str(row, "born_date"),
                DeathDate = Str(row, "death_date"),
                CanonicalId = row.Value<int?>("canonical_id"),
                BooksCount = row.Value<int?>("books_count") ?? 0,
                UsersCount = row.Value<int?>("users_count") ?? 0,
                UpdatedAt = Date(row, "updated_at"),
                ImageUrl = ImageUrl(row)
            };

            if (row["alternate_names"] is JArray names)
            {
                author.AlternateNames = names
                    .Select(n => n.Type == JTokenType.String ? n.Value<string>() : n.Type == JTokenType.Object ? n.Value<string>("name") : null)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (row["identifiers"] is JObject identifiers)
            {
                foreach (var property in identifiers.Properties())
                {
                    var values = property.Value.Type == JTokenType.Array
                        ? property.Value.Select(v => v.Type == JTokenType.Object ? v.Value<string>("value") ?? v.Value<string>("id") : v.ToString())
                        : new[] { property.Value.ToString() };

                    var list = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
                    if (list.Count > 0)
                    {
                        author.Identifiers[property.Name.Trim().ToLowerInvariant()] = list;
                    }
                }
            }

            return author.Id > 0 ? author : null;
        }

        internal static HardcoverBookRecord ParseBook(JToken row)
        {
            var id = row.Value<int?>("id") ?? 0;
            if (id <= 0)
            {
                return null;
            }

            var book = new HardcoverBookRecord
            {
                Id = id,
                Title = Str(row, "title"),
                Subtitle = Str(row, "subtitle"),
                Slug = Str(row, "slug"),
                Description = Str(row, "description"),
                ReleaseDate = Str(row, "release_date"),
                ReleaseYear = row.Value<int?>("release_year"),
                Pages = row.Value<int?>("pages"),
                AudioSeconds = row.Value<int?>("audio_seconds"),
                Rating = Dec(row, "rating"),
                RatingsCount = row.Value<int?>("ratings_count") ?? 0,
                UsersCount = row.Value<int?>("users_count") ?? 0,
                Compilation = row.Value<bool?>("compilation") ?? false,
                BookCategoryId = row.Value<int?>("book_category_id"),
                LiteraryTypeId = row.Value<int?>("literary_type_id"),
                BookStatusId = row.Value<int?>("book_status_id") ?? 1,
                CanonicalId = row.Value<int?>("canonical_id"),
                IsPartialBook = row.Value<bool?>("is_partial_book") ?? false,
                DefaultAudioEditionId = row.Value<int?>("default_audio_edition_id"),
                DefaultEbookEditionId = row.Value<int?>("default_ebook_edition_id"),
                DefaultPhysicalEditionId = row.Value<int?>("default_physical_edition_id"),
                UpdatedAt = Date(row, "updated_at"),
                ImageUrl = ImageUrl(row),
                Genres = Tags(row["cached_tags"], "Genre"),
                Moods = Tags(row["cached_tags"], "Mood")
            };

            if (row["contributions"] is JArray contributions)
            {
                // A null role is Hardcover's common "author" marker; keep it as an empty string so
                // HardcoverContributionRoles treats it the same way search does.
                book.AuthorRoles = contributions.Select(c => c.Value<string>("contribution") ?? string.Empty).ToList();
            }

            if (row["book_series"] is JArray seriesRows)
            {
                foreach (var s in seriesRows)
                {
                    var seriesId = s.Value<int?>("series_id") ?? 0;
                    if (seriesId <= 0)
                    {
                        continue;
                    }

                    book.Series.Add(new HardcoverBookSeriesRecord
                    {
                        SeriesId = seriesId,
                        Position = Dec(s, "position"),
                        Details = Str(s, "details"),
                        Featured = s.Value<bool?>("featured") ?? false,
                        Compilation = s.Value<bool?>("compilation") ?? false
                    });
                }
            }

            return book;
        }

        internal static HardcoverEditionRecord ParseEdition(JToken row)
        {
            var id = row.Value<int?>("id") ?? 0;
            if (id <= 0)
            {
                return null;
            }

            var edition = new HardcoverEditionRecord
            {
                Id = id,
                BookId = row.Value<int?>("book_id") ?? 0,
                Title = Str(row, "title"),
                Subtitle = Str(row, "subtitle"),
                Isbn10 = Str(row, "isbn_10"),
                Isbn13 = Str(row, "isbn_13"),
                Asin = Str(row, "asin"),
                ReadingFormatId = row.Value<int?>("reading_format_id") ?? 0,
                EditionFormat = Str(row, "edition_format"),
                EditionInformation = Str(row, "edition_information"),
                ReleaseDate = Str(row, "release_date"),
                Pages = row.Value<int?>("pages"),
                AudioSeconds = row.Value<int?>("audio_seconds"),
                Rating = Dec(row, "rating"),
                UsersCount = row.Value<int?>("users_count") ?? 0,
                CanonicalId = row.Value<int?>("canonical_id"),
                UpdatedAt = Date(row, "updated_at"),
                LanguageCode2 = Str(row["language"], "code2"),
                LanguageCode3 = Str(row["language"], "code3"),
                LanguageName = Str(row["language"], "language"),
                Publisher = Str(row["publisher"], "name"),
                ImageUrl = ImageUrl(row)
            };

            if (row["cached_contributors"] is JArray contributors)
            {
                foreach (var c in contributors)
                {
                    var author = c["author"] as JObject;
                    var name = Str(author, "name") ?? Str(c, "name");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    edition.Contributors.Add(new HardcoverContributorRecord
                    {
                        AuthorId = author?.Value<int?>("id"),
                        Name = name,
                        Contribution = Str(c, "contribution")
                    });
                }
            }

            return edition;
        }

        internal static HardcoverSeriesRecord ParseSeries(JToken row)
        {
            var id = row.Value<int?>("id") ?? 0;
            if (id <= 0)
            {
                return null;
            }

            return new HardcoverSeriesRecord
            {
                Id = id,
                Name = Str(row, "name"),
                Slug = Str(row, "slug"),
                Description = Str(row, "description"),
                BooksCount = row.Value<int?>("books_count") ?? 0,
                PrimaryBooksCount = row.Value<int?>("primary_books_count"),
                IsCompleted = row.Value<bool?>("is_completed"),
                CanonicalId = row.Value<int?>("canonical_id")
            };
        }

        // cached_tags (jsonb). Assumed shape: { "Genre": [{ "tag": "Fantasy", "count": 12 }, ...], "Mood": [...] }.
        // Strings in place of objects are accepted too.
        private static List<string> Tags(JToken cachedTags, string category)
        {
            if (cachedTags is not JObject tags)
            {
                return new List<string>();
            }

            var entries = tags.Properties().FirstOrDefault(p => string.Equals(p.Name, category, StringComparison.OrdinalIgnoreCase))?.Value as JArray;
            if (entries == null)
            {
                return new List<string>();
            }

            return entries
                .Select(e => e.Type == JTokenType.String ? e.Value<string>() : e.Type == JTokenType.Object ? e.Value<string>("tag") : null)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
        }

        private static string ImageUrl(JToken row)
        {
            var url = Str(row?["image"], "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            // Some rows carry only cached_image (jsonb) with a url field.
            return Str(row?["cached_image"], "url");
        }

        private static string Str(JToken token, string name)
        {
            if (token is not JObject obj)
            {
                return null;
            }

            var value = obj[name];
            if (value == null || value.Type == JTokenType.Null)
            {
                return null;
            }

            var text = value.Type == JTokenType.Date
                ? value.Value<DateTime>().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString();

            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static decimal? Dec(JToken token, string name)
        {
            var value = (token as JObject)?[name];
            if (value == null || value.Type == JTokenType.Null)
            {
                return null;
            }

            return decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }

        private static DateTime? Date(JToken token, string name)
        {
            var value = (token as JObject)?[name];
            if (value == null || value.Type == JTokenType.Null)
            {
                return null;
            }

            if (value.Type == JTokenType.Date)
            {
                return DateTime.SpecifyKind(value.Value<DateTime>(), DateTimeKind.Utc);
            }

            return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        private static string Truncate(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            return content.Length > 300 ? content.Substring(0, 300) : content;
        }
    }
}
