using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace NzbDrone.Core.Talearr.Seeding
{
    // TaleArr: pure rules behind the seeding report, kept apart from the client and database reads
    // so they can be tested with plain data.
    public static class TalearrSeedingRules
    {
        // MAM counts a torrent as satisfied after 72 hours of seeding within 30 days.
        public const long MamRequiredSeedingSeconds = 72 * 3600;

        public const string Seeding = "seeding";
        public const string Stalled = "stalled";
        public const string Queued = "queued";
        public const string Paused = "paused";
        public const string Checking = "checking";
        public const string Error = "error";
        public const string Downloading = "downloading";
        public const string Missing = "missing";

        public static string NormalizeQBittorrentState(string state)
        {
            switch (state)
            {
                case "uploading":
                case "stalledUP":
                case "forcedUP":
                    return Seeding;
                case "queuedUP":
                case "queuedDL":
                    return Queued;
                case "pausedUP":
                case "stoppedUP":
                case "pausedDL":
                case "stoppedDL":
                    return Paused;
                case "checkingUP":
                case "checkingDL":
                case "checkingResumeData":
                case "moving":
                    return Checking;
                case "error":
                case "missingFiles":
                    return Error;
                case "stalledDL":
                    return Stalled;
                default:
                    return Downloading;
            }
        }

        // Clients without seeding details: best effort from the queue status.
        public static string NormalizeItemStatus(DownloadItemStatus status)
        {
            switch (status)
            {
                case DownloadItemStatus.Completed:
                    return Seeding;
                case DownloadItemStatus.Paused:
                    return Paused;
                case DownloadItemStatus.Queued:
                    return Queued;
                case DownloadItemStatus.Warning:
                    return Stalled;
                case DownloadItemStatus.Failed:
                    return Error;
                default:
                    return Downloading;
            }
        }

        // qBittorrent tracker list entries: status 0 disabled, 1 not contacted, 2 working,
        // 3 updating, 4 not working (5 tracker error and 6 unreachable on newer builds).
        // DHT, PeX and LSD show up as "** [DHT] **" pseudo trackers and are ignored.
        public static string ReadTrackerError(IEnumerable<(string Url, int Status, string Message)> trackers)
        {
            var real = (trackers ?? Enumerable.Empty<(string Url, int Status, string Message)>())
                .Where(t => t.Url != null && !t.Url.StartsWith("** [", StringComparison.Ordinal))
                .ToList();

            if (real.Count == 0 || real.Any(t => t.Status == 2 || t.Status == 3 || t.Status == 1))
            {
                return null;
            }

            var failed = real.FirstOrDefault(t => t.Status >= 4);
            if (failed.Url == null)
            {
                return null;
            }

            var message = (failed.Message ?? string.Empty).Trim();
            if (message.Length > 200)
            {
                message = message.Substring(0, 200);
            }

            return message.Length > 0 ? message : "tracker not working";
        }

        public static bool IsMamIndexerName(string name)
        {
            return name != null &&
                   (name.IndexOf("myanonamouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.Equals("MAM", StringComparison.OrdinalIgnoreCase));
        }

        public static TalearrSeedingTorrent BuildRow(TalearrSeedingClientTorrent torrent,
                                                     string downloadClientName,
                                                     TalearrSeedingGrab grab,
                                                     TalearrSeedingIndexerInfo indexer)
        {
            var isMam = indexer?.IsMam == true || torrent.IsMamTracker || IsMamIndexerName(grab?.IndexerName);
            var isPrivate = torrent.IsPrivate ?? (isMam ? true : (bool?)null);
            var seedGoal = indexer?.SeedTimeSeconds ?? torrent.SeedLimitSeconds;

            long? required = null;
            if (isMam)
            {
                required = MamRequiredSeedingSeconds;
            }
            else if (isPrivate == true)
            {
                required = indexer?.SeedTimeSeconds ?? torrent.SeedLimitSeconds;
            }

            bool? met;
            if (!required.HasValue)
            {
                met = true;
            }
            else if (torrent.SeedingSeconds.HasValue)
            {
                met = torrent.SeedingSeconds.Value >= required.Value;
            }
            else
            {
                met = null;
            }

            return new TalearrSeedingTorrent
            {
                DownloadId = torrent.DownloadId,
                Title = torrent.Title,
                BookId = grab?.BookId,
                BookTitle = grab?.BookTitle,
                AuthorName = grab?.AuthorName,
                MediaType = grab?.MediaType,
                Indexer = grab?.IndexerName ?? indexer?.Name,
                IsPrivateTracker = isPrivate,
                DownloadClient = downloadClientName,
                State = torrent.State,
                Size = torrent.Size,
                Uploaded = torrent.Uploaded,
                Ratio = torrent.Ratio.HasValue ? Math.Round(torrent.Ratio.Value, 3) : null,
                SeedingSeconds = torrent.SeedingSeconds,
                RequiredSeedingSeconds = required,
                SeedGoalSeconds = seedGoal,
                RequirementMet = met,
                AddedAt = torrent.AddedAt,
                CompletedAt = torrent.CompletedAt
            };
        }

        // A grab TaleArr made from a private tracker that the client no longer has, while it
        // cannot have seeded long enough yet.
        public static TalearrSeedingTorrent BuildMissingRow(TalearrSeedingGrab grab, TalearrSeedingIndexerInfo indexer)
        {
            var required = MamRequiredSeedingSeconds;

            return new TalearrSeedingTorrent
            {
                DownloadId = grab.DownloadId,
                Title = null,
                BookId = grab.BookId,
                BookTitle = grab.BookTitle,
                AuthorName = grab.AuthorName,
                MediaType = grab.MediaType,
                Indexer = grab.IndexerName ?? indexer?.Name,
                IsPrivateTracker = true,
                DownloadClient = grab.DownloadClientName,
                State = Missing,
                RequiredSeedingSeconds = required,
                SeedGoalSeconds = indexer?.SeedTimeSeconds,
                RequirementMet = false,
                AddedAt = grab.Date
            };
        }

        // Whether the tracker status is worth one extra client call for this row.
        public static bool NeedsTrackerCheck(TalearrSeedingTorrent row)
        {
            return row.IsPrivateTracker == true &&
                   row.RequirementMet == false &&
                   (row.State == Seeding || row.State == Stalled);
        }

        public static void ApplyRisk(TalearrSeedingTorrent row, string trackerError, string clientMessage)
        {
            row.AtRisk = false;
            row.RiskReason = null;

            if (row.IsPrivateTracker != true || row.RequirementMet != false)
            {
                return;
            }

            var hours = FormatHours(row.RequiredSeedingSeconds);

            switch (row.State)
            {
                case Paused:
                    row.RiskReason = $"Paused before {hours} of seeding";
                    break;
                case Queued:
                    row.RiskReason = "Queued, not seeding";
                    break;
                case Error:
                    row.RiskReason = string.IsNullOrWhiteSpace(clientMessage)
                        ? "Download client reports an error"
                        : $"Download client error: {clientMessage.Trim()}";
                    break;
                case Missing:
                    row.RiskReason = $"Not in the download client before {hours} of seeding (removed or moved out of the TaleArr category)";
                    break;
                case Seeding:
                case Stalled:
                    if (!string.IsNullOrWhiteSpace(trackerError))
                    {
                        row.RiskReason = $"Tracker error: {trackerError.Trim()}";
                    }

                    break;
            }

            row.AtRisk = row.RiskReason != null;
        }

        public static TalearrSeedingSummary Summarize(IReadOnlyCollection<TalearrSeedingTorrent> torrents)
        {
            return new TalearrSeedingSummary
            {
                Total = torrents.Count,
                Seeding = torrents.Count(t => t.State == Seeding),
                AtRisk = torrents.Count(t => t.AtRisk),
                PrivateUnsatisfied = torrents.Count(t => t.IsPrivateTracker == true && t.RequirementMet == false)
            };
        }

        // From the persisted account status (hourly refresh); no MAM call here.
        public static TalearrSeedingMamStatus BuildMamStatus(MyAnonaMouseSettings settings)
        {
            if (settings == null)
            {
                return null;
            }

            var checkedAt = settings.UnsatisfiedStatusRefreshedUtc ?? settings.UnsatisfiedSnapshotUtc;

            return new TalearrSeedingMamStatus
            {
                UserClass = string.IsNullOrWhiteSpace(settings.UserClass) ? null : settings.UserClass,
                IsVip = settings.IsVip,
                Unsatisfied = settings.UnsatisfiedCount,
                UnsatisfiedLimit = settings.UnsatisfiedLimit,
                Wedges = settings.FreeleechWedges,
                BonusPoints = settings.BonusPoints,
                CheckedAt = checkedAt.HasValue ? DateTime.SpecifyKind(checkedAt.Value, DateTimeKind.Utc) : null,
                VipUntil = settings.VipUntilUtc.HasValue ? DateTime.SpecifyKind(settings.VipUntilUtc.Value, DateTimeKind.Utc) : null,
                Ratio = settings.Ratio,
                Restricted = MyAnonaMouseBonusSpendRules.IsRestricted(settings),
                BonusAutoSpend = MyAnonaMouseBonusSpendRules.AnyRuleEnabled(settings),
                BonusDryRun = settings.BonusDryRun
            };
        }

        public static DateTime? FromUnixSeconds(long? seconds)
        {
            // qBittorrent sends -1 (or 0 on old builds) for "never".
            if (!seconds.HasValue || seconds.Value <= 0 || seconds.Value > 253402300799)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
        }

        public static string FormatHours(long? seconds)
        {
            var hours = (seconds ?? MamRequiredSeedingSeconds) / 3600.0;
            return Math.Round(hours, 1).ToString("0.#", CultureInfo.InvariantCulture) + " h";
        }
    }
}
