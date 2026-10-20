using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Catalog
{
    // Talearr: one provider id of a row the merge tool removed, and the row it now means.
    public sealed class TalearrMergeRedirect
    {
        public const string Author = "author";

        // A work id (hc:/gr:/ol:) of a merged-away book row.
        public const string Book = "book";

        // An edition provider id (az:/gr:/gb:/...) of a merged-away book row, used only for
        // remote books that carry no work id at all.
        public const string Edition = "edition";

        public const int AnyMediaType = -1;

        public string EntityType { get; set; }
        public int MediaType { get; set; } = AnyMediaType;
        public string ProviderId { get; set; }
        public int TargetId { get; set; }
        public int? SourceId { get; set; }
        public string Title { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public interface ITalearrMergeRedirectStore
    {
        // Records the ids of a merged-away row. Redirects that pointed at the source row (an
        // earlier merge into it) are moved onto the new target. False when talearr.db is unusable.
        bool Record(string entityType, int sourceId, int targetId, IEnumerable<TalearrMergeRedirect> redirects);

        // The kept row for a provider id, or null.
        int? FindAuthor(string providerId);
        int? FindBook(string providerId, int mediaType);

        // Every book and edition redirect (the table is tiny). Empty when talearr.db is unusable.
        List<TalearrMergeRedirect> GetBookRedirects();
    }

    // Talearr: reads and writes MergeRedirects in talearr.db. Best effort like the other sidecar
    // stores: a failure is logged and treated as "no redirect", which is the 0.16.0 behavior.
    public class TalearrMergeRedirectStore : ITalearrMergeRedirectStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrMergeRedirectStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public bool Record(string entityType, int sourceId, int targetId, IEnumerable<TalearrMergeRedirect> redirects)
        {
            if (string.IsNullOrWhiteSpace(entityType) || sourceId <= 0 || targetId <= 0 || sourceId == targetId)
            {
                return false;
            }

            var types = entityType == TalearrMergeRedirect.Author
                ? new[] { TalearrMergeRedirect.Author }
                : new[] { TalearrMergeRedirect.Book, TalearrMergeRedirect.Edition };

            var rows = (redirects ?? Enumerable.Empty<TalearrMergeRedirect>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.ProviderId) && types.Contains(r.EntityType))
                .GroupBy(r => (r.EntityType, r.MediaType, Id: r.ProviderId.Trim().ToLowerInvariant()))
                .Select(g => g.First())
                .ToList();

            return Run("record merge redirects", false, connection =>
            {
                using var transaction = connection.BeginTransaction();
                var now = TalearrRecordingsStore.FormatDate(DateTime.UtcNow);

                // A chain (A into B, later B into C) ends at C.
                connection.Execute("UPDATE MergeRedirects SET TargetId = @Target WHERE EntityType IN @Types AND TargetId = @Source",
                    new { Target = targetId, Source = sourceId, Types = types },
                    transaction);

                foreach (var row in rows)
                {
                    connection.Execute(@"
INSERT INTO MergeRedirects (EntityType, MediaType, ProviderId, TargetId, SourceId, Title, CreatedAt)
VALUES (@EntityType, @MediaType, @ProviderId, @TargetId, @SourceId, @Title, @CreatedAt)
ON CONFLICT (EntityType, MediaType, ProviderId) DO UPDATE SET
    TargetId = excluded.TargetId, SourceId = excluded.SourceId, Title = excluded.Title, CreatedAt = excluded.CreatedAt;",
                        new
                        {
                            row.EntityType,
                            row.MediaType,
                            ProviderId = row.ProviderId.Trim(),
                            TargetId = targetId,
                            SourceId = sourceId,
                            row.Title,
                            CreatedAt = now
                        },
                        transaction);
                }

                transaction.Commit();
                return true;
            });
        }

        public int? FindAuthor(string providerId)
        {
            return Find(TalearrMergeRedirect.Author, providerId, TalearrMergeRedirect.AnyMediaType);
        }

        public int? FindBook(string providerId, int mediaType)
        {
            return Find(TalearrMergeRedirect.Book, providerId, mediaType) ?? Find(TalearrMergeRedirect.Edition, providerId, mediaType);
        }

        public List<TalearrMergeRedirect> GetBookRedirects()
        {
            return Run("read merge redirects", new List<TalearrMergeRedirect>(), connection =>
                connection.Query<Row>("SELECT * FROM MergeRedirects WHERE EntityType IN @Types",
                        new { Types = new[] { TalearrMergeRedirect.Book, TalearrMergeRedirect.Edition } })
                    .Select(ToModel)
                    .ToList());
        }

        // Canonical provider id ("hc:123", "az:B0ABC"), or null when it is not one.
        public static string Canonical(string providerId, string defaultPrefix = null)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            try
            {
                var id = providerId.Trim();
                if (!id.Contains(':') && defaultPrefix == null)
                {
                    return null;
                }

                return ProviderIdHelper.Canonicalize(id, id.Contains(':') ? null : defaultPrefix);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private int? Find(string entityType, string providerId, int mediaType)
        {
            var id = Canonical(providerId);
            if (id == null)
            {
                return null;
            }

            return Run("read a merge redirect", (int?)null, connection =>
                connection.QueryFirstOrDefault<long?>(
                    "SELECT TargetId FROM MergeRedirects WHERE EntityType = @EntityType AND MediaType = @MediaType AND ProviderId = @ProviderId",
                    new { EntityType = entityType, MediaType = mediaType, ProviderId = id }) is long target
                    ? (int)target
                    : null);
        }

        private static TalearrMergeRedirect ToModel(Row row)
        {
            return new TalearrMergeRedirect
            {
                EntityType = row.EntityType,
                MediaType = (int)row.MediaType,
                ProviderId = row.ProviderId,
                TargetId = (int)row.TargetId,
                SourceId = row.SourceId.HasValue ? (int?)row.SourceId.Value : null,
                Title = row.Title,
                CreatedAt = TalearrRecordingsStore.ParseDate(row.CreatedAt)
            };
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; merged ids are not redirected", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public string EntityType { get; set; }
            public long MediaType { get; set; }
            public string ProviderId { get; set; }
            public long TargetId { get; set; }
            public long? SourceId { get; set; }
            public string Title { get; set; }
            public string CreatedAt { get; set; }
        }
    }
}
