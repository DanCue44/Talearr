using System;
using System.Data;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Talearr.Recordings
{
    public interface ITalearrDatabase
    {
        string DatabasePath { get; }
        int SchemaVersion { get; }

        // Opens a connection to talearr.db, creating or upgrading the file first if needed.
        IDbConnection OpenConnection();

        // Deletes the file and starts again. Everything in it is a rebuildable cache.
        void Reset(string reason);

        bool IsCorruption(Exception ex);
    }

    // TaleArr: a small SQLite file next to chaptarr.db that only TaleArr opens. It holds
    // rebuildable data (Audible facts, which edition resolved to which product, optional request
    // provenance) and per-book abridged overrides (safe to lose: the book then follows the global
    // grab policy), keyed by provider identity rather than Chaptarr row ids. Chaptarr never reads
    // it, it is not part of the FluentMigrator schema, and losing it only costs a refetch.
    //
    // Schema versions live in PRAGMA user_version. A file that is corrupt, unreadable or written
    // by a newer TaleArr is moved aside and recreated rather than repaired.
    public class TalearrDatabase : ITalearrDatabase
    {
        public const string FileName = "talearr.db";

        private const int SqliteCorrupt = 11;
        private const int SqliteNotADatabase = 26;

        // One entry per schema version. Never edit a shipped entry; append a new one.
        private static readonly string[] Migrations =
        {
            // 1: Phase 2 recordings cache.
            @"
CREATE TABLE IF NOT EXISTS AudibleProducts (
    Marketplace    TEXT NOT NULL,
    Asin           TEXT NOT NULL,
    Status         INTEGER NOT NULL,
    Title          TEXT,
    Subtitle       TEXT,
    FormatType     TEXT,
    RuntimeMinutes INTEGER,
    Narrators      TEXT,
    Authors        TEXT,
    Publisher      TEXT,
    ReleaseDate    TEXT,
    Language       TEXT,
    Source         TEXT NOT NULL,
    FetchedAt      TEXT NOT NULL,
    PRIMARY KEY (Marketplace, Asin)
);

CREATE TABLE IF NOT EXISTS EditionAudibleLinks (
    ForeignEditionId TEXT NOT NULL,
    Marketplace      TEXT NOT NULL,
    Asin             TEXT,
    Method           TEXT NOT NULL,
    Confidence       REAL NOT NULL,
    EditionIdHint    INTEGER,
    CheckedAt        TEXT NOT NULL,
    PRIMARY KEY (ForeignEditionId, Marketplace)
);

CREATE INDEX IF NOT EXISTS IX_EditionAudibleLinks_Asin ON EditionAudibleLinks (Marketplace, Asin);

CREATE TABLE IF NOT EXISTS RecordingRequests (
    Id               INTEGER PRIMARY KEY,
    BaseBookId       TEXT NOT NULL,
    CopyBookIdHint   INTEGER,
    ForeignEditionId TEXT,
    Kind             TEXT NOT NULL,
    RequestedBy      TEXT,
    ExternalRef      TEXT,
    CreatedAt        TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_RecordingRequests_BaseBookId ON RecordingRequests (BaseBookId);
",

            // 2: Audiobookshelf library awareness (Talearr/Audiobookshelf). Rows are keyed by the
            // connection (Notifications row id) and the ABS library item id; a sync replaces them.
            @"
CREATE TABLE IF NOT EXISTS AbsItems (
    ConnectionId    INTEGER NOT NULL,
    ItemId          TEXT NOT NULL,
    LibraryId       TEXT NOT NULL,
    Path            TEXT,
    LocalPath       TEXT,
    Title           TEXT,
    Subtitle        TEXT,
    Authors         TEXT,
    Narrators       TEXT,
    Asin            TEXT,
    Isbn            TEXT,
    DurationSeconds REAL,
    Abridged        INTEGER NOT NULL DEFAULT 0,
    HasAudio        INTEGER NOT NULL DEFAULT 0,
    HasEbook        INTEGER NOT NULL DEFAULT 0,
    EbookFormat     TEXT,
    UpdatedAt       TEXT,
    SyncedAt        TEXT NOT NULL,
    PRIMARY KEY (ConnectionId, ItemId)
);

CREATE TABLE IF NOT EXISTS AbsItemBooks (
    ConnectionId INTEGER NOT NULL,
    ItemId       TEXT NOT NULL,
    BookId       INTEGER NOT NULL,
    Method       TEXT NOT NULL,
    Managed      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (ConnectionId, ItemId, BookId)
);

CREATE INDEX IF NOT EXISTS IX_AbsItemBooks_BookId ON AbsItemBooks (BookId);

CREATE TABLE IF NOT EXISTS AbsSyncState (
    ConnectionId INTEGER PRIMARY KEY,
    LastSyncAt   TEXT,
    LastError    TEXT,
    ItemCount    INTEGER NOT NULL DEFAULT 0,
    MatchedCount INTEGER NOT NULL DEFAULT 0
);
",

            // 3: per-book abridged policy overrides (Talearr/GrabPolicy/TalearrBookGrabPolicyStore).
            // Keyed by the work (the audiobook row's provider BaseBookId, the key recordings use),
            // so an override survives refresh and row churn. Losing a row only means the book
            // follows the global grab policy again.
            @"
CREATE TABLE IF NOT EXISTS BookGrabPolicies (
    WorkKey          TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
    AbridgedPolicy   TEXT NOT NULL,
    BookIdHint       INTEGER,
    ForeignEditionId TEXT,
    Title            TEXT,
    UpdatedAt        TEXT NOT NULL
);
"
        };

        public static int CurrentSchemaVersion => Migrations.Length;

        private readonly Logger _logger;
        private readonly object _lock = new object();
        private readonly string _connectionString;
        private bool _initialized;

        public TalearrDatabase(IAppFolderInfo appFolderInfo, Logger logger)
        {
            _logger = logger;

            // Next to chaptarr.db, wherever the app data folder is.
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
                Cache = SqliteCacheMode.Private,

                // No pooling, so Reset can always delete the file. Volumes here are tiny.
                Pooling = false
            }.ToString();
        }

        public string DatabasePath { get; }

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

        public void Reset(string reason)
        {
            lock (_lock)
            {
                _logger.Warn("TaleArr: recreating {0} ({1}). It holds cached data (Audible facts, the Audiobookshelf library), which is fetched again, and per-book abridged overrides, which fall back to the global grab policy.", FileName, reason);
                MoveAside();
                _initialized = false;
                InitializeCore(allowRecreate: false);
            }
        }

        public bool IsCorruption(Exception ex)
        {
            for (var current = ex; current != null; current = current.InnerException)
            {
                if (current is SqliteException sqlite &&
                    (sqlite.SqliteErrorCode == SqliteCorrupt || sqlite.SqliteErrorCode == SqliteNotADatabase))
                {
                    return true;
                }
            }

            return false;
        }

        private void EnsureInitialized()
        {
            if (_initialized && File.Exists(DatabasePath))
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized && File.Exists(DatabasePath))
                {
                    return;
                }

                InitializeCore(allowRecreate: true);
            }
        }

        private void InitializeCore(bool allowRecreate)
        {
            try
            {
                using var connection = OpenRaw();

                var check = connection.ExecuteScalar<string>("PRAGMA quick_check(1);");
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    throw new TalearrDatabaseUnusableException($"integrity check returned '{check}'");
                }

                var version = connection.ExecuteScalar<int>("PRAGMA user_version;");
                if (version > CurrentSchemaVersion)
                {
                    throw new TalearrDatabaseUnusableException($"schema version {version} is newer than this TaleArr ({CurrentSchemaVersion})");
                }

                for (var next = version; next < CurrentSchemaVersion; next++)
                {
                    using var transaction = connection.BeginTransaction();
                    connection.Execute(Migrations[next], transaction: transaction);
                    connection.Execute($"PRAGMA user_version = {next + 1};", transaction: transaction);
                    transaction.Commit();

                    _logger.Debug("TaleArr: {0} schema upgraded to version {1}", FileName, next + 1);
                }

                _initialized = true;
            }
            catch (Exception ex) when (allowRecreate && (ex is TalearrDatabaseUnusableException || IsCorruption(ex)))
            {
                _logger.Warn("TaleArr: {0} is unusable ({1}); recreating it. It holds cached data (Audible facts, the Audiobookshelf library) and per-book abridged overrides (books fall back to the global grab policy).", FileName, ex.Message);
                MoveAside();
                InitializeCore(allowRecreate: false);
            }
        }

        private SqliteConnection OpenRaw()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=3000;";
                command.ExecuteNonQuery();
            }

            return connection;
        }

        private void MoveAside()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                if (File.Exists(DatabasePath))
                {
                    var aside = DatabasePath + ".broken";
                    if (File.Exists(aside))
                    {
                        File.Delete(aside);
                    }

                    File.Move(DatabasePath, aside);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not move {0} aside, deleting it", FileName);
                TryDelete(DatabasePath);
            }

            TryDelete(DatabasePath + "-wal");
            TryDelete(DatabasePath + "-shm");
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not delete {0}", path);
            }
        }
    }

    public class TalearrDatabaseUnusableException : Exception
    {
        public TalearrDatabaseUnusableException(string message)
            : base(message)
        {
        }
    }
}
