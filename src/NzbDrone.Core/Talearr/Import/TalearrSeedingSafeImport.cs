using System;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.Talearr.Import
{
    // TaleArr: torrent imports never move the client's files. Upstream moves a torrent once it has
    // reached its seed goal (CanMoveFiles), and honours an explicit Move from manual import even
    // while the torrent is still seeding; both pull the data out from under the client, and the
    // first also breaks torrents kept in a post-import category. With this on, a torrent import is
    // always a copy, which is a hardlink when "Use Hardlinks instead of Copy" is on (falling back
    // to a full copy across filesystems). The client still removes finished torrents and their
    // data the usual way, and the library keeps its own link. Usenet is unaffected.
    public static class TalearrSeedingSafeImport
    {
        public static ImportMode Apply(ImportMode mode, DownloadClientItem downloadClientItem, bool enabled, out string reason)
        {
            reason = null;

            if (!enabled || mode != ImportMode.Move || !IsClientManagedTorrent(downloadClientItem))
            {
                return mode;
            }

            reason = downloadClientItem.DownloadClientInfo.HasPostImportCategory
                ? "the download client keeps imported torrents in a post-import category"
                : "torrent imports never move the client's files (seeding-safe imports)";

            return ImportMode.Copy;
        }

        // A torrent the client seeds from its own folder. A torrent blackhole only hands files over,
        // so its "read only" setting already decides whether moving is allowed.
        public static bool IsClientManagedTorrent(DownloadClientItem downloadClientItem)
        {
            var info = downloadClientItem?.DownloadClientInfo;

            return info != null &&
                   info.Protocol == DownloadProtocol.Torrent &&
                   (info.Type == null || info.Type.IndexOf("Blackhole", StringComparison.OrdinalIgnoreCase) < 0);
        }
    }
}
