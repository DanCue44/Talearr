using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.BookEdits
{
    // Talearr: one series membership the user set for a work. Series are matched by title within
    // the author and media type, so the same entry links the audiobook and the ebook row.
    public sealed class TalearrSeriesEntry
    {
        public string Title { get; set; }

        // Free text ("1", "2.5", "0.5"), as the provider gives it. Null or empty means unnumbered.
        public string Position { get; set; }

        // API only: a series picked from the list. Never stored; the title is what is kept.
        // In a response it is the series the book is linked to, null when nothing links it.
        public int? SeriesId { get; set; }
    }

    // The provider's series link for one book row, as it was before an edit changed it.
    public sealed class TalearrProviderSeriesLink
    {
        public int SeriesId { get; set; }
        public string Position { get; set; }
    }

    // The provider's values for one book row, kept so clearing an edit can put them back.
    // Captured when an edit is first applied to the row and refreshed from every metadata refresh.
    public sealed class TalearrProviderBookValues
    {
        public bool TitleCaptured { get; set; }
        public string Title { get; set; }

        // By TalearrEditionKey (or "edition:<id>" for an edition without a provider id).
        public Dictionary<string, string> EditionTitles { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool SeriesCaptured { get; set; }
        public int? SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesPosition { get; set; }
        public List<TalearrProviderSeriesLink> Links { get; set; } = new List<TalearrProviderSeriesLink>();
    }

    // Talearr: the title and series edits of one work, as stored in talearr.db (BookDetailEdits).
    public sealed class TalearrBookEdit
    {
        // The work: Book.BaseBookId, else "book:<row id>" (TalearrBookGrabPolicyService.WorkKey).
        public string WorkKey { get; set; }

        // Null when the title is not edited.
        public string Title { get; set; }

        // Null when the series are not edited; empty when the user removed every series.
        public List<TalearrSeriesEntry> Series { get; set; }

        // Provider values by book row id. Its keys are also the rows the edit was applied to.
        public Dictionary<int, TalearrProviderBookValues> Provider { get; set; } = new Dictionary<int, TalearrProviderBookValues>();

        public int? BookIdHint { get; set; }
        public DateTime UpdatedAt { get; set; }

        public bool HasTitle => Title != null;
        public bool HasSeries => Series != null;
        public bool IsEmpty => !HasTitle && !HasSeries;
    }

    // Talearr: the narrators the user set for one edition (NarratorEdits). Keyed by the edition's
    // provider identity, so the base edition and every recording copy of it share the edit.
    public sealed class TalearrNarratorEdit
    {
        public string EditionKey { get; set; }
        public string WorkKey { get; set; }
        public List<string> Narrators { get; set; } = new List<string>();

        public bool ProviderCaptured { get; set; }
        public string ProviderNarrator { get; set; }
        public List<string> ProviderNarratorNames { get; set; } = new List<string>();

        public int? BookIdHint { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // A Series row Talearr created for an edit (UserSeries). Metadata refresh leaves these alone.
    public sealed class TalearrUserSeries
    {
        public int SeriesId { get; set; }
        public int AuthorId { get; set; }
        public int MediaType { get; set; }
        public string Title { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public static class TalearrBookEditFields
    {
        public const string Title = "title";
        public const string Series = "series";
        public const string Narrators = "narrators";

        public static readonly string[] All = { Title, Series, Narrators };
    }

    public enum TalearrBookEditStatus
    {
        Ok,

        // Unknown book (404).
        NotFound,

        // A bad value (400).
        Invalid
    }

    // A change from the API. Null fields are left as they are; Reset clears fields first.
    public sealed class TalearrBookEditChange
    {
        public string Title { get; set; }
        public List<TalearrSeriesEntry> Series { get; set; }
        public List<string> Narrators { get; set; }
        public List<string> Reset { get; set; }
    }

    public sealed class TalearrEditedValue<T>
    {
        public bool Edited { get; set; }
        public T Value { get; set; }

        // What the provider gave (the current value when nothing is edited).
        public T ProviderValue { get; set; }
    }

    public sealed class TalearrSeriesOption
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public bool CreatedByTalearr { get; set; }
    }

    public sealed class TalearrBookDetails
    {
        public int BookId { get; set; }
        public string WorkKey { get; set; }
        public string MediaType { get; set; }

        public TalearrEditedValue<string> Title { get; set; }
        public TalearrEditedValue<List<TalearrSeriesEntry>> Series { get; set; }

        // Null for an ebook or a book without editions.
        public TalearrEditedValue<List<string>> Narrators { get; set; }

        // The edition narrator edits apply to (the edition the book follows).
        public int? NarratorEditionId { get; set; }

        // The author's series for this media type, for a picker.
        public List<TalearrSeriesOption> AuthorSeries { get; set; } = new List<TalearrSeriesOption>();
    }

    public sealed class TalearrBookEditResult
    {
        public TalearrBookEditStatus Status { get; set; }
        public string Message { get; set; }
        public TalearrBookDetails Details { get; set; }
    }
}
