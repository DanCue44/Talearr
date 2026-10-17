using System;
using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Migration
{
    public static class TalearrMigrationSources
    {
        // Readarr's API (URL and API key of a running instance) or its readarr.db / backup zip.
        public const string Readarr = "readarr";

        // Bookshelf (the Readarr fork with Hardcover metadata) speaks the same API and database
        // layout; only its ids are Hardcover's, so they are not looked up as Goodreads ids.
        public const string Bookshelf = "bookshelf";

        // LazyLibrarian's API (getIndex and getAllBooks).
        public const string LazyLibrarian = "lazylibrarian";
    }

    public static class TalearrMigrationActions
    {
        public const string Create = "create";
        public const string Exists = "exists";
        public const string Map = "map";
        public const string Monitor = "monitor";
        public const string Skip = "skip";
        public const string Unmatched = "unmatched";
        public const string Conflict = "conflict";
        public const string Failed = "failed";
    }

    public static class TalearrMigrationAreas
    {
        public const string Tag = "tag";
        public const string RootFolder = "rootFolder";
        public const string QualityProfile = "qualityProfile";
        public const string MetadataProfile = "metadataProfile";
        public const string DownloadClient = "downloadClient";
        public const string Indexer = "indexer";
        public const string Author = "author";
        public const string Book = "book";
    }

    public sealed class TalearrMigrationPathMapping
    {
        public string From { get; set; }
        public string To { get; set; }
    }

    // What to read and how to bring it over. Nothing in the source is ever changed: the API is
    // only read with GET, a database file is opened read-only (a zip is unpacked to a temp folder).
    public sealed class TalearrMigrationRequest
    {
        public string Source { get; set; } = TalearrMigrationSources.Readarr;
        public string Url { get; set; }
        public string ApiKey { get; set; }

        // A readarr.db or a Readarr backup zip on this server (for example a mounted Readarr /config).
        public string BackupPath { get; set; }

        // "auto" (a root folder with "audio" in its path holds audiobooks), "ebook" or "audiobook".
        public string MediaType { get; set; } = "auto";

        // Source path prefix to TaleArr path prefix, for containers that mount the library elsewhere.
        public List<TalearrMigrationPathMapping> PathMappings { get; set; } = new List<TalearrMigrationPathMapping>();

        public bool ImportRootFolders { get; set; } = true;
        public bool ImportDownloadClients { get; set; } = true;
        public bool ImportIndexers { get; set; } = true;

        // Off by default: the source app usually keeps running, and two apps grabbing from the same
        // indexers into the same client make duplicate downloads. Turn them on once the source stops.
        public bool EnableImportedProviders { get; set; }

        public bool SearchOnAdd { get; set; }
    }

    public sealed class TalearrSourceTag
    {
        public int Id { get; set; }
        public string Label { get; set; }
    }

    public sealed class TalearrSourceProfile
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class TalearrSourceRootFolder
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public string NewItemMonitor { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public sealed class TalearrSourceProvider
    {
        public string Name { get; set; }
        public string Implementation { get; set; }
        public string ConfigContract { get; set; }

        // The provider's settings as a JSON object (camelCase, as Readarr stores and serves them).
        public string Settings { get; set; }
        public bool Enable { get; set; }
        public bool EnableRss { get; set; }
        public bool EnableAutomaticSearch { get; set; }
        public bool EnableInteractiveSearch { get; set; }
        public int Priority { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public sealed class TalearrSourceAuthor
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string ForeignAuthorId { get; set; }
        public bool Monitored { get; set; }
        public string MonitorNewItems { get; set; }
        public string Path { get; set; }
        public string RootFolderPath { get; set; }
        public int QualityProfileId { get; set; }
        public int MetadataProfileId { get; set; }
        public List<int> Tags { get; set; } = new List<int>();
    }

    public sealed class TalearrSourceBook
    {
        public int Id { get; set; }
        public int AuthorId { get; set; }
        public string Title { get; set; }
        public string ForeignBookId { get; set; }
        public string ForeignEditionId { get; set; }
        public string Isbn13 { get; set; }
        public string Asin { get; set; }
        public bool Monitored { get; set; }
        public bool HasFile { get; set; }

        // LazyLibrarian tracks the ebook and the audiobook of a book separately.
        public bool? AudiobookMonitored { get; set; }
    }

    // Everything read from the source, in one shape for every source.
    public sealed class TalearrMigrationSnapshot
    {
        public string Source { get; set; }
        public string AppName { get; set; }
        public string Version { get; set; }
        public List<TalearrSourceRootFolder> RootFolders { get; set; } = new List<TalearrSourceRootFolder>();
        public List<TalearrSourceProfile> QualityProfiles { get; set; } = new List<TalearrSourceProfile>();
        public List<TalearrSourceProfile> MetadataProfiles { get; set; } = new List<TalearrSourceProfile>();
        public List<TalearrSourceTag> Tags { get; set; } = new List<TalearrSourceTag>();
        public List<TalearrSourceProvider> DownloadClients { get; set; } = new List<TalearrSourceProvider>();
        public List<TalearrSourceProvider> Indexers { get; set; } = new List<TalearrSourceProvider>();
        public List<TalearrSourceAuthor> Authors { get; set; } = new List<TalearrSourceAuthor>();
        public List<TalearrSourceBook> Books { get; set; } = new List<TalearrSourceBook>();
    }

    public sealed class TalearrMigrationEntry
    {
        public string Area { get; set; }
        public string Name { get; set; }
        public string Action { get; set; }
        public string Detail { get; set; }
    }

    // One source author matched to a metadata author, with the books that get monitored.
    public sealed class TalearrMigrationAuthorPlan
    {
        public int SourceAuthorId { get; set; }
        public string Name { get; set; }
        public string AuthorProviderId { get; set; }
        public string MediaType { get; set; }
        public string RootFolderPath { get; set; }
        public bool Monitored { get; set; }
        public string MonitorNewItems { get; set; }
        public int? QualityProfileId { get; set; }
        public int? MetadataProfileId { get; set; }
        public List<string> TagLabels { get; set; } = new List<string>();
        public List<TalearrMigrationBookPlan> Books { get; set; } = new List<TalearrMigrationBookPlan>();
    }

    public sealed class TalearrMigrationBookPlan
    {
        public string Title { get; set; }
        public string BookProviderId { get; set; }
        public string MediaType { get; set; }
    }

    public sealed class TalearrMigrationSummary
    {
        public int Authors { get; set; }
        public int AuthorsMatched { get; set; }
        public int Books { get; set; }
        public int BooksMonitored { get; set; }
        public int BooksMatched { get; set; }
        public int Unmatched { get; set; }
        public int Conflicts { get; set; }
        public int Creates { get; set; }
    }

    public sealed class TalearrMigrationReport
    {
        public bool DryRun { get; set; } = true;
        public string Source { get; set; }
        public string AppName { get; set; }
        public string Version { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? FinishedAt { get; set; }
        public string Error { get; set; }
        public TalearrMigrationSummary Summary { get; set; } = new TalearrMigrationSummary();
        public List<TalearrMigrationEntry> Entries { get; set; } = new List<TalearrMigrationEntry>();
        public List<TalearrMigrationAuthorPlan> Authors { get; set; } = new List<TalearrMigrationAuthorPlan>();

        public TalearrMigrationEntry Add(string area, string name, string action, string detail = null)
        {
            var entry = new TalearrMigrationEntry { Area = area, Name = name, Action = action, Detail = detail };
            Entries.Add(entry);
            switch (action)
            {
                case TalearrMigrationActions.Unmatched: Summary.Unmatched++; break;
                case TalearrMigrationActions.Conflict: Summary.Conflicts++; break;
                case TalearrMigrationActions.Create: Summary.Creates++; break;
            }

            return entry;
        }
    }

    // The last dry run, kept in <config>/talearr-migration/plan.json so Apply does exactly what the
    // preview showed, without looking everything up again. Removed after a successful apply.
    public sealed class TalearrMigrationPlan
    {
        public TalearrMigrationRequest Request { get; set; }
        public TalearrMigrationSnapshot Snapshot { get; set; }
        public TalearrMigrationReport Report { get; set; }
    }

    public sealed class TalearrMigrationStatus
    {
        public string State { get; set; } = "idle";
        public string Message { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public bool HasPlan { get; set; }
        public TalearrMigrationReport Report { get; set; }
    }

    public class TalearrMigrationException : Exception
    {
        public TalearrMigrationException(string message)
            : base(message)
        {
        }

        public TalearrMigrationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public class TalearrMigrationCommand : Command
    {
        // False: read the source and write the dry-run plan. True: carry out the saved plan.
        public bool Apply { get; set; }

        public override bool SendUpdatesToClient => true;
        public override string CompletionMessage => Apply ? "Migration applied" : "Migration preview ready";
    }
}
