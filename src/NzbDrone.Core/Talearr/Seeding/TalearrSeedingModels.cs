using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NzbDrone.Core.Talearr.Seeding
{
    // TaleArr: GET /api/v1/talearr/seeding. Read-only. Bytes and seconds, ISO 8601 UTC times.
    // The API serializer drops nulls by default, but this contract keeps every field, so every
    // nullable property says so explicitly.
    public class TalearrSeedingReport
    {
        public DateTime GeneratedAt { get; set; }
        public List<TalearrSeedingTorrent> Torrents { get; set; } = new List<TalearrSeedingTorrent>();
        public TalearrSeedingSummary Summary { get; set; } = new TalearrSeedingSummary();

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public TalearrSeedingMamStatus Mam { get; set; }

        // TaleArr: MAM freeleech seeding filler, null when it was never turned on.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public MamFiller.TalearrMamFillerStatus Filler { get; set; }
    }

    public class TalearrSeedingTorrent
    {
        public string DownloadId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Title { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? BookId { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string BookTitle { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string AuthorName { get; set; }

        // audiobook | ebook | null
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string MediaType { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string Indexer { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool? IsPrivateTracker { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string DownloadClient { get; set; }

        // seeding | stalled | queued | paused | checking | error | downloading | missing
        public string State { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? Size { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? Uploaded { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public double? Ratio { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? SeedingSeconds { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? RequiredSeedingSeconds { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? SeedGoalSeconds { get; set; }

        // Null when the client does not report seeding time and a requirement exists.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool? RequirementMet { get; set; }

        public bool AtRisk { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string RiskReason { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? AddedAt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? CompletedAt { get; set; }

        // TaleArr: added by the MAM freeleech seeding filler (not a library grab).
        public bool IsFiller { get; set; }
    }

    public class TalearrSeedingSummary
    {
        public int Total { get; set; }
        public int Seeding { get; set; }
        public int AtRisk { get; set; }
        public int PrivateUnsatisfied { get; set; }
    }

    public class TalearrSeedingMamStatus
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string UserClass { get; set; }

        public bool IsVip { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? Unsatisfied { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? UnsatisfiedLimit { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? Wedges { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public double? BonusPoints { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? CheckedAt { get; set; }

        // TaleArr: bonus auto-spend additions (null when MAM did not send them).
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? VipUntil { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public double? Ratio { get; set; }

        public bool Restricted { get; set; }
        public bool BonusAutoSpend { get; set; }
        public bool BonusDryRun { get; set; }
        public List<TalearrSeedingBonusPurchase> BonusPurchases { get; set; } = new List<TalearrSeedingBonusPurchase>();

        // TaleArr: Power User progress (TalearrMamPowerUserProgress). Null when unknown.
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? MemberSince { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? UploadedBytes { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? DownloadedBytes { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int? DaysUntilEligible { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public long? UploadedRemainingBytes { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool? RatioOk { get; set; }
    }

    public class TalearrSeedingBonusPurchase
    {
        public DateTime AttemptedAt { get; set; }
        public string SpendType { get; set; }
        public string Parameters { get; set; }
        public int EstimatedCost { get; set; }
        public bool DryRun { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Reason { get; set; }
    }

    // One torrent as a download client reports it, before it is matched to TaleArr's history.
    public class TalearrSeedingClientTorrent
    {
        public string DownloadId { get; set; }
        public string Title { get; set; }
        public string Category { get; set; }
        public string State { get; set; }
        public string ClientMessage { get; set; }
        public long? Size { get; set; }
        public long? Uploaded { get; set; }
        public double? Ratio { get; set; }
        public long? SeedingSeconds { get; set; }

        // The client's effective seeding time limit for this torrent, null when unlimited/unknown.
        public long? SeedLimitSeconds { get; set; }
        public DateTime? AddedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public bool? IsPrivate { get; set; }

        // The torrent's current tracker is MyAnonamouse (the host only; the URL holds a passkey).
        public bool IsMamTracker { get; set; }

        // Set when the client is not filtered to TaleArr's categories: keep only TaleArr grabs.
        public bool RequireHistoryMatch { get; set; }
    }

    // A download client that can report seeding details (qBittorrent).
    public interface ITalearrSeedingSource
    {
        List<TalearrSeedingClientTorrent> GetSeedingTorrents();

        // Null when at least one real tracker works (or none was contacted yet).
        string GetTrackerError(string downloadId);
    }

    public class TalearrSeedingGrab
    {
        public string DownloadId { get; set; }
        public int? BookId { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
        public string MediaType { get; set; }
        public string IndexerName { get; set; }
        public string DownloadClientName { get; set; }
        public DateTime Date { get; set; }
    }

    public class TalearrSeedingIndexerInfo
    {
        public string Name { get; set; }
        public bool IsMam { get; set; }
        public long? SeedTimeSeconds { get; set; }
    }
}
