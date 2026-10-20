using System;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Import
{
    public class TalearrHardlinkStatus
    {
        public int ConsecutiveFallbacks { get; set; }
        public int TotalFallbacks { get; set; }
        public int TotalHardlinks { get; set; }
        public string LastFallbackSource { get; set; }
        public string LastFallbackTarget { get; set; }
        public DateTime? LastFallbackUtc { get; set; }
    }

    public class TalearrHardlinkFallbackChangedEvent : IEvent
    {
    }

    public interface ITalearrHardlinkMonitor
    {
        void Record(string sourcePath, string targetPath, TransferMode requested, TransferMode actual);
        TalearrHardlinkStatus GetStatus();
    }

    // Talearr: counts imports that asked for a hardlink and got a full copy instead (different
    // filesystem, different Docker volume, different Unraid share). A few in a row means the
    // download and library folders cannot share files, so every torrent import doubles the disk
    // space; the health check turns that into a System > Status warning. In memory only: the
    // count starts again after a restart, and one successful hardlink clears the warning.
    public class TalearrHardlinkMonitor : ITalearrHardlinkMonitor
    {
        public const int WarningThreshold = 3;

        private readonly object _lock = new object();
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly TalearrHardlinkStatus _status = new TalearrHardlinkStatus();

        public TalearrHardlinkMonitor(IEventAggregator eventAggregator, Logger logger)
        {
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public void Record(string sourcePath, string targetPath, TransferMode requested, TransferMode actual)
        {
            if (!requested.HasFlag(TransferMode.HardLink) || (actual != TransferMode.HardLink && actual != TransferMode.Copy))
            {
                return;
            }

            bool warningChanged;

            lock (_lock)
            {
                var wasWarning = IsWarning(_status);

                if (actual == TransferMode.HardLink)
                {
                    _status.TotalHardlinks++;
                    _status.ConsecutiveFallbacks = 0;
                }
                else
                {
                    _status.TotalFallbacks++;
                    _status.ConsecutiveFallbacks++;
                    _status.LastFallbackSource = sourcePath;
                    _status.LastFallbackTarget = targetPath;
                    _status.LastFallbackUtc = DateTime.UtcNow;
                }

                warningChanged = wasWarning != IsWarning(_status);
            }

            if (actual == TransferMode.Copy)
            {
                _logger.Debug("Hardlink was not possible, the file was copied: {0} > {1}", sourcePath, targetPath);
            }

            if (warningChanged)
            {
                try
                {
                    _eventAggregator?.PublishEvent(new TalearrHardlinkFallbackChangedEvent());
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not publish the hardlink fallback change");
                }
            }
        }

        public TalearrHardlinkStatus GetStatus()
        {
            lock (_lock)
            {
                return new TalearrHardlinkStatus
                {
                    ConsecutiveFallbacks = _status.ConsecutiveFallbacks,
                    TotalFallbacks = _status.TotalFallbacks,
                    TotalHardlinks = _status.TotalHardlinks,
                    LastFallbackSource = _status.LastFallbackSource,
                    LastFallbackTarget = _status.LastFallbackTarget,
                    LastFallbackUtc = _status.LastFallbackUtc
                };
            }
        }

        public static bool IsWarning(TalearrHardlinkStatus status)
        {
            return status != null && status.ConsecutiveFallbacks >= WarningThreshold;
        }

        // English text on purpose: Talearr-only, kept out of the shared translation files.
        public static string GetMessage(TalearrHardlinkStatus status)
        {
            if (!IsWarning(status))
            {
                return null;
            }

            var message = $"The last {status.ConsecutiveFallbacks} imports could not hardlink and copied the files instead, " +
                          "so torrent imports use double the disk space. Last one: " +
                          $"'{status.LastFallbackSource}' to '{status.LastFallbackTarget}'. " +
                          "Hardlinks only work when the download folder and the library are on the same filesystem " +
                          "and reached through the same container volume (map one parent folder, for example /data, " +
                          "that holds both).";

            var sourceShare = GetUnraidShare(status.LastFallbackSource);
            var targetShare = GetUnraidShare(status.LastFallbackTarget);
            if (sourceShare != null && targetShare != null && !sourceShare.Equals(targetShare, StringComparison.Ordinal))
            {
                message += $" On Unraid, /mnt/user only hardlinks within one share; '{sourceShare}' and '{targetShare}' are different shares.";
            }

            return message;
        }

        // "/mnt/user/media/books/x.epub" -> "media"; null when the path is not on an Unraid user share.
        public static string GetUnraidShare(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var normalized = path.Replace('\\', '/');
            foreach (var prefix in new[] { "/mnt/user/", "/mnt/user0/" })
            {
                if (normalized.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var rest = normalized.Substring(prefix.Length);
                    var slash = rest.IndexOf('/');
                    var share = slash < 0 ? rest : rest.Substring(0, slash);
                    return share.Length == 0 ? null : share;
                }
            }

            return null;
        }
    }
}
