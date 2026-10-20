using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public sealed class TalearrRetryDecision
    {
        public bool Allowed { get; set; }
        public int Attempt { get; set; }
        public int MaxAttempts { get; set; }
        public CommandTrigger Trigger { get; set; }
        public string Note { get; set; }
    }

    public interface ITalearrRetryService
    {
        TalearrRetryDecision Evaluate(DownloadFailedEvent message);
        void Annotate(DownloadFailedEvent message, string note);
    }

    // Talearr: caps automatic re-searching after failed downloads and records each step on the
    // DownloadFailed history row (Data["Message"]), so the history page shows what happened
    // without new history event types (which Chaptarr would not understand after a rollback).
    public class TalearrRetryService : ITalearrRetryService
    {
        public const string NotePrefix = "Talearr:";

        private readonly IHistoryService _historyService;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public TalearrRetryService(IHistoryService historyService, ITalearrGrabPolicyService grabPolicyService, Logger logger, ITalearrBookGrabPolicyService bookGrabPolicyService = null, IBookService bookService = null)
        {
            _historyService = historyService;
            _grabPolicyService = grabPolicyService;
            _bookGrabPolicyService = bookGrabPolicyService;
            _bookService = bookService;
            _logger = logger;
        }

        public TalearrRetryDecision Evaluate(DownloadFailedEvent message)
        {
            var policy = TalearrGrabPolicyService.Resolve(_grabPolicyService);
            var maxAttempts = policy.EffectiveMaxAutomaticRetries;
            var attempt = CountRecentFailures(message);

            var decision = new TalearrRetryDecision
            {
                Attempt = attempt,
                MaxAttempts = maxAttempts,
                Allowed = attempt <= maxAttempts,
                Trigger = IsUserGrab(message.ReleaseSource) ? CommandTrigger.Manual : CommandTrigger.Unspecified
            };

            decision.Note = decision.Allowed
                ? string.Format("release blocklisted, searching all indexers for a replacement (attempt {0} of {1})", attempt, maxAttempts)
                : string.Format("release blocklisted, automatic retry limit reached ({0} failed downloads in {1} days, limit {2}), not searching again", attempt, TalearrGrabPolicy.RetryWindowDays, maxAttempts);

            // The replacement search uses the book's own abridged policy; say so when it has one.
            var bookPolicy = DescribeBookPolicy(message);
            if (decision.Allowed && bookPolicy != null)
            {
                decision.Note += "; " + bookPolicy;
            }

            return decision;
        }

        public void Annotate(DownloadFailedEvent message, string note)
        {
            if (message == null || note.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                var rows = FindFailureRows(message);
                if (rows.Count == 0)
                {
                    _logger.Debug("No DownloadFailed history row found for {0} to record: {1}", message.SourceTitle, note);
                    return;
                }

                var text = NotePrefix + " " + note;
                foreach (var row in rows)
                {
                    var key = row.Data.Keys.FirstOrDefault(k => string.Equals(k, "Message", StringComparison.OrdinalIgnoreCase)) ?? "Message";
                    var existing = row.Data.GetValueOrDefault(key);

                    if (existing != null && existing.Contains(NotePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    row.Data[key] = existing.IsNullOrWhiteSpace() ? text : existing.TrimEnd() + " - " + text;
                }

                _historyService.UpdateMany(rows);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to record Talearr retry note for {0}", message.SourceTitle);
            }

            _logger.Info("{0}: {1} {2}", message.SourceTitle, NotePrefix, note);
        }

        private string DescribeBookPolicy(DownloadFailedEvent message)
        {
            if (_bookGrabPolicyService == null || _bookService == null)
            {
                return null;
            }

            foreach (var bookId in message?.BookIds?.Where(id => id > 0).Distinct() ?? Enumerable.Empty<int>())
            {
                try
                {
                    var value = _bookGrabPolicyService.GetOverride(_bookService.GetBook(bookId));
                    if (value.HasValue)
                    {
                        return "abridged policy for this book: " + TalearrAbridgedRules.Describe(value.Value);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to read the abridged policy of book {0}", bookId);
                }
            }

            return null;
        }

        private int CountRecentFailures(DownloadFailedEvent message)
        {
            var since = DateTime.UtcNow.AddDays(-TalearrGrabPolicy.RetryWindowDays);
            var bookIds = message?.BookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();

            if (bookIds.Count == 0)
            {
                return 1;
            }

            var counts = bookIds.Select(bookId =>
            {
                try
                {
                    return _historyService.GetByBook(bookId, EntityHistoryEventType.DownloadFailed)
                        .Where(h => h.Date >= since)
                        .Select(h => h.DownloadId.IsNullOrWhiteSpace()
                            ? h.SourceTitle + "|" + h.Date.ToString("yyyyMMddHHmm")
                            : h.DownloadId.ToUpperInvariant())
                        .Distinct()
                        .Count();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to count failed downloads for book {0}", bookId);
                    return 0;
                }
            });

            // The failure being handled is already in history; never report less than one.
            return Math.Max(1, counts.Max());
        }

        private List<EntityHistory> FindFailureRows(DownloadFailedEvent message)
        {
            var recent = DateTime.UtcNow.AddMinutes(-30);

            if (message.DownloadId.IsNotNullOrWhiteSpace())
            {
                var byDownload = _historyService.FindByDownloadId(message.DownloadId)
                    .Where(h => h.EventType == EntityHistoryEventType.DownloadFailed && h.Date >= recent)
                    .GroupBy(h => h.BookId)
                    .Select(g => g.OrderByDescending(h => h.Date).First())
                    .ToList();

                if (byDownload.Any())
                {
                    return byDownload;
                }
            }

            return (message.BookIds ?? new List<int>())
                .Distinct()
                .Select(bookId => _historyService.GetByBook(bookId, EntityHistoryEventType.DownloadFailed)
                    .Where(h => h.Date >= recent && string.Equals(h.SourceTitle, message.SourceTitle, StringComparison.Ordinal))
                    .OrderByDescending(h => h.Date)
                    .FirstOrDefault())
                .Where(h => h != null)
                .ToList();
        }

        private static bool IsUserGrab(ReleaseSourceType releaseSource)
        {
            return releaseSource == ReleaseSourceType.InteractiveSearch ||
                   releaseSource == ReleaseSourceType.UserInvokedSearch;
        }
    }
}
