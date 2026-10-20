using System;
using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.SeriesMonitoring
{
    public static class TalearrSeriesFormats
    {
        public const string Audiobook = "audiobook";
        public const string Ebook = "ebook";

        public static readonly string[] All = { Audiobook, Ebook };
    }

    // A monitored series. Stored as JSON in one Config row per series
    // (talearr.series.monitor.<series key>), so it survives a talearr.db reset and a rollback
    // leaves it as a row stock Chaptarr never reads.
    public sealed class TalearrSeriesRule
    {
        public string SeriesKey { get; set; }
        public string Title { get; set; }
        public bool Audiobook { get; set; }
        public bool Ebook { get; set; }

        // Search for books the rule monitors (on creation and when new books arrive).
        public bool SearchOnAdd { get; set; } = true;

        // Novellas, short stories and other entries the metadata does not count as main books
        // (not primary, or a fractional position such as 1.5).
        public bool IncludeExtras { get; set; }

        public DateTime AddedAt { get; set; }
        public DateTime? LastAppliedAt { get; set; }
        public string LastResult { get; set; }

        // Works the rule has already acted on, per format (BaseBookId). A work is acted on once:
        // if you unmonitor it later, the rule leaves it alone.
        public Dictionary<string, List<string>> Handled { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Works the rule monitored while the author was not monitored for the format, so it did
        // not search; they are searched once the author's format is on (and still missing).
        public Dictionary<string, List<string>> PendingSearch { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public bool Wants(string format)
        {
            return string.Equals(format, TalearrSeriesFormats.Audiobook, StringComparison.OrdinalIgnoreCase) ? Audiobook : Ebook;
        }
    }

    public sealed class TalearrSeriesRuleRequest
    {
        public bool Audiobook { get; set; }
        public bool Ebook { get; set; }
        public bool? SearchOnAdd { get; set; }
        public bool? IncludeExtras { get; set; }
    }

    public sealed class TalearrSeriesAuthorsRequest
    {
        // audiobook | ebook
        public string Format { get; set; }
    }

    public sealed class TalearrSeriesFormatState
    {
        public string Format { get; set; }

        // The book row of this format (null when the library has no entry of this format).
        public int? BookId { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }

        // False when the author is not monitored for this format: Talearr then does not search.
        public bool AuthorMonitored { get; set; }

        // Additional recordings of the book (Talearr recordings); never changed by a series rule.
        public int Copies { get; set; }
        public bool Handled { get; set; }
    }

    public sealed class TalearrSeriesEntry
    {
        public string BaseBookId { get; set; }
        public string Title { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string Position { get; set; }
        public decimal? SortPosition { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsBundle { get; set; }
        public bool IsExtra { get; set; }

        // Why the rule skips this entry (bundle, extra), null when it is included.
        public string SkipReason { get; set; }
        public TalearrSeriesFormatState Audiobook { get; set; }
        public TalearrSeriesFormatState Ebook { get; set; }
    }

    public sealed class TalearrSeriesSummary
    {
        // Any of the series' rows (one per media type); the series page accepts each of them.
        public int SeriesId { get; set; }
        public List<int> SeriesIds { get; set; } = new List<int>();
        public string SeriesKey { get; set; }
        public string Title { get; set; }
        public List<string> Authors { get; set; } = new List<string>();
        public int BookCount { get; set; }
        public int AudiobookFiles { get; set; }
        public int EbookFiles { get; set; }
        public TalearrSeriesRule Rule { get; set; }
    }

    public sealed class TalearrSeriesDetail
    {
        public TalearrSeriesSummary Series { get; set; }
        public string Description { get; set; }
        public bool AllowBundles { get; set; }
        public List<TalearrSeriesEntry> Entries { get; set; } = new List<TalearrSeriesEntry>();
    }

    public sealed class TalearrSeriesApplyResult
    {
        public string SeriesKey { get; set; }
        public int Monitored { get; set; }
        public int AlreadyMonitored { get; set; }
        public int Searched { get; set; }
        public int NoEntry { get; set; }
        public int NotAllowed { get; set; }
        public int AuthorPaused { get; set; }
        public int Skipped { get; set; }

        public string Describe()
        {
            var parts = new List<string>();
            parts.Add($"{Monitored} newly monitored");
            if (AlreadyMonitored > 0)
            {
                parts.Add($"{AlreadyMonitored} already monitored");
            }

            if (Searched > 0)
            {
                parts.Add($"{Searched} searched");
            }

            if (NoEntry > 0)
            {
                parts.Add($"{NoEntry} not in the library in that format yet");
            }

            if (NotAllowed > 0)
            {
                parts.Add($"{NotAllowed} without a root folder for that format");
            }

            if (AuthorPaused > 0)
            {
                parts.Add($"{AuthorPaused} waiting on the author's format being monitored");
            }

            if (Skipped > 0)
            {
                parts.Add($"{Skipped} bundles or extras skipped");
            }

            return string.Join(", ", parts);
        }
    }

    // Applies every series rule (or those touching one author) to books that arrived since. The
    // class name is what System > Tasks shows ("Apply Series Monitoring"), so it has no brand word.
    public class ApplySeriesMonitoringCommand : Command
    {
        public int? AuthorId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => !AuthorId.HasValue;

        public override string CompletionMessage => "Series monitoring applied";
    }
}
