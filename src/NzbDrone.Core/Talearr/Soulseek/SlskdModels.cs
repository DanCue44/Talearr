using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.Soulseek
{
    // Talearr: the parts of slskd's REST API (api/v0) that Talearr reads. Only the fields used
    // here are mapped; slskd sends more.
    public class SlskdSearch
    {
        public string Id { get; set; }
        public string SearchText { get; set; }
        public bool IsComplete { get; set; }
        public string State { get; set; }
        public int ResponseCount { get; set; }
        public int FileCount { get; set; }
    }

    public class SlskdSearchResponse
    {
        public string Username { get; set; }
        public int FileCount { get; set; }
        public List<SlskdFile> Files { get; set; } = new List<SlskdFile>();
        public bool HasFreeUploadSlot { get; set; }
        public long QueueLength { get; set; }

        // Bytes per second.
        public int UploadSpeed { get; set; }
        public int LockedFileCount { get; set; }
        public List<SlskdFile> LockedFiles { get; set; } = new List<SlskdFile>();
    }

    public class SlskdFile
    {
        public string Filename { get; set; }
        public long Size { get; set; }
        public int? BitRate { get; set; }
        public int? BitDepth { get; set; }
        public int? SampleRate { get; set; }
        public int? Length { get; set; }
        public string Extension { get; set; }
        public bool IsLocked { get; set; }
    }

    public class SlskdUserTransfers
    {
        public string Username { get; set; }
        public List<SlskdTransferDirectory> Directories { get; set; } = new List<SlskdTransferDirectory>();
    }

    public class SlskdTransferDirectory
    {
        public string Directory { get; set; }
        public List<SlskdTransfer> Files { get; set; } = new List<SlskdTransfer>();
    }

    public class SlskdTransfer
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string Filename { get; set; }
        public long Size { get; set; }

        // A flags enum serialized as text, for example "Queued, Remotely", "InProgress",
        // "Completed, Succeeded", "Completed, Errored".
        public string State { get; set; }
        public long BytesTransferred { get; set; }
        public double PercentComplete { get; set; }
        public DateTime? RequestedAt { get; set; }
        public DateTime? EnqueuedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public int? PlaceInQueue { get; set; }
        public string Exception { get; set; }
    }

    public class SlskdQueueFile
    {
        public string Filename { get; set; }
        public long Size { get; set; }
    }

    public class SlskdBatchRequest
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public List<SlskdQueueFile> Files { get; set; } = new List<SlskdQueueFile>();
        public SlskdBatchOptions Options { get; set; } = new SlskdBatchOptions();
    }

    public class SlskdBatchOptions
    {
        public string Destination { get; set; }
        public string ExternalId { get; set; }
    }

    public class SlskdApplicationState
    {
        public SlskdVersion Version { get; set; }
        public SlskdServerState Server { get; set; }
    }

    public class SlskdVersion
    {
        public string Full { get; set; }
        public string Current { get; set; }
    }

    public class SlskdServerState
    {
        public bool IsConnected { get; set; }
        public bool IsLoggedIn { get; set; }
        public string Username { get; set; }
    }

    // What slskd's enqueue call achieved.
    public class SlskdEnqueueResult
    {
        // True when slskd accepted a batch with Talearr's own destination folder (slskd 0.26+).
        public bool UsedBatchDestination { get; set; }
        public string Destination { get; set; }
    }
}
