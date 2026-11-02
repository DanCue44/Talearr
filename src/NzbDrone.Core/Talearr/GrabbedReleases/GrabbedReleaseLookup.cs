using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;

namespace NzbDrone.Core.Talearr.GrabbedReleases
{
    // Talearr: what became of a release that was grabbed for a book before, so a manual
    // search can show it instead of offering it like any other release. Grabbing it again
    // only collides with the copy already in the download client.
    public static class GrabbedReleaseStates
    {
        public const string Downloading = "downloading";
        public const string ImportPending = "importPending";
        public const string Importing = "importing";
        public const string ImportBlocked = "importBlocked";
        public const string Imported = "imported";
        public const string Failed = "failed";

        // Grabbed, but no longer in the download client's queue and never imported
        public const string Grabbed = "grabbed";

        // The states where the download is still in the client: grabbing it again collides
        public static bool IsInDownloadClient(string state)
        {
            return state == Downloading || state == ImportPending || state == Importing || state == ImportBlocked;
        }
    }

    public class GrabbedRelease
    {
        public string State { get; set; }
        public DateTime? GrabbedAt { get; set; }
        public string DownloadClient { get; set; }
        public string DownloadId { get; set; }
    }

    public class GrabbedReleaseLookup
    {
        public static readonly GrabbedReleaseLookup Empty = new GrabbedReleaseLookup(new List<Entry>());

        private readonly List<Entry> _entries;

        private GrabbedReleaseLookup(List<Entry> entries)
        {
            _entries = entries;
        }

        public bool IsEmpty => _entries.Count == 0;

        // A queue item as the lookup needs it, so it can be built without the queue service
        public class QueueEntry
        {
            public string DownloadId { get; set; }
            public TrackedDownloadState? State { get; set; }
            public string DownloadClient { get; set; }
            public DateTime? Added { get; set; }
        }

        private class Entry
        {
            public string Guid { get; set; }
            public string InfoHash { get; set; }
            public GrabbedRelease Release { get; set; }
        }

        /// <summary>
        /// Builds the lookup from a book's history (any event types; grabs are picked out) and
        /// the queue items of that book. Newest grabs come first, so a release grabbed twice
        /// reports its latest download.
        /// </summary>
        public static GrabbedReleaseLookup Build(IEnumerable<EntityHistory> history, IEnumerable<QueueEntry> queue)
        {
            var historyList = (history ?? Enumerable.Empty<EntityHistory>()).Where(h => h != null).ToList();
            var queueByDownloadId = (queue ?? Enumerable.Empty<QueueEntry>())
                .Where(q => q != null && !string.IsNullOrWhiteSpace(q.DownloadId))
                .GroupBy(q => q.DownloadId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var entries = new List<Entry>();
            var seenDownloadIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var grab in historyList
                         .Where(h => h.EventType == EntityHistoryEventType.Grabbed)
                         .OrderByDescending(h => h.Date))
            {
                var guid = GetData(grab, "Guid");
                var infoHash = GetData(grab, "TorrentInfoHash");
                var downloadId = grab.DownloadId;

                if (guid == null && infoHash == null && string.IsNullOrWhiteSpace(downloadId))
                {
                    continue;
                }

                queueByDownloadId.TryGetValue(downloadId ?? string.Empty, out var queued);

                entries.Add(new Entry
                {
                    Guid = guid,

                    // A torrent's download id is its info hash
                    InfoHash = infoHash ?? downloadId,
                    Release = new GrabbedRelease
                    {
                        State = queued != null ? StateOf(queued.State) : StateFromHistory(historyList, downloadId),
                        GrabbedAt = grab.Date,
                        DownloadClient = queued?.DownloadClient ?? GetData(grab, "DownloadClientName") ?? GetData(grab, "DownloadClient"),
                        DownloadId = downloadId
                    }
                });

                if (!string.IsNullOrWhiteSpace(downloadId))
                {
                    seenDownloadIds.Add(downloadId);
                }
            }

            // Downloads in the queue with no grab on record (added in the client by hand)
            foreach (var queued in queueByDownloadId.Values.Where(q => !seenDownloadIds.Contains(q.DownloadId)))
            {
                entries.Add(new Entry
                {
                    InfoHash = queued.DownloadId,
                    Release = new GrabbedRelease
                    {
                        State = StateOf(queued.State),
                        GrabbedAt = queued.Added,
                        DownloadClient = queued.DownloadClient,
                        DownloadId = queued.DownloadId
                    }
                });
            }

            return new GrabbedReleaseLookup(entries);
        }

        /// <summary>The latest grab of a release, by its indexer guid or its torrent info hash.</summary>
        public GrabbedRelease Find(string guid, string infoHash)
        {
            if (string.IsNullOrWhiteSpace(guid) && string.IsNullOrWhiteSpace(infoHash))
            {
                return null;
            }

            return _entries.FirstOrDefault(e =>
                (!string.IsNullOrWhiteSpace(guid) && e.Guid != null && string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(infoHash) && e.InfoHash != null && string.Equals(e.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase)))
                ?.Release;
        }

        private static string StateOf(TrackedDownloadState? state)
        {
            switch (state)
            {
                case TrackedDownloadState.ImportPending:
                    return GrabbedReleaseStates.ImportPending;
                case TrackedDownloadState.Importing:
                    return GrabbedReleaseStates.Importing;
                case TrackedDownloadState.ImportBlocked:
                    return GrabbedReleaseStates.ImportBlocked;
                case TrackedDownloadState.Imported:
                    return GrabbedReleaseStates.Imported;
                case TrackedDownloadState.DownloadFailed:
                case TrackedDownloadState.DownloadFailedPending:
                    return GrabbedReleaseStates.Failed;
                default:
                    return GrabbedReleaseStates.Downloading;
            }
        }

        private static string StateFromHistory(List<EntityHistory> history, string downloadId)
        {
            if (string.IsNullOrWhiteSpace(downloadId))
            {
                return GrabbedReleaseStates.Grabbed;
            }

            var latest = history
                .Where(h => string.Equals(h.DownloadId, downloadId, StringComparison.OrdinalIgnoreCase) &&
                            (h.EventType == EntityHistoryEventType.BookFileImported ||
                             h.EventType == EntityHistoryEventType.DownloadImported ||
                             h.EventType == EntityHistoryEventType.DownloadFailed))
                .OrderByDescending(h => h.Date)
                .FirstOrDefault();

            if (latest == null)
            {
                return GrabbedReleaseStates.Grabbed;
            }

            return latest.EventType == EntityHistoryEventType.DownloadFailed
                ? GrabbedReleaseStates.Failed
                : GrabbedReleaseStates.Imported;
        }

        private static string GetData(EntityHistory history, string key)
        {
            if (history.Data == null)
            {
                return null;
            }

            // Written as "Guid", read back as "guid": the stored JSON is camel cased
            var value = history.Data.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
    }
}
