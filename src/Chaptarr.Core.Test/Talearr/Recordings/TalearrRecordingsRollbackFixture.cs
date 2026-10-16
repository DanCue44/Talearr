using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Drop-in guard: everything the recordings feature stores goes to talearr.db or to
    // talearr.* Config rows. A chaptarr.db built by the real migrations keeps exactly the same
    // schema and migration version, so stock Chaptarr starts on it after a rollback.
    [TestFixture]
    public class TalearrRecordingsRollbackFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [Test]
        public void should_leave_the_chaptarr_schema_and_migration_version_unchanged()
        {
            using var folder = new TalearrTestFolder();
            var chaptarrDb = Path.Combine(folder.Folder, "chaptarr.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = chaptarrDb,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            new MigrationController(LogManager.GetLogger("TalearrRollbackFixture"), null)
                .Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            var schemaBefore = Schema(connectionString);
            var versionBefore = MigrationVersion(connectionString);
            Assert.That(schemaBefore, Does.Contain("CREATE TABLE \"Config\""), "the real schema was built");

            // TaleArr writes: settings into Config, cache rows into talearr.db.
            var database = new Database("main", () =>
            {
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            });
            var configRepository = new ConfigRepository(new MainDatabase(database), new StubEventAggregator());
            new TalearrRecordingsSettingsService(configRepository, LogManager.GetCurrentClassLogger())
                .Save(new TalearrRecordingsSettings { Marketplace = "uk", AudnexusFallback = true });

            var talearrDatabase = folder.CreateDatabase();
            var store = folder.CreateStore(talearrDatabase);
            store.UpsertProducts(new[]
            {
                new AudibleProduct { Marketplace = "us", Asin = "B002V5BSLC", Status = AudibleProductStatus.Ok, Title = "The Firm", FetchedAt = DateTime.UtcNow }
            });
            store.UpsertLink(new EditionAudibleLink { ForeignEditionId = "hc:edition:30583648-audiobook", Marketplace = "us", Asin = "B002V5BSLC", Confidence = 1, CheckedAt = DateTime.UtcNow });
            store.AddRequest(new RecordingRequest { BaseBookId = "hc:378630", Kind = "unabridged" });

            Assert.Multiple(() =>
            {
                Assert.That(Schema(connectionString), Is.EqualTo(schemaBefore), "chaptarr.db schema must not change");
                Assert.That(MigrationVersion(connectionString), Is.EqualTo(versionBefore), "no TaleArr migration versions");
                Assert.That(talearrDatabase.DatabasePath, Is.EqualTo(Path.Combine(folder.Folder, "talearr.db")));
                Assert.That(File.Exists(talearrDatabase.DatabasePath), Is.True);

                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                var keys = connection.Query<string>("SELECT \"Key\" FROM \"Config\" WHERE \"Key\" LIKE 'talearr.%'").ToList();
                Assert.That(keys, Is.Not.Empty);
                Assert.That(keys.All(k => k.StartsWith("talearr.recordings.")), Is.True);
            });

            // Deleting talearr.db (a rollback cleanup) costs nothing but a refetch.
            SqliteConnection.ClearAllPools();
            File.Delete(talearrDatabase.DatabasePath);
            File.Delete(talearrDatabase.DatabasePath + "-wal");
            File.Delete(talearrDatabase.DatabasePath + "-shm");
            Assert.That(store.GetProduct("us", "B002V5BSLC"), Is.Null);
            Assert.That(Schema(connectionString), Is.EqualTo(schemaBefore));
        }

        private static string Schema(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return string.Join("\n", connection.Query<string>(
                "SELECT type || ' ' || name || ' ' || COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name"));
        }

        private static long MigrationVersion(string connectionString)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection.ExecuteScalar<long>("SELECT MAX(\"Version\") FROM \"VersionInfo\"");
        }
    }
}
