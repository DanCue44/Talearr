using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Talearr.Comics
{
    public interface ITalearrComicsDatabase
    {
        string DatabasePath { get; }
        int SchemaVersion { get; }

        // Opens talearr-comics.db, creating or upgrading it first. Throws
        // TalearrComicsDatabaseUnavailableException when the file cannot be used.
        IDbConnection OpenConnection();

        // Null when the database opened fine, otherwise why it did not.
        string UnavailableReason { get; }
    }

    public class TalearrComicsDatabaseUnavailableException : NzbDroneException
    {
        public TalearrComicsDatabaseUnavailableException(string message)
            : base(message)
        {
        }

        public TalearrComicsDatabaseUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    // Talearr: the comics and manga library (docs: notes/TALEARR_COMICS_PLAN.md). Unlike
    // talearr.db this file is durable: it holds the user's comic series, volumes and files, so it
    // is never deleted or recreated. A damaged or too-new file stops the comics module (with a
    // health check) and is left as it is for the user to restore from a backup. chaptarr.db is
    // never touched, so books keep working and a rollback to Chaptarr is unaffected; the regular
    // backup copies every .db file next to chaptarr.db, so this one is backed up too.
    public class TalearrComicsDatabase : ITalearrComicsDatabase
    {
        public const string FileName = "talearr-comics.db";

        // One entry per schema version. Never edit a shipped entry; append a new one.
        private static readonly string[] Migrations =
        {
            // 1: Phase 1, volumes.
            @"
CREATE TABLE IF NOT EXISTS ComicRootFolders (
    Id            INTEGER PRIMARY KEY,
    Path          TEXT NOT NULL UNIQUE,
    Kind          TEXT NOT NULL,
    IsKids        INTEGER NOT NULL DEFAULT 0,
    MaxAgeRating  TEXT
);

CREATE TABLE IF NOT EXISTS ComicSeries (
    Id                 INTEGER PRIMARY KEY,
    Kind               TEXT NOT NULL,
    Title              TEXT NOT NULL,
    SortTitle          TEXT NOT NULL,
    AlternateTitles    TEXT,
    Year               INTEGER,
    Status             TEXT,
    Overview           TEXT,
    Authors            TEXT,
    Publisher          TEXT,
    Genres             TEXT,
    CoverUrl           TEXT,
    HardcoverSeriesId  INTEGER,
    AniListId          INTEGER,
    TotalVolumes       INTEGER,
    AgeRating          TEXT,
    AgeRatingOverride  TEXT,
    IsAdult            INTEGER NOT NULL DEFAULT 0,
    ReadingDirection   TEXT,
    LanguageIso        TEXT,
    RootFolderId       INTEGER NOT NULL,
    Path               TEXT NOT NULL,
    Monitored          INTEGER NOT NULL DEFAULT 1,
    MonitorNewVolumes  INTEGER NOT NULL DEFAULT 1,
    Added              TEXT NOT NULL,
    LastRefreshed      TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_ComicSeries_AniList ON ComicSeries (AniListId) WHERE AniListId IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS IX_ComicSeries_Hardcover ON ComicSeries (HardcoverSeriesId) WHERE HardcoverSeriesId IS NOT NULL;

CREATE TABLE IF NOT EXISTS ComicVolumes (
    Id              INTEGER PRIMARY KEY,
    SeriesId        INTEGER NOT NULL,
    Number          REAL NOT NULL,
    Title           TEXT,
    ReleaseDate     TEXT,
    CoverUrl        TEXT,
    PageCount       INTEGER,
    HardcoverBookId INTEGER,
    Monitored       INTEGER NOT NULL DEFAULT 1,
    UNIQUE (SeriesId, Number)
);

CREATE TABLE IF NOT EXISTS ComicFiles (
    Id            INTEGER PRIMARY KEY,
    SeriesId      INTEGER NOT NULL,
    VolumeId      INTEGER NOT NULL,
    Path          TEXT NOT NULL UNIQUE,
    Size          INTEGER NOT NULL DEFAULT 0,
    Format        TEXT NOT NULL,
    Source        TEXT NOT NULL,
    ReleaseGroup  TEXT,
    SourceTitle   TEXT,
    Score         INTEGER NOT NULL DEFAULT 0,
    DateAdded     TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ComicFiles_Volume ON ComicFiles (VolumeId);

CREATE TABLE IF NOT EXISTS ComicDownloads (
    DownloadId        TEXT NOT NULL PRIMARY KEY,
    DownloadClientId  INTEGER NOT NULL,
    Protocol          TEXT NOT NULL,
    SeriesId          INTEGER NOT NULL,
    VolumeIds         TEXT NOT NULL,
    ReleaseTitle      TEXT NOT NULL,
    Indexer           TEXT,
    Score             INTEGER NOT NULL DEFAULT 0,
    Status            TEXT NOT NULL,
    OutputPath        TEXT,
    Message           TEXT,
    GrabbedAt         TEXT NOT NULL,
    UpdatedAt         TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ComicHistory (
    Id           INTEGER PRIMARY KEY,
    SeriesId     INTEGER NOT NULL,
    VolumeId     INTEGER,
    EventType    TEXT NOT NULL,
    SourceTitle  TEXT,
    Message      TEXT,
    Date         TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ComicHistory_Series ON ComicHistory (SeriesId, Date);

CREATE TABLE IF NOT EXISTS ComicBlocklist (
    Id           INTEGER PRIMARY KEY,
    SeriesId     INTEGER NOT NULL,
    SourceTitle  TEXT NOT NULL,
    Indexer      TEXT,
    Reason       TEXT,
    Date         TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ComicBlocklist_Series ON ComicBlocklist (SeriesId);

CREATE TABLE IF NOT EXISTS ComicSettings (
    Key    TEXT NOT NULL PRIMARY KEY,
    Value  TEXT
);
"
        };

        public static int CurrentSchemaVersion => Migrations.Length;

        private readonly Logger _logger;
        private readonly object _lock = new object();
        private readonly string _connectionString;
        private bool _initialized;

        public TalearrComicsDatabase(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _logger = logger;

            var folder = Path.GetDirectoryName(appFolderInfo.GetDatabase());
            if (string.IsNullOrWhiteSpace(folder))
            {
                folder = appFolderInfo.AppDataFolder;
            }

            DatabasePath = Path.Combine(folder, FileName);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
        }

        public string DatabasePath { get; }

        public string UnavailableReason { get; private set; }

        public int SchemaVersion
        {
            get
            {
                using var connection = OpenConnection();
                return connection.ExecuteScalar<int>("PRAGMA user_version;");
            }
        }

        public IDbConnection OpenConnection()
        {
            EnsureInitialized();
            return OpenRaw();
        }

        private void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized)
                {
                    return;
                }

                try
                {
                    using var connection = OpenRaw();

                    var check = connection.ExecuteScalar<string>("PRAGMA quick_check(1);");
                    if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Unavailable($"{FileName} failed its integrity check ('{check}'). Restore it from a backup.");
                    }

                    var version = connection.ExecuteScalar<int>("PRAGMA user_version;");
                    if (version > CurrentSchemaVersion)
                    {
                        throw Unavailable($"{FileName} was written by a newer Talearr (schema {version}, this one knows {CurrentSchemaVersion}). Update Talearr.");
                    }

                    for (var next = version; next < CurrentSchemaVersion; next++)
                    {
                        using var transaction = connection.BeginTransaction();
                        connection.Execute(Migrations[next], transaction: transaction);
                        connection.Execute($"PRAGMA user_version = {next + 1};", transaction: transaction);
                        transaction.Commit();

                        _logger.Info("Talearr: {0} schema upgraded to version {1}", FileName, next + 1);
                    }

                    UnavailableReason = null;
                    _initialized = true;
                }
                catch (TalearrComicsDatabaseUnavailableException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw Unavailable($"{FileName} cannot be opened: {ex.Message}", ex);
                }
            }
        }

        private TalearrComicsDatabaseUnavailableException Unavailable(string reason, Exception inner = null)
        {
            UnavailableReason = reason;
            _logger.Error(inner, "Talearr: comics are unavailable. {0}", reason);
            return inner == null
                ? new TalearrComicsDatabaseUnavailableException(reason)
                : new TalearrComicsDatabaseUnavailableException(reason, inner);
        }

        private SqliteConnection OpenRaw()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                // FULL: this file holds the user's library, not a cache.
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
                command.ExecuteNonQuery();
            }

            return connection;
        }
    }
}
