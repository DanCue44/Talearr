using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.Talearr.MamFiller
{
    // Talearr: System > Tasks "MAM Freeleech Filler". Scheduled every hour; the filler itself
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

        // Talearr: taste preferences (TalearrMamFillerTaste). MAM's site category and author_info.
        public int? CategoryId { get; set; }
        public string CategoryName { get; set; }
        public List<string> Authors { get; set; } = new List<string>();

        // Set by the run: why this is a preferred pick, null for an ordinary one.
        public string PreferredReason { get; set; }

        public bool IsAudiobook => MainCategory == 13;
        public bool IsPreferred => PreferredReason != null;
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

        // Talearr: taste preferences. Additive API.
        public bool Preferred { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string PreferredReason { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Category { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Author { get; set; }

        // Library import of a preferred pick (TalearrFillerPick.LibraryState), null when none.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string LibraryState { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string LibraryMessage { get; set; }
    }

    // Talearr: a preferred filler pick that is also put into the library ("Import Preferred
    // Picks"). Kept as JSON in TalearrFillerState (key "pick:<torrent id>"), so no schema change.
    // The torrent itself stays in the filler category and keeps seeding; the files are copied or
    // hardlinked into the library once the download has finished and the book is in the library.
    public class TalearrFillerPick
    {
        public const string StateWaiting = "waiting";
        public const string StateAdding = "adding";
        public const string StateImported = "imported";
        public const string StateInLibrary = "inLibrary";
        public const string StateNoMatch = "noMatch";
        public const string StateExcluded = "excluded";
        public const string StateFailed = "failed";
        public const string StateGone = "gone";

        public string TorrentId { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public bool IsAudiobook { get; set; }
        public string PreferredReason { get; set; }
        public string LibraryState { get; set; }
        public string LibraryMessage { get; set; }
        public string BookProviderId { get; set; }
        public string AuthorProviderId { get; set; }
        public int? BookId { get; set; }
        public int Attempts { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public bool IsOpen => LibraryState == StateWaiting || LibraryState == StateAdding;
    }

    // Talearr: what the download client says about one filler torrent, for the library import.
    public class TalearrFillerDownload
    {
        public bool Completed { get; set; }

        // Local path of the torrent's content (remote path mappings applied), null when unknown.
        public string OutputPath { get; set; }
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

    // A torrent client the filler can add to without going through Talearr's grab path (no
    // history, no tracked download, no import). qBittorrent implements it.
    public interface ITalearrMamFillerClient
    {
        void AddFillerTorrent(string fileName, byte[] torrentFile, string hash, string category, TimeSpan? seedTime);

        // Talearr's normal category for a media type, used when Import Into Library is on.
        string GetLibraryCategory(bool audiobook);

        List<TalearrSeedingClientTorrent> GetCategoryTorrents(string category);

        bool HasTorrent(string hash);

        void RemoveFillerTorrent(string hash);

        // Talearr: taste preferences. Null when the client does not have the torrent.
        TalearrFillerDownload GetFillerDownload(string hash, string category);
    }

    // Talearr: puts preferred filler picks into the library (TalearrMamFillerLibraryService).
    public interface ITalearrMamFillerLibrary
    {
        // Names of the authors in the library, for "Prefer Authors in My Library".
        List<string> LibraryAuthorNames();

        // Moves one pick along (find the book, add it, import the finished download). Returns
        // true when the pick changed.
        bool Advance(TalearrFillerPick pick, TalearrFillerDownload download, DateTime now);
    }
}
