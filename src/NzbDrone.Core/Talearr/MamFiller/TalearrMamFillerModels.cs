using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.Talearr.MamFiller
{
    // TaleArr: System > Tasks "MAM Freeleech Filler". Scheduled every hour; the filler itself
    // only runs once its own interval (MAM indexer setting, default 6 hours) has passed, and a
    // manual run skips that wait (not the safety checks).
    public class MamFreeleechFillerCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => true;
    }

    // One MAM JSON search result as the filler sees it.
    public class TalearrMamFillerCandidate
    {
        public string TorrentId { get; set; }
        public string Title { get; set; }
        public long Size { get; set; }
        public int Seeders { get; set; }
        public int Leechers { get; set; }
        public int? MainCategory { get; set; }
        public string LanguageCode { get; set; }
        public int? LanguageId { get; set; }
        public bool Free { get; set; }
        public bool PersonalFreeleech { get; set; }
        public bool VipFreeleech { get; set; }
        public bool Vip { get; set; }

        public bool IsAudiobook => MainCategory == 13;
    }

    // A torrent the filler sent to the download client (sidecar table TalearrFillerTorrents).
    public class TalearrFillerTorrent
    {
        public const string StateAdded = "added";
        public const string StateRemoved = "removed";
        public const string StateDone = "done";

        public string TorrentId { get; set; }
        public string Title { get; set; }
        public long Size { get; set; }
        public bool IsAudiobook { get; set; }
        public DateTime AddedAt { get; set; }
        public string ClientHash { get; set; }
        public string DownloadClient { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class TalearrMamFillerPreviewItem
    {
        public string TorrentId { get; set; }
        public string Title { get; set; }
        public long Size { get; set; }
        public int Seeders { get; set; }
        public int Leechers { get; set; }
        public bool IsAudiobook { get; set; }
        public bool Grabbed { get; set; }
    }

    // The latest run, shown on Activity > Seeding (report "filler" block). Additive API.
    public class TalearrMamFillerStatus
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? LastRunAt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string LastResult { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? BackoffUntil { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? Target { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? FreeSlots { get; set; }

        public bool PreviewIsDryRun { get; set; }
        public List<TalearrMamFillerPreviewItem> Preview { get; set; } = new List<TalearrMamFillerPreviewItem>();
        public int Tracked { get; set; }
    }

    // A torrent client the filler can add to without going through TaleArr's grab path (no
    // history, no tracked download, no import). qBittorrent implements it.
    public interface ITalearrMamFillerClient
    {
        void AddFillerTorrent(string fileName, byte[] torrentFile, string hash, string category, TimeSpan? seedTime);

        // TaleArr's normal category for a media type, used when Import Into Library is on.
        string GetLibraryCategory(bool audiobook);

        List<TalearrSeedingClientTorrent> GetCategoryTorrents(string category);

        bool HasTorrent(string hash);

        void RemoveFillerTorrent(string hash);
    }
}
