using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.PreferredSeries
{
    // The book row's own main series before the preference replaced it, so clearing the
    // preference can put it back without a refresh.
    public sealed class TalearrPreferredSeriesProvider
    {
        public int? SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
    }

    // One stored preference: the work (TalearrBookGrabPolicyService.WorkKey) and the title of the
    // series that is its main one.
    public sealed class TalearrPreferredSeriesEntry
    {
        public string WorkKey { get; set; }
        public string SeriesTitle { get; set; }
        public int? BookIdHint { get; set; }

        // By book row id. Its keys are the rows the preference was written to.
        public Dictionary<int, TalearrPreferredSeriesProvider> Provider { get; set; } = new Dictionary<int, TalearrPreferredSeriesProvider>();

        public DateTime UpdatedAt { get; set; }
    }

    public sealed class TalearrPreferredSeriesOption
    {
        public int SeriesId { get; set; }
        public string Title { get; set; }
        public string Position { get; set; }
    }

    public sealed class TalearrPreferredSeriesDetails
    {
        public int BookId { get; set; }

        // The stored choice, null when the book follows the metadata's order.
        public string PreferredSeries { get; set; }

        // The series the book uses now for naming and ordering.
        public string CurrentSeries { get; set; }

        // The series the book is in. A choice is only offered when there are two or more.
        public List<TalearrPreferredSeriesOption> Options { get; set; } = new List<TalearrPreferredSeriesOption>();
    }

    public enum TalearrPreferredSeriesStatus
    {
        Ok,
        NotFound,
        Invalid
    }

    public sealed class TalearrPreferredSeriesResult
    {
        public TalearrPreferredSeriesStatus Status { get; set; }
        public string Message { get; set; }
        public TalearrPreferredSeriesDetails Details { get; set; }
    }
}
