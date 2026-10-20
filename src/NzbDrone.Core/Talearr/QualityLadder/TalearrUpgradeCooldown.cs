using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    public sealed class TalearrUpgradeCooldownEntry
    {
        public int BookId { get; set; }
        public DateTime Until { get; set; }
        public string Reason { get; set; }
        public string DownloadId { get; set; }
        public string ReleaseTitle { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public interface ITalearrUpgradeCooldown
    {
        // Called by the upgrade guard when it rejects an upgrade download at import.
        void RecordRejectedUpgrade(Book book, string reason, DownloadClientItem downloadClientItem);

        // The active pause for a book, or null.
        TalearrUpgradeCooldownEntry GetActive(int bookId);
    }

    // Talearr: stops the upgrade loop where a grab-time size estimate looks like an upgrade, the
    // upgrade guard measures the real files at import and rejects them, and the next search grabs
    // the same (or a similar) release again. When the guard rejects an upgrade download:
    //   - the book gets a cooldown (Cooldown, 7 days) in talearr.db, during which automatic
    //     searches do not grab upgrades for it (TalearrUpgradeCooldownSpecification, a soft
    //     filter: interactive search can still grab), and
    //   - the blocked download is failed without a new search, which blocklists that release.
    // Explicit user grabs stay in the queue, the same rule TalearrImportRejectionRetryService uses.
    // Best effort: a broken talearr.db only means no cooldown, the 0.16.0 behavior.
    public class TalearrUpgradeCooldownService : ITalearrUpgradeCooldown, IHandle<BookImportIncompleteEvent>
    {
        public static readonly TimeSpan Cooldown = TimeSpan.FromDays(7);
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);

        private readonly ITalearrDatabase _database;
        private readonly IFailedDownloadService _failedDownloadService;
        private readonly IHistoryService _historyService;
        private readonly Logger _logger;
        private readonly object _cacheLock = new object();

        private Dictionary<int, TalearrUpgradeCooldownEntry> _cache;
        private DateTime _cacheLoadedAt = DateTime.MinValue;

        public TalearrUpgradeCooldownService(ITalearrDatabase database,
                                             IFailedDownloadService failedDownloadService,
                                             IHistoryService historyService,
                                             Logger logger)
        {
            _database = database;
            _failedDownloadService = failedDownloadService;
            _historyService = historyService;
            _logger = logger;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void RecordRejectedUpgrade(Book book, string reason, DownloadClientItem downloadClientItem)
        {
            if (book == null || book.Id <= 0 || downloadClientItem == null)
            {
                return;
            }

            var now = UtcNow();
            var until = now.Add(Cooldown);
            var ok = Run("record an upgrade cooldown", false, connection => connection.Execute(@"
INSERT INTO UpgradeCooldowns (BookId, Until, Reason, DownloadId, ReleaseTitle, CreatedAt)
VALUES (@BookId, @Until, @Reason, @DownloadId, @ReleaseTitle, @CreatedAt)
ON CONFLICT (BookId) DO UPDATE SET
    Until = excluded.Until, Reason = excluded.Reason, DownloadId = excluded.DownloadId,
    ReleaseTitle = excluded.ReleaseTitle, CreatedAt = excluded.CreatedAt;", new
            {
                BookId = book.Id,
                Until = TalearrRecordingsStore.FormatDate(until),
                Reason = StripPrefix(reason),
                downloadClientItem.DownloadId,
                ReleaseTitle = downloadClientItem.Title,
                CreatedAt = TalearrRecordingsStore.FormatDate(now)
            }) > 0);

            InvalidateCache();

            if (ok)
            {
                _logger.Info("Talearr: upgrades for '{0}' are paused until {1:yyyy-MM-dd HH:mm} UTC after '{2}' was rejected at import",
                    book.Title,
                    until,
                    downloadClientItem.Title);
            }
        }

        private static string StripPrefix(string reason)
        {
            const string prefix = "Talearr: ";
            return reason != null && reason.StartsWith(prefix, StringComparison.Ordinal) ? reason.Substring(prefix.Length) : reason;
        }

        public TalearrUpgradeCooldownEntry GetActive(int bookId)
        {
            if (bookId <= 0)
            {
                return null;
            }

            var now = UtcNow();
            return Load(now).TryGetValue(bookId, out var entry) && entry.Until > now ? entry : null;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(BookImportIncompleteEvent message)
        {
            var trackedDownload = message?.TrackedDownload;
            var downloadId = trackedDownload?.DownloadItem?.DownloadId;
            if (string.IsNullOrWhiteSpace(downloadId) || trackedDownload.State != TrackedDownloadState.ImportBlocked)
            {
                return;
            }

            var now = UtcNow();
            var entry = Load(now).Values.FirstOrDefault(e => e.Until > now && string.Equals(e.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return;
            }

            if (TalearrImportRejectionRetryService.IsUserGrab(trackedDownload, _historyService, _logger))
            {
                _logger.Debug("Upgrade {0} was rejected at import but it was an explicit user grab; leaving it in the queue", trackedDownload.DownloadItem.Title);
                return;
            }

            var reason = "Import rejected: " + (entry.Reason ?? "not an upgrade");
            _logger.Warn("{0} for {1}; blocklisting it (no new search, upgrades for this book are paused).", reason, trackedDownload.DownloadItem.Title);

            try
            {
                _failedDownloadService.MarkAsFailed(trackedDownload, reason, skipRedownload: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to mark {0} as failed after a rejected upgrade", trackedDownload.DownloadItem.Title);
            }
        }

        private Dictionary<int, TalearrUpgradeCooldownEntry> Load(DateTime now)
        {
            lock (_cacheLock)
            {
                if (_cache != null && now - _cacheLoadedAt < CacheLifetime && now >= _cacheLoadedAt)
                {
                    return _cache;
                }
            }

            var rows = Run("read upgrade cooldowns", new List<Row>(), connection =>
                connection.Query<Row>("SELECT * FROM UpgradeCooldowns WHERE Until > @Now", new { Now = TalearrRecordingsStore.FormatDate(now) }).ToList());

            var result = rows
                .Select(r => new TalearrUpgradeCooldownEntry
                {
                    BookId = (int)r.BookId,
                    Until = TalearrRecordingsStore.ParseDate(r.Until),
                    Reason = r.Reason,
                    DownloadId = r.DownloadId,
                    ReleaseTitle = r.ReleaseTitle,
                    CreatedAt = TalearrRecordingsStore.ParseDate(r.CreatedAt)
                })
                .GroupBy(e => e.BookId)
                .ToDictionary(g => g.Key, g => g.First());

            lock (_cacheLock)
            {
                _cache = result;
                _cacheLoadedAt = now;
            }

            return result;
        }

        private void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cache = null;
            }
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; upgrades are not paused", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public long BookId { get; set; }
            public string Until { get; set; }
            public string Reason { get; set; }
            public string DownloadId { get; set; }
            public string ReleaseTitle { get; set; }
            public string CreatedAt { get; set; }
        }
    }

    // Talearr: while a book's upgrade cooldown is active, automatic searches do not grab an upgrade
    // for it (a book with files). A soft filter, so interactive search can still grab one.
    public class TalearrUpgradeCooldownSpecification : IDecisionEngineSpecification
    {
        public const string Category = "Upgrade paused";

        private readonly ITalearrUpgradeCooldown _cooldown;

        public TalearrUpgradeCooldownSpecification(ITalearrUpgradeCooldown cooldown)
        {
            _cooldown = cooldown;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            if (_cooldown == null || subject?.Books == null || subject.Books.Count == 0)
            {
                return Decision.Accept();
            }

            foreach (var book in subject.Books.Where(b => b != null && b.Id > 0))
            {
                var entry = _cooldown.GetActive(book.Id);
                if (entry == null || !HasFiles(book))
                {
                    continue;
                }

                return Decision.RejectSoftFilter(
                    $"Talearr: upgrades for this book are paused until {entry.Until:yyyy-MM-dd} after '{entry.ReleaseTitle}' was rejected at import ({entry.Reason})",
                    Category);
            }

            return Decision.Accept();
        }

        private static bool HasFiles(Book book)
        {
            try
            {
                return book.BookFiles?.Any() == true;
            }
            catch (Exception)
            {
                // Files not loadable here: treat it as an upgrade, the cooldown was set for one.
                return true;
            }
        }
    }
}
