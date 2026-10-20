using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    // Talearr: seeding details for GET /api/v1/talearr/seeding and the seeding risk health check.
    // Reads Talearr's categories including the post-import ones (the queue only reads the
    // download categories, so imported torrents never show there). Read-only.
    public partial class QBittorrent : ITalearrSeedingSource
    {
        public List<TalearrSeedingClientTorrent> GetSeedingTorrents()
        {
            var proxy = Proxy;
            var config = proxy.GetConfig(Settings);
            var categories = new[]
                {
                    Settings.AudiobookCategory,
                    Settings.EbookCategory,
                    Settings.AudiobookImportedCategory,
                    Settings.EbookImportedCategory
                }
                .Where(c => c.IsNotNullOrWhiteSpace())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();

            List<QBittorrentTorrent> torrents;
            var requireHistoryMatch = categories.Count == 0;

            if (categories.Any())
            {
                torrents = categories
                    .SelectMany(c => proxy.GetTorrents(Settings, c))
                    .GroupBy(t => t.Hash, StringComparer.InvariantCultureIgnoreCase)
                    .Select(g => g.First())
                    .ToList();
            }
            else
            {
                torrents = proxy.GetTorrents(Settings);
            }

            return torrents
                .Where(t => t.Hash.IsNotNullOrWhiteSpace())
                .Select(t => ToSeedingTorrent(t, config, requireHistoryMatch))
                .ToList();
        }

        public string GetTrackerError(string downloadId)
        {
            if (downloadId.IsNullOrWhiteSpace() || Proxy is not QBittorrentProxyV2 proxyV2)
            {
                return null;
            }

            var trackers = proxyV2.GetTorrentTrackers(downloadId.ToLowerInvariant(), Settings) ?? new List<QBittorrentTracker>();

            return TalearrSeedingRules.ReadTrackerError(trackers.Select(t => (t.Url, t.Status, t.Msg)));
        }

        internal static TalearrSeedingClientTorrent ToSeedingTorrent(QBittorrentTorrent torrent, QBittorrentPreferences config, bool requireHistoryMatch)
        {
            string message = null;
            if (torrent.State == "error")
            {
                message = "qBittorrent is reporting an error";
            }
            else if (torrent.State == "missingFiles")
            {
                message = "the download is missing files";
            }

            return new TalearrSeedingClientTorrent
            {
                DownloadId = torrent.Hash.ToUpperInvariant(),
                Title = torrent.Name,
                Category = torrent.Category.IsNotNullOrWhiteSpace() ? torrent.Category : torrent.Label,
                State = TalearrSeedingRules.NormalizeQBittorrentState(torrent.State),
                ClientMessage = message,
                Size = torrent.Size,
                Uploaded = torrent.Uploaded,
                Ratio = torrent.Ratio,
                SeedingSeconds = torrent.SeedingTime,
                SeedLimitSeconds = GetSeedLimitSeconds(torrent, config),
                AddedAt = TalearrSeedingRules.FromUnixSeconds(torrent.AddedOn),
                CompletedAt = TalearrSeedingRules.FromUnixSeconds(torrent.CompletionOn),
                IsPrivate = torrent.IsPrivate,
                IsMamTracker = IsMamTracker(torrent.Tracker),
                RequireHistoryMatch = requireHistoryMatch
            };
        }

        // Effective limit: max_seeding_time when the build reports it, else the per-torrent limit
        // (-2 = global, -1 = unlimited), else the global limit. Minutes in, seconds out.
        internal static long? GetSeedLimitSeconds(QBittorrentTorrent torrent, QBittorrentPreferences config)
        {
            if (torrent.MaxSeedingTime.HasValue)
            {
                return torrent.MaxSeedingTime.Value >= 0 ? torrent.MaxSeedingTime.Value * 60 : null;
            }

            if (torrent.SeedingTimeLimit >= 0)
            {
                return torrent.SeedingTimeLimit * 60;
            }

            if (torrent.SeedingTimeLimit == -2 && config is { MaxSeedingTimeEnabled: true } && config.MaxSeedingTime >= 0)
            {
                return config.MaxSeedingTime * 60;
            }

            return null;
        }

        internal static bool IsMamTracker(string tracker)
        {
            if (tracker.IsNullOrWhiteSpace())
            {
                return false;
            }

            if (Uri.TryCreate(tracker, UriKind.Absolute, out var uri))
            {
                return uri.Host.Equals("myanonamouse.net", StringComparison.OrdinalIgnoreCase) ||
                       uri.Host.EndsWith(".myanonamouse.net", StringComparison.OrdinalIgnoreCase);
            }

            return tracker.IndexOf("myanonamouse.net", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
