using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Explain
{
    public static class TalearrDecisionStages
    {
        public const string Metadata = "metadata";
        public const string Release = "release";
        public const string Import = "import";
    }

    public static class TalearrDecisionOutcomes
    {
        public const string Matched = "matched";
        public const string Grabbed = "grabbed";
        public const string Waiting = "waiting";
        public const string Skipped = "skipped";
        public const string Imported = "imported";
        public const string Refused = "refused";
        public const string Failed = "failed";
    }

    // One step of a book's story: what Talearr decided, a stable code for it, and the plain
    // reasons. Details are short sentences, never logs or settings.
    public class TalearrDecisionEntry
    {
        public long Id { get; set; }
        public int BookId { get; set; }
        public string Stage { get; set; }
        public string Outcome { get; set; }
        public string Code { get; set; }
        public string Summary { get; set; }
        public List<string> Details { get; set; } = new List<string>();
        public string ReleaseTitle { get; set; }
        public string DownloadId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public interface ITalearrDecisionStore
    {
        void Add(TalearrDecisionEntry entry);
        List<TalearrDecisionEntry> GetForBook(int bookId);
        TalearrDecisionEntry GetLatest(int bookId);
    }

    // Talearr: decision explanations in talearr.db, newest first, at most KeepPerBook per book.
    // Best effort: a broken talearr.db only means the "Why" tab is empty.
    public class TalearrDecisionStore : ITalearrDecisionStore
    {
        public const int KeepPerBook = 30;
        public const int MaxDetails = 25;
        public const int MaxTextLength = 400;

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrDecisionStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public void Add(TalearrDecisionEntry entry)
        {
            if (entry == null || entry.BookId <= 0 || string.IsNullOrWhiteSpace(entry.Summary))
            {
                return;
            }

            var details = (entry.Details ?? new List<string>())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(Trim)
                .Take(MaxDetails)
                .ToList();

            Run("record a decision", false, connection =>
            {
                connection.Execute(@"
INSERT INTO BookDecisions (BookId, Stage, Outcome, Code, Summary, Details, ReleaseTitle, DownloadId, CreatedAt)
VALUES (@BookId, @Stage, @Outcome, @Code, @Summary, @Details, @ReleaseTitle, @DownloadId, @CreatedAt);", new
                {
                    entry.BookId,
                    entry.Stage,
                    entry.Outcome,
                    entry.Code,
                    Summary = Trim(entry.Summary),
                    Details = JsonSerializer.Serialize(details),
                    ReleaseTitle = Trim(entry.ReleaseTitle),
                    entry.DownloadId,
                    CreatedAt = TalearrRecordingsStore.FormatDate(entry.CreatedAt == default ? DateTime.UtcNow : entry.CreatedAt)
                });

                connection.Execute(@"
DELETE FROM BookDecisions
WHERE BookId = @BookId AND Id NOT IN (
    SELECT Id FROM BookDecisions WHERE BookId = @BookId ORDER BY CreatedAt DESC, Id DESC LIMIT @Keep);", new
                {
                    entry.BookId,
                    Keep = KeepPerBook
                });

                return true;
            });
        }

        public List<TalearrDecisionEntry> GetForBook(int bookId)
        {
            return Run("read decisions", new List<TalearrDecisionEntry>(), connection =>
                connection.Query<Row>(@"
SELECT Id, BookId, Stage, Outcome, Code, Summary, Details, ReleaseTitle, DownloadId, CreatedAt
FROM BookDecisions WHERE BookId = @BookId ORDER BY CreatedAt DESC, Id DESC LIMIT @Keep;", new { BookId = bookId, Keep = KeepPerBook })
                    .Select(ToEntry)
                    .ToList());
        }

        public TalearrDecisionEntry GetLatest(int bookId)
        {
            return GetForBook(bookId).FirstOrDefault();
        }

        private static string Trim(string value)
        {
            if (value == null)
            {
                return null;
            }

            value = value.Trim();
            return value.Length <= MaxTextLength ? value : value.Substring(0, MaxTextLength - 3) + "...";
        }

        private static TalearrDecisionEntry ToEntry(Row row)
        {
            List<string> details;
            try
            {
                details = string.IsNullOrEmpty(row.Details) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(row.Details) ?? new List<string>();
            }
            catch (JsonException)
            {
                details = new List<string>();
            }

            return new TalearrDecisionEntry
            {
                Id = row.Id,
                BookId = (int)row.BookId,
                Stage = row.Stage,
                Outcome = row.Outcome,
                Code = row.Code,
                Summary = row.Summary,
                Details = details,
                ReleaseTitle = row.ReleaseTitle,
                DownloadId = row.DownloadId,
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public long Id { get; set; }
            public long BookId { get; set; }
            public string Stage { get; set; }
            public string Outcome { get; set; }
            public string Code { get; set; }
            public string Summary { get; set; }
            public string Details { get; set; }
            public string ReleaseTitle { get; set; }
            public string DownloadId { get; set; }
            public string CreatedAt { get; set; }
        }
    }
}
