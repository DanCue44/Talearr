using System;
using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: the read-only comparison of the metadata server and Talearr's Hardcover source for
    // one local author, and for a whole run over the library (docs/TALEARR_METADATA_SOURCE.md).
    public class TalearrShadowMediaCounts
    {
        public int Audiobook { get; set; }
        public int Ebook { get; set; }
        public int Total => Audiobook + Ebook;
    }

    public class TalearrShadowSideReport
    {
        public bool Available { get; set; }
        public string Error { get; set; }

        // Book rows (one per media type) as converted, and after the author's metadata profiles,
        // import list exclusions and edition retention (RefreshAuthorService.NormalizeRemoteBooks).
        public TalearrShadowMediaCounts BooksBeforeFilter { get; set; } = new TalearrShadowMediaCounts();
        public TalearrShadowMediaCounts BooksAfterFilter { get; set; } = new TalearrShadowMediaCounts();

        // Distinct works (base book ids) after filtering.
        public int WorksAfterFilter { get; set; }

        public TalearrShadowMediaCounts EditionsBeforeFilter { get; set; } = new TalearrShadowMediaCounts();
        public TalearrShadowMediaCounts EditionsAfterFilter { get; set; } = new TalearrShadowMediaCounts();

        // Series rows the conversion keeps (today only Goodreads-backed series survive it).
        public int SeriesKept { get; set; }

        // Series the source sent before conversion, when known (Hardcover side).
        public int? SeriesFromSource { get; set; }

        // Local book rows that a remote book on this side matches by provider id.
        public int MatchedLocalBooks { get; set; }
    }

    public class TalearrShadowWork
    {
        public string Title { get; set; }
        public string WorkId { get; set; }
        public List<string> ProviderIds { get; set; } = new List<string>();
        public List<string> MediaTypes { get; set; } = new List<string>();
    }

    public class TalearrShadowLocalBook
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public string MediaType { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
        public bool ManualAdd { get; set; }

        // Monitored, file-backed or manually added: the user wants this row.
        public bool Protected => Monitored || HasFiles || ManualAdd;
        public List<string> ProviderIds { get; set; } = new List<string>();

        // "missing from Hardcover" (no Hardcover book carries any of its ids), or
        // "filtered by profile" (a Hardcover book matches it, but the metadata profile drops it).
        public string Reason { get; set; }
    }

    public class TalearrShadowAuthorReport
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string ServerAuthorId { get; set; }
        public string HardcoverAuthorId { get; set; }

        // ok | skipped | error
        public string Status { get; set; }
        public string Message { get; set; }
        public DateTime ComparedAt { get; set; }

        public int LocalBooks { get; set; }
        public int LocalProtectedBooks { get; set; }

        public TalearrShadowSideReport Server { get; set; } = new TalearrShadowSideReport();
        public TalearrShadowSideReport Hardcover { get; set; } = new TalearrShadowSideReport();

        // Works after filtering that only one side has (matched by stable work ids). Lists are
        // capped at ListLimit entries; the counts are complete.
        public int OnlyOnServerCount { get; set; }
        public int OnlyOnHardcoverCount { get; set; }
        public List<TalearrShadowWork> OnlyOnServer { get; set; } = new List<TalearrShadowWork>();
        public List<TalearrShadowWork> OnlyOnHardcover { get; set; } = new List<TalearrShadowWork>();

        // Local rows the server matches today and Hardcover would not. Protected ones first.
        public int LostMatchesCount { get; set; }
        public int LostProtectedCount { get; set; }
        public List<TalearrShadowLocalBook> LostMatches { get; set; } = new List<TalearrShadowLocalBook>();

        // Local rows only Hardcover matches.
        public int GainedMatchesCount { get; set; }

        public int HardcoverRequests { get; set; }
    }

    public class TalearrShadowRunReport
    {
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public int AuthorsTotal { get; set; }
        public int AuthorsCompared { get; set; }
        public int AuthorsSkipped { get; set; }
        public int AuthorsFailed { get; set; }
        public int AuthorsWithLostProtectedBooks { get; set; }
        public int HardcoverRequests { get; set; }
        public bool StoppedEarly { get; set; }
        public string StopReason { get; set; }
        public List<TalearrShadowAuthorReport> Authors { get; set; } = new List<TalearrShadowAuthorReport>();
    }

    // Talearr: runs the shadow comparison over the library (or the given authors) and keeps the
    // last report in memory for GET /api/v1/talearr/metadata/shadow. Never writes books or
    // authors. Not scheduled; queue it from the API: {"name": "TalearrMetadataShadowCompare"}.
    public class TalearrMetadataShadowCompareCommand : Command
    {
        public const int DefaultMaxHardcoverRequests = 1500;

        // Empty: every author with a Hardcover id.
        public List<int> AuthorIds { get; set; } = new List<int>();

        // Stop the run after this many Hardcover requests (Free tokens get 5,000 a day).
        public int MaxHardcoverRequests { get; set; } = DefaultMaxHardcoverRequests;

        public override bool SendUpdatesToClient => true;
        public override string CompletionMessage => "Metadata shadow comparison finished";
    }
}
