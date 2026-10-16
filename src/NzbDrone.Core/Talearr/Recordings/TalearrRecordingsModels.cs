using System;
using System.Collections.Generic;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Recordings
{
    public enum AudibleProductStatus
    {
        Ok = 0,
        NotFound = 1,
        RegionUnavailable = 2
    }

    // TaleArr: Audible facts for one product in one marketplace, as cached in talearr.db.
    public class AudibleProduct
    {
        public const string SourceAudible = "audible";
        public const string SourceAudnexus = "audnexus";

        public string Marketplace { get; set; }
        public string Asin { get; set; }
        public AudibleProductStatus Status { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }

        // "unabridged", "abridged" or null.
        public string FormatType { get; set; }
        public int? RuntimeMinutes { get; set; }
        public List<string> Narrators { get; set; } = new List<string>();
        public List<string> Authors { get; set; } = new List<string>();
        public string Publisher { get; set; }

        // ISO date (yyyy-MM-dd) or null.
        public string ReleaseDate { get; set; }
        public string Language { get; set; }
        public string Source { get; set; } = SourceAudible;
        public DateTime FetchedAt { get; set; }

        public bool Found => Status == AudibleProductStatus.Ok;

        public AbridgedMarker Abridged
        {
            get
            {
                if (string.Equals(FormatType, "unabridged", StringComparison.OrdinalIgnoreCase))
                {
                    return AbridgedMarker.Unabridged;
                }

                return string.Equals(FormatType, "abridged", StringComparison.OrdinalIgnoreCase)
                    ? AbridgedMarker.Abridged
                    : AbridgedMarker.Unknown;
            }
        }

        public static AudibleProduct Missing(string marketplace, string asin, AudibleProductStatus status, string source, DateTime fetchedAt)
        {
            return new AudibleProduct
            {
                Marketplace = marketplace,
                Asin = asin,
                Status = status,
                Source = source,
                FetchedAt = fetchedAt
            };
        }
    }

    // TaleArr: which Audible product a Chaptarr edition resolved to, keyed by the edition's
    // provider identity (TalearrEditionKey, stored in the ForeignEditionId column) so it survives
    // row churn and is shared by a base edition and its upstream copies. Asin null means "looked,
    // nothing matched well enough".
    public class EditionAudibleLink
    {
        public const string MethodStoredAsin = "stored-asin";
        public const string MethodAsinsList = "asins-list";
        public const string MethodSearch = "search";

        public string ForeignEditionId { get; set; }
        public string Marketplace { get; set; }
        public string Asin { get; set; }
        public string Method { get; set; }
        public double Confidence { get; set; }

        // Local edition id at the time of the check. A hint only; recheck on read.
        public int? EditionIdHint { get; set; }
        public DateTime CheckedAt { get; set; }

        public bool Matched => !string.IsNullOrWhiteSpace(Asin);
    }

    // TaleArr: optional provenance for a recording someone asked for. Safe to lose.
    public class RecordingRequest
    {
        public int Id { get; set; }
        public string BaseBookId { get; set; }
        public int? CopyBookIdHint { get; set; }
        public string ForeignEditionId { get; set; }

        // unabridged | abridged | narrator | edition
        public string Kind { get; set; }

        // ui | allseerr | api | ...
        public string RequestedBy { get; set; }
        public string ExternalRef { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
