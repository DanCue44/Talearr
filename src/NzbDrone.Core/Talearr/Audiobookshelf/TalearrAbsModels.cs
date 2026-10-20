using System;
using System.Collections.Generic;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // Talearr: one Audiobookshelf library item as cached in talearr.db.
    public class TalearrAbsItem
    {
        // The Notifications row id of the Audiobookshelf connection the item was read through.
        public int ConnectionId { get; set; }
        public string ItemId { get; set; }
        public string LibraryId { get; set; }

        // As Audiobookshelf sees it.
        public string Path { get; set; }

        // The same folder as Talearr sees it, through the connection's library mappings. Null when
        // no mapping covers the item.
        public string LocalPath { get; set; }

        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Authors { get; set; }
        public string Narrators { get; set; }
        public string Asin { get; set; }
        public string Isbn { get; set; }
        public double? DurationSeconds { get; set; }

        // From Audiobookshelf's own abridged flag or an "abridged" / "unabridged" marker in the
        // title, subtitle or folder name. Runtime is judged later, against the wanted edition.
        public AbridgedMarker Abridged { get; set; }
        public bool HasAudio { get; set; }
        public bool HasEbook { get; set; }
        public string EbookFormat { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime SyncedAt { get; set; }
    }

    public static class TalearrAbsMatchMethods
    {
        // The item's folder holds files Talearr imported for the book.
        public const string Path = "path";
        public const string Asin = "asin";
        public const string Isbn = "isbn";
        public const string Title = "title";
    }

    // Talearr: which Talearr book row an Audiobookshelf item was matched to.
    public class TalearrAbsItemBook
    {
        public int ConnectionId { get; set; }
        public string ItemId { get; set; }
        public int BookId { get; set; }
        public string Method { get; set; }

        // True when the item is Talearr's own import (matched by path). Such items show the badge
        // but never block a grab, so upgrades of Talearr's files keep working.
        public bool Managed { get; set; }
    }

    public class TalearrAbsSyncState
    {
        public int ConnectionId { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string LastError { get; set; }
        public int ItemCount { get; set; }
        public int MatchedCount { get; set; }
    }

    // An item together with how it matched one book row.
    public class TalearrAbsBookMatch
    {
        public TalearrAbsItem Item { get; set; }
        public TalearrAbsItemBook Match { get; set; }
    }

    // The outcome of a sync of one connection.
    public class TalearrAbsSyncResult
    {
        public int ConnectionId { get; set; }
        public List<TalearrAbsItem> Items { get; set; } = new List<TalearrAbsItem>();
        public List<TalearrAbsItemBook> Matches { get; set; } = new List<TalearrAbsItemBook>();
    }
}
