using Newtonsoft.Json;

namespace NzbDrone.Core.Download.Clients.QBittorrent
{
    // TaleArr: extra /torrents/info fields for the seeding report (all optional; older builds
    // leave them out).
    public partial class QBittorrentTorrent
    {
        [JsonProperty(PropertyName = "uploaded")]
        public long? Uploaded { get; set; } // Bytes uploaded in total

        [JsonProperty(PropertyName = "added_on")]
        public long? AddedOn { get; set; } // Unix seconds

        [JsonProperty(PropertyName = "completion_on")]
        public long? CompletionOn { get; set; } // Unix seconds, -1 while incomplete

        [JsonProperty(PropertyName = "max_seeding_time")] // Effective seeding time limit in minutes, -1 = unlimited (4.6+)
        public long? MaxSeedingTime { get; set; }

        [JsonProperty(PropertyName = "private")]
        public bool? IsPrivate { get; set; } // Private torrent flag (5.0+)

        [JsonProperty(PropertyName = "tracker")]
        public string Tracker { get; set; } // Current working tracker URL (holds the passkey; never expose it)
    }

    // TaleArr: one entry of /torrents/trackers.
    public class QBittorrentTracker
    {
        public string Url { get; set; }
        public int Status { get; set; }
        public string Msg { get; set; }
    }
}
