using System;
using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public static class TalearrWishlistKinds
    {
        // A CSV export: uploaded (kept in the config folder) or a path on the server that is read
        // again on every sync.
        public const string File = "file";

        // A Hardcover list by its URL (https://hardcover.app/@user/lists/slug), read with the
        // Hardcover API token from Settings > Metadata.
        public const string HardcoverList = "hardcoverList";
    }

    public static class TalearrWishlistStates
    {
        public const string New = "new";
        public const string Monitored = "monitored";
        public const string Added = "added";
        public const string Queued = "queued";
        public const string InLibrary = "inLibrary";
        public const string Excluded = "excluded";
        public const string NoMatch = "noMatch";
        public const string Failed = "failed";
    }

    // One wishlist source. Stored as JSON in its own Config row (talearr.wishlist.<id>), so it
    // survives a talearr.db reset and is ignored by stock Chaptarr after a rollback. It is not an
    // import list provider: Chaptarr deletes provider rows whose implementation it does not know.
    public sealed class TalearrWishlist
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; } = TalearrWishlistKinds.File;

        // File: the CSV path (an uploaded file lives in <config>/talearr-wishlists/<id>.csv).
        public string Path { get; set; }

        // HardcoverList: the list URL.
        public string Url { get; set; }

        public bool Audiobook { get; set; } = true;
        public bool Ebook { get; set; }
        public bool SearchOnAdd { get; set; } = true;

        // Exports with a shelf or status column: only the to-read rows.
        public bool ToReadOnly { get; set; } = true;

        // Synced every 12 hours (System > Tasks > Sync Wishlists) when on.
        public bool Enabled { get; set; } = true;

        public string Format { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string LastResult { get; set; }
        public string LastError { get; set; }

        // Rows already acted on: row key -> state ("added", "monitored", "queued", "excluded",
        // "noMatch@2026-10-16"). A row is acted on once; unmonitoring the book later sticks.
        public Dictionary<string, string> Handled { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TalearrWishlistRow
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Author { get; set; }
        public string Isbn { get; set; }
        public string Asin { get; set; }
        public string Status { get; set; }

        // Known provider ids (Hardcover lists carry them), so no search is needed.
        public string BookProviderId { get; set; }
        public string AuthorProviderId { get; set; }
    }

    public sealed class TalearrWishlistItem
    {
        public TalearrWishlistRow Row { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
        public string BookTitle { get; set; }
        public string AuthorName { get; set; }
        public string BookProviderId { get; set; }
        public string AuthorProviderId { get; set; }

        // Library book rows of the matched book, per format, when it is already there.
        public int? AudiobookBookId { get; set; }
        public int? EbookBookId { get; set; }
    }

    public sealed class TalearrWishlistPreview
    {
        public string Format { get; set; }
        public int RowCount { get; set; }
        public List<TalearrWishlistItem> Items { get; set; } = new List<TalearrWishlistItem>();
    }

    public sealed class TalearrWishlistSyncResult
    {
        public string WishlistId { get; set; }
        public int Rows { get; set; }
        public int Added { get; set; }
        public int Monitored { get; set; }
        public int Queued { get; set; }
        public int AlreadyThere { get; set; }
        public int Excluded { get; set; }
        public int NoMatch { get; set; }
        public int Failed { get; set; }
        public int Deferred { get; set; }
        public List<TalearrWishlistItem> Items { get; set; } = new List<TalearrWishlistItem>();

        public string Describe()
        {
            var parts = new List<string> { $"{Rows} rows" };
            void Add(int count, string text)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {text}");
                }
            }

            Add(Added + Queued, "added");
            Add(Monitored, "monitored");
            Add(AlreadyThere, "already wanted or downloaded");
            Add(Excluded, "excluded");
            Add(NoMatch, "not found");
            Add(Failed, "failed");
            Add(Deferred, "left for the next sync");
            return string.Join(", ", parts);
        }
    }

    // The class name is what System > Tasks shows ("Sync Wishlists").
    public class SyncWishlistsCommand : Command
    {
        public string WishlistId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => string.IsNullOrWhiteSpace(WishlistId);
        public override string CompletionMessage => "Wishlists synced";
    }
}
