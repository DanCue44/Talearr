using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.Explain
{
    public interface ITalearrDecisionRecorder
    {
        // Called by ProcessDownloadDecisions after a search or RSS pass has been sent.
        void RecordSearch(List<DownloadDecision> decisions, List<DownloadDecision> grabbed, List<DownloadDecision> pending);
    }

    // Talearr: writes the "Why" story for each book (Chaptarr #81). Release steps come from the
    // decision engine's own rejections and pick reasons, so the explanation and the real decision
    // can never disagree; import steps come from the import events. Never throws into the caller.
    public class TalearrDecisionRecorder : ITalearrDecisionRecorder,
                                           IHandle<BookGrabbedEvent>,
                                           IHandle<BookImportedEvent>,
                                           IHandle<BookImportIncompleteEvent>,
                                           IHandle<TrackImportFailedEvent>
    {
        private const int MaxReasonGroups = 8;
        private static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(6);
        // A path ending in a file name (folders may contain spaces), then any other path.
        private static readonly Regex FilePathPattern = new Regex(@"(?:[A-Za-z]:\\|\\\\|/)[^\r\n""'<>|:]*?[\\/]([^\\/\r\n""'<>|:]+?\.[A-Za-z0-9]{1,5})(?=$|[\s,;)""'])", RegexOptions.Compiled);
        private static readonly Regex PathPattern = new Regex(@"(?:[A-Za-z]:\\|\\\\|/)(?:[^\s""'<>|]+[\\/])+([^\s""'<>|\\/]+)", RegexOptions.Compiled);

        private readonly ITalearrDecisionStore _store;
        private readonly Logger _logger;

        // Download ids of automatic grabs, so the search entry written after the pass can carry them.
        private readonly ConcurrentDictionary<string, string> _recentGrabs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public TalearrDecisionRecorder(ITalearrDecisionStore store, Logger logger)
        {
            _store = store;
            _logger = logger;
        }

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void RecordSearch(List<DownloadDecision> decisions, List<DownloadDecision> grabbed, List<DownloadDecision> pending)
        {
            try
            {
                RecordSearchCore(decisions ?? new List<DownloadDecision>(), grabbed ?? new List<DownloadDecision>(), pending ?? new List<DownloadDecision>());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not record search decisions");
            }
        }

        public void Handle(BookGrabbedEvent message)
        {
            Safe(() =>
            {
                var remoteBook = message.Book;
                var release = remoteBook?.Release;
                if (release == null)
                {
                    return;
                }

                var source = remoteBook.ReleaseSource;
                if (source != ReleaseSourceType.InteractiveSearch && source != ReleaseSourceType.ReleasePush)
                {
                    if (message.DownloadId.IsNotNullOrWhiteSpace() && release.Title.IsNotNullOrWhiteSpace())
                    {
                        TrimRecentGrabs();
                        _recentGrabs[release.Title] = message.DownloadId;
                    }

                    return;
                }

                foreach (var book in Books(remoteBook))
                {
                    var details = new List<string> { DescribeRelease(remoteBook) };
                    if (message.DownloadClientName.IsNotNullOrWhiteSpace())
                    {
                        details.Add($"Sent to {message.DownloadClientName}.");
                    }

                    _store.Add(new TalearrDecisionEntry
                    {
                        BookId = book.Id,
                        Stage = TalearrDecisionStages.Release,
                        Outcome = TalearrDecisionOutcomes.Grabbed,
                        Code = source == ReleaseSourceType.ReleasePush ? "grabbed-pushed" : "grabbed-by-you",
                        Summary = source == ReleaseSourceType.ReleasePush
                            ? $"Grabbed \"{release.Title}\" sent from another app"
                            : $"You grabbed \"{release.Title}\" from interactive search",
                        Details = details,
                        ReleaseTitle = release.Title,
                        DownloadId = message.DownloadId,
                        CreatedAt = UtcNow()
                    });
                }
            });
        }

        public void Handle(BookImportedEvent message)
        {
            Safe(() =>
            {
                if (message.Book == null || message.Book.Id <= 0)
                {
                    return;
                }

                var files = message.ImportedBooks ?? new List<MediaFiles.BookFile>();
                var oldFiles = message.OldFiles ?? new List<MediaFiles.BookFile>();
                var releaseTitle = files.Select(f => f.SceneName).FirstOrDefault(t => t.IsNotNullOrWhiteSpace());
                var details = new List<string>();

                var quality = files.Select(f => f.Quality?.Quality?.Name).FirstOrDefault(q => q.IsNotNullOrWhiteSpace());
                if (quality != null)
                {
                    details.Add($"Quality: {quality}.");
                }

                if (oldFiles.Any())
                {
                    var oldQuality = oldFiles.Select(f => f.Quality?.Quality?.Name).FirstOrDefault(q => q.IsNotNullOrWhiteSpace());
                    details.Add(oldQuality != null
                        ? $"It replaced the older copy ({oldQuality})."
                        : "It replaced the older copy.");
                }

                details.Add(message.NewDownload ? "Imported from a finished download." : "Imported from a manual or folder import.");

                _store.Add(new TalearrDecisionEntry
                {
                    BookId = message.Book.Id,
                    Stage = TalearrDecisionStages.Import,
                    Outcome = TalearrDecisionOutcomes.Imported,
                    Code = oldFiles.Any() ? "imported-upgrade" : "imported",
                    Summary = files.Count == 1 ? "Imported 1 file" : $"Imported {files.Count} files",
                    Details = details,
                    ReleaseTitle = releaseTitle,
                    DownloadId = message.DownloadId,
                    CreatedAt = UtcNow()
                });
            });
        }

        public void Handle(BookImportIncompleteEvent message)
        {
            Safe(() =>
            {
                var trackedDownload = message.TrackedDownload;
                if (trackedDownload == null)
                {
                    return;
                }

                var details = (trackedDownload.StatusMessages ?? Array.Empty<Download.TrackedDownloads.TrackedDownloadStatusMessage>())
                    .SelectMany(m => (m.Messages ?? new List<string>()).Select(text => DescribeStatus(m.Title, text)))
                    .Where(d => d.IsNotNullOrWhiteSpace())
                    .Distinct()
                    .ToList();

                if (!details.Any())
                {
                    details.Add("The download finished but no file could be matched to this book.");
                }

                var title = trackedDownload.DownloadItem?.Title;
                foreach (var book in Books(trackedDownload.RemoteBook))
                {
                    var entry = new TalearrDecisionEntry
                    {
                        BookId = book.Id,
                        Stage = TalearrDecisionStages.Import,
                        Outcome = TalearrDecisionOutcomes.Refused,
                        Code = "import-blocked",
                        Summary = "The download finished but was not imported",
                        Details = details,
                        ReleaseTitle = title,
                        DownloadId = trackedDownload.DownloadItem?.DownloadId,
                        CreatedAt = UtcNow()
                    };

                    if (!IsRepeat(entry))
                    {
                        _store.Add(entry);
                    }
                }
            });
        }

        public void Handle(TrackImportFailedEvent message)
        {
            Safe(() =>
            {
                var book = message.BookInfo?.Book;
                if (book == null || book.Id <= 0)
                {
                    return;
                }

                var reason = Redact(message.Exception?.Message);
                var entry = new TalearrDecisionEntry
                {
                    BookId = book.Id,
                    Stage = TalearrDecisionStages.Import,
                    Outcome = TalearrDecisionOutcomes.Failed,
                    Code = "import-error",
                    Summary = "A file could not be imported",
                    Details = new List<string>
                    {
                        $"File: {Redact(message.BookInfo.Path)}",
                        reason.IsNotNullOrWhiteSpace() ? $"Error: {reason}" : "The import stopped with an error."
                    },
                    DownloadId = message.DownloadId,
                    CreatedAt = UtcNow()
                };

                if (!IsRepeat(entry))
                {
                    _store.Add(entry);
                }
            });
        }

        private void RecordSearchCore(List<DownloadDecision> decisions, List<DownloadDecision> grabbed, List<DownloadDecision> pending)
        {
            if (!decisions.Any())
            {
                return;
            }

            // RSS passes run every few minutes for every release; only grabs are worth a line there.
            var automaticSearch = decisions.Any(d => d.RemoteBook?.ReleaseSource == ReleaseSourceType.Search ||
                                                     d.RemoteBook?.ReleaseSource == ReleaseSourceType.UserInvokedSearch);

            var books = decisions.SelectMany(d => Books(d.RemoteBook))
                .GroupBy(b => b.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var book in books)
            {
                var forBook = decisions.Where(d => Books(d.RemoteBook).Any(b => b.Id == book.Id)).ToList();
                var winner = grabbed.FirstOrDefault(d => Books(d.RemoteBook).Any(b => b.Id == book.Id));
                var waiting = pending.FirstOrDefault(d => Books(d.RemoteBook).Any(b => b.Id == book.Id));

                TalearrDecisionEntry entry;
                if (winner != null)
                {
                    entry = DescribeGrab(book.Id, winner, forBook, automaticSearch);
                }
                else if (!automaticSearch)
                {
                    continue;
                }
                else if (waiting != null)
                {
                    entry = DescribeWaiting(book.Id, waiting, forBook);
                }
                else
                {
                    entry = DescribeNothingGrabbed(book.Id, forBook);
                }

                _store.Add(entry);
            }
        }

        private TalearrDecisionEntry DescribeGrab(int bookId, DownloadDecision winner, List<DownloadDecision> forBook, bool automaticSearch)
        {
            var title = winner.RemoteBook.Release.Title;
            var details = new List<string> { DescribeRelease(winner.RemoteBook) };

            var pickReasons = (winner.AutoPickReasons ?? new List<DownloadDecisionReason>())
                .Select(r => StripPrefix(r.ToString()))
                .Where(r => r.IsNotNullOrWhiteSpace())
                .ToList();

            if (pickReasons.Any())
            {
                details.Add("Why this one: " + string.Join("; ", pickReasons) + ".");
            }

            var otherAccepted = forBook.Count(d => d.Approved && d != winner);
            if (otherAccepted > 0)
            {
                details.Add(otherAccepted == 1
                    ? "1 other release was also acceptable."
                    : $"{otherAccepted} other releases were also acceptable.");
            }

            details.AddRange(DescribeRejections(forBook.Where(d => !d.Approved && d != winner)));

            _recentGrabs.TryRemove(title, out var downloadId);

            return new TalearrDecisionEntry
            {
                BookId = bookId,
                Stage = TalearrDecisionStages.Release,
                Outcome = TalearrDecisionOutcomes.Grabbed,
                Code = automaticSearch ? "grabbed-search" : "grabbed-rss",
                Summary = automaticSearch
                    ? $"Search grabbed \"{title}\""
                    : $"Grabbed \"{title}\" from an indexer feed",
                Details = details,
                ReleaseTitle = title,
                DownloadId = downloadId,
                CreatedAt = UtcNow()
            };
        }

        private TalearrDecisionEntry DescribeWaiting(int bookId, DownloadDecision waiting, List<DownloadDecision> forBook)
        {
            var title = waiting.RemoteBook.Release.Title;
            var details = new List<string> { DescribeRelease(waiting.RemoteBook) };
            details.AddRange(waiting.Rejections.Select(r => StripPrefix(r.Reason)).Where(r => r.IsNotNullOrWhiteSpace()).Distinct());
            details.AddRange(DescribeRejections(forBook.Where(d => d.Rejected)));

            return new TalearrDecisionEntry
            {
                BookId = bookId,
                Stage = TalearrDecisionStages.Release,
                Outcome = TalearrDecisionOutcomes.Waiting,
                Code = "waiting-delay",
                Summary = $"Found \"{title}\" and is waiting before grabbing it",
                Details = details,
                ReleaseTitle = title,
                CreatedAt = UtcNow()
            };
        }

        private TalearrDecisionEntry DescribeNothingGrabbed(int bookId, List<DownloadDecision> forBook)
        {
            var approved = forBook.Where(d => d.Approved).ToList();
            var details = new List<string>();
            string code;

            if (approved.Any())
            {
                code = "not-sent";
                details.Add(approved.Count == 1
                    ? "1 release was acceptable but could not be sent to a download client."
                    : $"{approved.Count} releases were acceptable but none could be sent to a download client.");
            }
            else
            {
                // The most common reason gives the stable code, e.g. "rejected:quality-allowed-by-profile".
                var topSource = forBook.Where(d => !d.Approved)
                    .Select(d => MainRejection(d)?.Source)
                    .Where(s => s.IsNotNullOrWhiteSpace())
                    .GroupBy(s => s)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                code = topSource != null ? "rejected:" + CodeFromSource(topSource) : "no-acceptable-release";
            }

            details.AddRange(DescribeRejections(forBook.Where(d => !d.Approved)));

            return new TalearrDecisionEntry
            {
                BookId = bookId,
                Stage = TalearrDecisionStages.Release,
                Outcome = TalearrDecisionOutcomes.Skipped,
                Code = code,
                Summary = forBook.Count == 1
                    ? "Search found 1 release but grabbed nothing"
                    : $"Search found {forBook.Count} releases but grabbed nothing",
                Details = details,
                CreatedAt = UtcNow()
            };
        }

        // Rejections grouped by their main reason, most common first.
        internal static List<string> DescribeRejections(IEnumerable<DownloadDecision> rejected)
        {
            var groups = rejected
                .Where(d => d?.RemoteBook != null)
                .Select(d => new { Decision = d, Rejection = MainRejection(d) })
                .Where(x => x.Rejection != null)
                .GroupBy(x => StripPrefix(x.Rejection.Reason), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var lines = groups
                .Take(MaxReasonGroups)
                .Select(g =>
                {
                    var example = g.First().Decision.RemoteBook.Release?.Title;
                    var count = g.Count();
                    var label = TalearrRejectionLabels.GetLabel(g.First().Rejection);
                    var prefix = count == 1 ? $"1 release skipped ({label})" : $"{count} releases skipped ({label})";
                    return example.IsNotNullOrWhiteSpace() && count == 1
                        ? $"{prefix}: {g.Key} (\"{example}\")."
                        : $"{prefix}: {g.Key}.";
                })
                .ToList();

            var remaining = groups.Skip(MaxReasonGroups).Sum(g => g.Count());
            if (remaining > 0)
            {
                lines.Add($"{remaining} more releases skipped for other reasons.");
            }

            return lines;
        }

        internal static string CodeFromSource(string source)
        {
            if (source.IsNullOrWhiteSpace())
            {
                return "other";
            }

            var name = source;
            foreach (var suffix in new[] { "Specification", "Spec" })
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }

            if (name.StartsWith("Talearr", StringComparison.Ordinal) && name.Length > 7)
            {
                name = name.Substring(7);
            }

            return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "-").ToLowerInvariant();
        }

        // Keeps the file name and drops the folders, so explanations never show library paths.
        internal static string Redact(string text)
        {
            if (text.IsNullOrWhiteSpace())
            {
                return text;
            }

            text = FilePathPattern.Replace(text, m => m.Groups[1].Value);
            return PathPattern.Replace(text, m => m.Groups[1].Value);
        }

        private static Rejection MainRejection(DownloadDecision decision)
        {
            if (decision?.Rejections == null)
            {
                return null;
            }

            return decision.Rejections.FirstOrDefault(r => r.Type == RejectionType.Permanent) ?? decision.Rejections.FirstOrDefault();
        }

        private static string DescribeRelease(RemoteBook remoteBook)
        {
            var release = remoteBook.Release;
            var parts = new List<string>();

            var quality = remoteBook.ParsedBookInfo?.Quality?.Quality?.Name;
            if (quality.IsNotNullOrWhiteSpace())
            {
                parts.Add(quality);
            }

            if (release.Size > 0)
            {
                parts.Add(release.Size.SizeSuffix());
            }

            var text = parts.Any() ? string.Join(", ", parts) : "Release";
            return release.Indexer.IsNotNullOrWhiteSpace() ? $"{text} from {release.Indexer}." : text + ".";
        }

        private static string DescribeStatus(string title, string message)
        {
            var text = Redact(StripPrefix(message));
            if (text.IsNullOrWhiteSpace())
            {
                return null;
            }

            var file = Redact(title);
            return file.IsNotNullOrWhiteSpace() && !string.Equals(file, text, StringComparison.OrdinalIgnoreCase)
                ? $"{file}: {text}"
                : text;
        }

        private static string StripPrefix(string reason)
        {
            const string prefix = "Talearr: ";
            if (reason == null)
            {
                return null;
            }

            reason = reason.Trim();
            return reason.StartsWith(prefix, StringComparison.Ordinal) ? reason.Substring(prefix.Length) : reason;
        }

        private static IEnumerable<Books.Book> Books(RemoteBook remoteBook)
        {
            return (remoteBook?.Books ?? new List<Books.Book>()).Where(b => b != null && b.Id > 0);
        }

        private bool IsRepeat(TalearrDecisionEntry entry)
        {
            var latest = _store.GetForBook(entry.BookId)
                .FirstOrDefault(e => e.Stage == entry.Stage && e.Code == entry.Code && e.DownloadId == entry.DownloadId);

            return latest != null &&
                   entry.CreatedAt - latest.CreatedAt < RepeatWindow &&
                   latest.Details.SequenceEqual(entry.Details.Take(TalearrDecisionStore.MaxDetails));
        }

        private void TrimRecentGrabs()
        {
            if (_recentGrabs.Count > 200)
            {
                _recentGrabs.Clear();
            }
        }

        private void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not record a decision explanation");
            }
        }
    }
}
