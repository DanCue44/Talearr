using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: one per-book abridged override, as stored in talearr.db (BookGrabPolicies).
    public sealed class BookGrabPolicyOverride
    {
        // The work: the audiobook row's BaseBookId ("hc:378630"), else "book:<row id>".
        public string WorkKey { get; set; }
        public AbridgedAudiobookPolicy AbridgedPolicy { get; set; }

        // Informational only: the row and monitored edition (TalearrEditionKey) when it was set.
        public int? BookIdHint { get; set; }
        public string ForeignEditionId { get; set; }
        public string Title { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public interface ITalearrBookGrabPolicyStore
    {
        // Every override, keyed by work key (case-insensitive). Empty when talearr.db is unusable.
        Dictionary<string, AbridgedAudiobookPolicy> GetAll();
        BookGrabPolicyOverride Get(string workKey);

        // False when the write did not reach talearr.db.
        bool Upsert(BookGrabPolicyOverride value);
        bool Delete(string workKey);
    }

    // Talearr: reads and writes the BookGrabPolicies table in talearr.db. Like the recordings
    // store, every call is best effort: a failure is logged and treated as "no override", so a
    // missing or broken talearr.db only means every book follows the global grab policy.
    public class TalearrBookGrabPolicyStore : ITalearrBookGrabPolicyStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrBookGrabPolicyStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public Dictionary<string, AbridgedAudiobookPolicy> GetAll()
        {
            var empty = new Dictionary<string, AbridgedAudiobookPolicy>(StringComparer.OrdinalIgnoreCase);
            return Run("read book grab policies", empty, connection =>
            {
                var result = new Dictionary<string, AbridgedAudiobookPolicy>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in connection.Query<Row>("SELECT WorkKey, AbridgedPolicy FROM BookGrabPolicies"))
                {
                    if (!string.IsNullOrWhiteSpace(row.WorkKey) && TryParse(row.AbridgedPolicy, out var policy))
                    {
                        result[row.WorkKey.Trim()] = policy;
                    }
                }

                return result;
            });
        }

        public BookGrabPolicyOverride Get(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return null;
            }

            return Run("read a book grab policy", (BookGrabPolicyOverride)null, connection =>
            {
                var row = connection.QueryFirstOrDefault<Row>("SELECT * FROM BookGrabPolicies WHERE WorkKey = @WorkKey", new { WorkKey = workKey.Trim() });
                if (row == null || !TryParse(row.AbridgedPolicy, out var policy))
                {
                    return null;
                }

                return new BookGrabPolicyOverride
                {
                    WorkKey = row.WorkKey,
                    AbridgedPolicy = policy,
                    BookIdHint = row.BookIdHint.HasValue ? (int?)row.BookIdHint.Value : null,
                    ForeignEditionId = row.ForeignEditionId,
                    Title = row.Title,
                    UpdatedAt = TalearrRecordingsStore.ParseDate(row.UpdatedAt)
                };
            });
        }

        public bool Upsert(BookGrabPolicyOverride value)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.WorkKey))
            {
                return false;
            }

            return Run("write a book grab policy", false, connection => connection.Execute(@"
INSERT INTO BookGrabPolicies (WorkKey, AbridgedPolicy, BookIdHint, ForeignEditionId, Title, UpdatedAt)
VALUES (@WorkKey, @AbridgedPolicy, @BookIdHint, @ForeignEditionId, @Title, @UpdatedAt)
ON CONFLICT (WorkKey) DO UPDATE SET
    AbridgedPolicy = excluded.AbridgedPolicy, BookIdHint = excluded.BookIdHint,
    ForeignEditionId = excluded.ForeignEditionId, Title = excluded.Title, UpdatedAt = excluded.UpdatedAt;", new
            {
                WorkKey = value.WorkKey.Trim(),
                AbridgedPolicy = Format(value.AbridgedPolicy),
                value.BookIdHint,
                value.ForeignEditionId,
                value.Title,
                UpdatedAt = TalearrRecordingsStore.FormatDate(value.UpdatedAt == default ? DateTime.UtcNow : value.UpdatedAt)
            }) > 0);
        }

        public bool Delete(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return false;
            }

            return Run("clear a book grab policy", false, connection =>
            {
                connection.Execute("DELETE FROM BookGrabPolicies WHERE WorkKey = @WorkKey", new { WorkKey = workKey.Trim() });
                return true;
            });
        }

        // Stored the way the global setting is stored (talearr.grabpolicy.abridged): lower case.
        public static string Format(AbridgedAudiobookPolicy policy)
        {
            return policy.ToString().ToLowerInvariant();
        }

        public static bool TryParse(string value, out AbridgedAudiobookPolicy policy)
        {
            policy = AbridgedAudiobookPolicy.UnabridgedOnly;
            if (string.IsNullOrWhiteSpace(value) || value.Trim().All(char.IsDigit))
            {
                return false;
            }

            return Enum.TryParse(value.Trim(), true, out policy) && Enum.IsDefined(typeof(AbridgedAudiobookPolicy), policy);
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; books follow the global grab policy", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public string WorkKey { get; set; }
            public string AbridgedPolicy { get; set; }
            public long? BookIdHint { get; set; }
            public string ForeignEditionId { get; set; }
            public string Title { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
