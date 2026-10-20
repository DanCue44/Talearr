using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;

namespace NzbDrone.Core.Talearr.Recordings
{
    public interface ITalearrRecordingsStore
    {
        AudibleProduct GetProduct(string marketplace, string asin);
        List<AudibleProduct> GetProducts(string marketplace, IEnumerable<string> asins);
        void UpsertProducts(IEnumerable<AudibleProduct> products);

        EditionAudibleLink GetLink(string foreignEditionId, string marketplace);
        List<EditionAudibleLink> GetLinks(IEnumerable<string> foreignEditionIds, string marketplace);
        void UpsertLink(EditionAudibleLink link);

        RecordingRequest AddRequest(RecordingRequest request);
        List<RecordingRequest> GetRequests(string baseBookId);

        // Drops cache rows not refreshed since the cutoff. Returns the number of rows removed.
        int PruneOlderThan(DateTime cutoffUtc);
    }

    // Talearr: reads and writes talearr.db. Every call is best effort: a failure is logged and
    // treated as a cache miss (or a skipped write), so a broken or missing talearr.db never breaks
    // a search, an import or an API call. A corrupt file is recreated on the spot.
    public class TalearrRecordingsStore : ITalearrRecordingsStore
    {
        // SQLite's default variable limit is 999 (older builds) or 32766; stay well under both.
        private const int ChunkSize = 200;

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrRecordingsStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public static string NormalizeMarketplace(string marketplace)
        {
            return TalearrAudibleMarketplaces.Get(marketplace).Code;
        }

        public static string NormalizeAsin(string asin)
        {
            return string.IsNullOrWhiteSpace(asin) ? null : asin.Trim().ToUpperInvariant();
        }

        public AudibleProduct GetProduct(string marketplace, string asin)
        {
            var normalized = NormalizeAsin(asin);
            if (normalized == null)
            {
                return null;
            }

            return GetProducts(marketplace, new[] { normalized }).FirstOrDefault();
        }

        public List<AudibleProduct> GetProducts(string marketplace, IEnumerable<string> asins)
        {
            var market = NormalizeMarketplace(marketplace);
            var keys = (asins ?? Enumerable.Empty<string>())
                .Select(NormalizeAsin)
                .Where(a => a != null)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (keys.Count == 0)
            {
                return new List<AudibleProduct>();
            }

            return Run("read Audible products", new List<AudibleProduct>(), connection =>
            {
                var result = new List<AudibleProduct>();
                foreach (var chunk in keys.Chunk(ChunkSize))
                {
                    result.AddRange(connection
                        .Query<ProductRow>("SELECT * FROM AudibleProducts WHERE Marketplace = @Marketplace AND Asin IN @Asins",
                            new { Marketplace = market, Asins = chunk })
                        .Select(ToModel));
                }

                return result;
            });
        }

        public void UpsertProducts(IEnumerable<AudibleProduct> products)
        {
            var rows = (products ?? Enumerable.Empty<AudibleProduct>())
                .Where(p => p != null && NormalizeAsin(p.Asin) != null)
                .Select(ToRow)
                .ToList();

            if (rows.Count == 0)
            {
                return;
            }

            Run("write Audible products", 0, connection =>
            {
                using var transaction = connection.BeginTransaction();
                connection.Execute(@"
INSERT INTO AudibleProducts (Marketplace, Asin, Status, Title, Subtitle, FormatType, RuntimeMinutes, Narrators, Authors, Publisher, ReleaseDate, Language, Source, FetchedAt)
VALUES (@Marketplace, @Asin, @Status, @Title, @Subtitle, @FormatType, @RuntimeMinutes, @Narrators, @Authors, @Publisher, @ReleaseDate, @Language, @Source, @FetchedAt)
ON CONFLICT (Marketplace, Asin) DO UPDATE SET
    Status = excluded.Status, Title = excluded.Title, Subtitle = excluded.Subtitle, FormatType = excluded.FormatType,
    RuntimeMinutes = excluded.RuntimeMinutes, Narrators = excluded.Narrators, Authors = excluded.Authors,
    Publisher = excluded.Publisher, ReleaseDate = excluded.ReleaseDate, Language = excluded.Language,
    Source = excluded.Source, FetchedAt = excluded.FetchedAt;", rows, transaction);
                transaction.Commit();
                return rows.Count;
            });
        }

        public EditionAudibleLink GetLink(string foreignEditionId, string marketplace)
        {
            if (string.IsNullOrWhiteSpace(foreignEditionId))
            {
                return null;
            }

            return GetLinks(new[] { foreignEditionId }, marketplace).FirstOrDefault();
        }

        public List<EditionAudibleLink> GetLinks(IEnumerable<string> foreignEditionIds, string marketplace)
        {
            var market = NormalizeMarketplace(marketplace);
            var keys = (foreignEditionIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (keys.Count == 0)
            {
                return new List<EditionAudibleLink>();
            }

            return Run("read edition links", new List<EditionAudibleLink>(), connection =>
            {
                var result = new List<EditionAudibleLink>();
                foreach (var chunk in keys.Chunk(ChunkSize))
                {
                    result.AddRange(connection
                        .Query<LinkRow>("SELECT * FROM EditionAudibleLinks WHERE Marketplace = @Marketplace AND ForeignEditionId IN @Ids",
                            new { Marketplace = market, Ids = chunk })
                        .Select(ToModel));
                }

                return result;
            });
        }

        public void UpsertLink(EditionAudibleLink link)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.ForeignEditionId))
            {
                return;
            }

            var row = new LinkRow
            {
                ForeignEditionId = link.ForeignEditionId.Trim(),
                Marketplace = NormalizeMarketplace(link.Marketplace),
                Asin = NormalizeAsin(link.Asin),
                Method = link.Method ?? EditionAudibleLink.MethodStoredAsin,
                Confidence = link.Confidence,
                EditionIdHint = link.EditionIdHint,
                CheckedAt = FormatDate(link.CheckedAt)
            };

            Run("write edition link", 0, connection => connection.Execute(@"
INSERT INTO EditionAudibleLinks (ForeignEditionId, Marketplace, Asin, Method, Confidence, EditionIdHint, CheckedAt)
VALUES (@ForeignEditionId, @Marketplace, @Asin, @Method, @Confidence, @EditionIdHint, @CheckedAt)
ON CONFLICT (ForeignEditionId, Marketplace) DO UPDATE SET
    Asin = excluded.Asin, Method = excluded.Method, Confidence = excluded.Confidence,
    EditionIdHint = excluded.EditionIdHint, CheckedAt = excluded.CheckedAt;", row));
        }

        public RecordingRequest AddRequest(RecordingRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BaseBookId) || string.IsNullOrWhiteSpace(request.Kind))
            {
                return request;
            }

            if (request.CreatedAt == default)
            {
                request.CreatedAt = DateTime.UtcNow;
            }

            request.Id = Run("write recording request", 0, connection => (int)connection.ExecuteScalar<long>(@"
INSERT INTO RecordingRequests (BaseBookId, CopyBookIdHint, ForeignEditionId, Kind, RequestedBy, ExternalRef, CreatedAt)
VALUES (@BaseBookId, @CopyBookIdHint, @ForeignEditionId, @Kind, @RequestedBy, @ExternalRef, @CreatedAt);
SELECT last_insert_rowid();", new
            {
                request.BaseBookId,
                request.CopyBookIdHint,
                request.ForeignEditionId,
                request.Kind,
                request.RequestedBy,
                request.ExternalRef,
                CreatedAt = FormatDate(request.CreatedAt)
            }));

            return request;
        }

        public List<RecordingRequest> GetRequests(string baseBookId)
        {
            if (string.IsNullOrWhiteSpace(baseBookId))
            {
                return new List<RecordingRequest>();
            }

            return Run("read recording requests", new List<RecordingRequest>(), connection => connection
                .Query<RequestRow>("SELECT * FROM RecordingRequests WHERE BaseBookId = @BaseBookId ORDER BY Id", new { BaseBookId = baseBookId.Trim() })
                .Select(row => new RecordingRequest
                {
                    Id = (int)row.Id,
                    BaseBookId = row.BaseBookId,
                    CopyBookIdHint = row.CopyBookIdHint.HasValue ? (int?)row.CopyBookIdHint.Value : null,
                    ForeignEditionId = row.ForeignEditionId,
                    Kind = row.Kind,
                    RequestedBy = row.RequestedBy,
                    ExternalRef = row.ExternalRef,
                    CreatedAt = ParseDate(row.CreatedAt)
                })
                .ToList());
        }

        public int PruneOlderThan(DateTime cutoffUtc)
        {
            var cutoff = FormatDate(cutoffUtc);
            return Run("prune cache", 0, connection =>
                connection.Execute("DELETE FROM AudibleProducts WHERE FetchedAt < @Cutoff", new { Cutoff = cutoff }) +
                connection.Execute("DELETE FROM EditionAudibleLinks WHERE CheckedAt < @Cutoff", new { Cutoff = cutoff }));
        }

        private T Run<T>(string what, T fallback, Func<IDbConnection, T> action)
        {
            try
            {
                using var connection = _database.OpenConnection();
                return action(connection);
            }
            catch (Exception ex) when (_database.IsCorruption(ex))
            {
                _database.Reset($"corruption found during {what}");
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not {0} in {1}; continuing without the cache", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        // Dates are stored as fixed-width UTC ISO strings so they sort and compare as text, and
        // so no global Dapper type handler can change how they are written.
        internal static string FormatDate(DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
            return utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        internal static DateTime ParseDate(string value)
        {
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return DateTime.MinValue;
        }

        private static string ToJson(List<string> values)
        {
            var clean = (values ?? new List<string>()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
        }

        private static List<string> FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        private static ProductRow ToRow(AudibleProduct product)
        {
            return new ProductRow
            {
                Marketplace = NormalizeMarketplace(product.Marketplace),
                Asin = NormalizeAsin(product.Asin),
                Status = (int)product.Status,
                Title = product.Title,
                Subtitle = product.Subtitle,
                FormatType = string.IsNullOrWhiteSpace(product.FormatType) ? null : product.FormatType.Trim().ToLowerInvariant(),
                RuntimeMinutes = product.RuntimeMinutes,
                Narrators = ToJson(product.Narrators),
                Authors = ToJson(product.Authors),
                Publisher = product.Publisher,
                ReleaseDate = product.ReleaseDate,
                Language = product.Language,
                Source = product.Source ?? AudibleProduct.SourceAudible,
                FetchedAt = FormatDate(product.FetchedAt == default ? DateTime.UtcNow : product.FetchedAt)
            };
        }

        private static AudibleProduct ToModel(ProductRow row)
        {
            return new AudibleProduct
            {
                Marketplace = row.Marketplace,
                Asin = row.Asin,
                Status = Enum.IsDefined(typeof(AudibleProductStatus), (int)row.Status) ? (AudibleProductStatus)row.Status : AudibleProductStatus.NotFound,
                Title = row.Title,
                Subtitle = row.Subtitle,
                FormatType = row.FormatType,
                RuntimeMinutes = row.RuntimeMinutes.HasValue ? (int?)row.RuntimeMinutes.Value : null,
                Narrators = FromJson(row.Narrators),
                Authors = FromJson(row.Authors),
                Publisher = row.Publisher,
                ReleaseDate = row.ReleaseDate,
                Language = row.Language,
                Source = row.Source,
                FetchedAt = ParseDate(row.FetchedAt)
            };
        }

        private static EditionAudibleLink ToModel(LinkRow row)
        {
            return new EditionAudibleLink
            {
                ForeignEditionId = row.ForeignEditionId,
                Marketplace = row.Marketplace,
                Asin = row.Asin,
                Method = row.Method,
                Confidence = row.Confidence,
                EditionIdHint = row.EditionIdHint.HasValue ? (int?)row.EditionIdHint.Value : null,
                CheckedAt = ParseDate(row.CheckedAt)
            };
        }

        // Row shapes use SQLite's native types (INTEGER is long) so Dapper maps them without help.
        private sealed class ProductRow
        {
            public string Marketplace { get; set; }
            public string Asin { get; set; }
            public long Status { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public string FormatType { get; set; }
            public long? RuntimeMinutes { get; set; }
            public string Narrators { get; set; }
            public string Authors { get; set; }
            public string Publisher { get; set; }
            public string ReleaseDate { get; set; }
            public string Language { get; set; }
            public string Source { get; set; }
            public string FetchedAt { get; set; }
        }

        private sealed class LinkRow
        {
            public string ForeignEditionId { get; set; }
            public string Marketplace { get; set; }
            public string Asin { get; set; }
            public string Method { get; set; }
            public double Confidence { get; set; }
            public long? EditionIdHint { get; set; }
            public string CheckedAt { get; set; }
        }

        private sealed class RequestRow
        {
            public long Id { get; set; }
            public string BaseBookId { get; set; }
            public long? CopyBookIdHint { get; set; }
            public string ForeignEditionId { get; set; }
            public string Kind { get; set; }
            public string RequestedBy { get; set; }
            public string ExternalRef { get; set; }
            public string CreatedAt { get; set; }
        }
    }
}
