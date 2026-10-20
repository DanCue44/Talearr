using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Talearr.MamFiller;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    // Talearr: the MAM freeleech seeding filler adds torrents here directly, in its own category,
    // so they never create grab history, tracked downloads or imports.
    public partial class QBittorrent : ITalearrMamFillerClient
    {
        public void AddFillerTorrent(string fileName, byte[] torrentFile, string hash, string category, TimeSpan? seedTime)
        {
            var proxy = Proxy;
            var seedConfiguration = seedTime.HasValue ? new TorrentSeedConfiguration { SeedTime = seedTime } : null;
            var addHasShareLimits = seedConfiguration != null && ProxyApiVersion >= new Version(2, 8, 1);

            EnsureCategory(proxy, category);
            proxy.AddTorrentFromFile(fileName, torrentFile, addHasShareLimits ? seedConfiguration : null, Settings, category);

            if (seedConfiguration != null && !addHasShareLimits && hash.IsNotNullOrWhiteSpace() && WaitForTorrent(hash))
            {
                try
                {
                    proxy.SetTorrentSeedingConfiguration(hash.ToLowerInvariant(), seedConfiguration, Settings);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Failed to set the seeding time for filler torrent {0}", hash);
                }
            }
        }

        public string GetLibraryCategory(bool audiobook)
        {
            return audiobook ? Settings.AudiobookCategory : Settings.EbookCategory;
        }

        public List<TalearrSeedingClientTorrent> GetCategoryTorrents(string category)
        {
            if (category.IsNullOrWhiteSpace())
            {
                return new List<TalearrSeedingClientTorrent>();
            }

            var proxy = Proxy;
            var config = proxy.GetConfig(Settings);

            return proxy.GetTorrents(Settings, category)
                .Where(t => t.Hash.IsNotNullOrWhiteSpace())
                .Select(t => ToSeedingTorrent(t, config, false))
                .ToList();
        }

        public bool HasTorrent(string hash)
        {
            return hash.IsNotNullOrWhiteSpace() && Proxy.IsTorrentLoaded(hash.ToLowerInvariant(), Settings);
        }

        public void RemoveFillerTorrent(string hash)
        {
            Proxy.RemoveTorrent(hash.ToLowerInvariant(), true, Settings);
        }

        // Talearr: filler taste preferences. Completed once qBittorrent has the whole payload and is
        // seeding (or stopped after seeding); the path is the torrent's own folder or file.
        public TalearrFillerDownload GetFillerDownload(string hash, string category)
        {
            if (hash.IsNullOrWhiteSpace())
            {
                return null;
            }

            var torrent = Proxy.GetTorrents(Settings, category.IsNullOrWhiteSpace() ? null : category)
                .FirstOrDefault(t => string.Equals(t.Hash, hash, StringComparison.OrdinalIgnoreCase));

            if (torrent == null)
            {
                return null;
            }

            var completed = torrent.Progress >= 1.0 &&
                            torrent.State is "pausedUP" or "stoppedUP" or "uploading" or "stalledUP" or "queuedUP" or "forcedUP";
            var contentPath = completed ? ResolveContentPath(torrent) : null;

            return new TalearrFillerDownload
            {
                Completed = completed && contentPath.HasValue,
                OutputPath = contentPath.HasValue ? RemapRemoteToLocal(contentPath.Value).FullPath : null
            };
        }

        private void EnsureCategory(IQBittorrentProxy proxy, string category)
        {
            if (category.IsNullOrWhiteSpace() || ProxyApiVersion < new Version(2, 0))
            {
                return;
            }

            try
            {
                var labels = proxy.GetLabels(Settings);
                if (labels == null || !labels.Keys.Contains(category, StringComparer.Ordinal))
                {
                    proxy.AddLabel(category, Settings);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not create qBittorrent category {0}; adding the torrent anyway", category);
            }
        }
    }
}
