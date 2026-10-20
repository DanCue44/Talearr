using System.Collections.Generic;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    // Talearr: tracker status for the seeding report. Not on IQBittorrentProxy so the v1 proxy
    // (qBittorrent before 4.1) and test doubles stay untouched.
    public partial class QBittorrentProxyV2
    {
        public List<QBittorrentTracker> GetTorrentTrackers(string hash, QBittorrentSettings settings)
        {
            var request = BuildRequest(settings).Resource("/api/v2/torrents/trackers")
                                                .AddQueryParam("hash", hash);

            return ProcessRequest<List<QBittorrentTracker>>(request, settings);
        }
    }
}
