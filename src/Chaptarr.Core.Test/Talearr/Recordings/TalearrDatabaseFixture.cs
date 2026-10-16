using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrDatabaseFixture
    {
        private TalearrTestFolder _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void should_create_talearr_db_next_to_chaptarr_db_on_first_use()
        {
            var database = _folder.CreateDatabase();

            Assert.That(File.Exists(_folder.TalearrDbPath), Is.False, "nothing is created before first use");

            using (var connection = database.OpenConnection())
            {
                var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").ToList();
                Assert.That(tables, Is.EquivalentTo(new[] { "AbsItemBooks", "AbsItems", "AbsSyncState", "AudibleProducts", "EditionAudibleLinks", "RecordingRequests" }));
                Assert.That(connection.ExecuteScalar<string>("PRAGMA journal_mode;"), Is.EqualTo("wal"));
            }

            Assert.That(database.DatabasePath, Is.EqualTo(_folder.TalearrDbPath));
            Assert.That(database.SchemaVersion, Is.EqualTo(TalearrDatabase.CurrentSchemaVersion));
            Assert.That(File.Exists(Path.Combine(_folder.Folder, "chaptarr.db")), Is.False, "TaleArr must not create chaptarr.db");
        }

        [Test]
        public void should_upgrade_a_version_zero_file_in_place_and_keep_unrelated_data()
        {
            using (var connection = new SqliteConnection($"Data Source={_folder.TalearrDbPath};Pooling=False"))
            {
                connection.Open();
                connection.Execute("CREATE TABLE Keep (Value TEXT); INSERT INTO Keep VALUES ('still here');");
            }

            var database = _folder.CreateDatabase();

            using (var connection = database.OpenConnection())
            {
                Assert.That(connection.ExecuteScalar<string>("SELECT Value FROM Keep"), Is.EqualTo("still here"));
                Assert.That(connection.ExecuteScalar<int>("PRAGMA user_version;"), Is.EqualTo(TalearrDatabase.CurrentSchemaVersion));
            }
        }

        [Test]
        public void should_be_idempotent_across_instances()
        {
            var store = _folder.CreateStore();
            store.UpsertProducts(new[] { Product("us", "B002V5BSLC") });

            var reopened = _folder.CreateStore();

            Assert.That(reopened.GetProduct("us", "B002V5BSLC"), Is.Not.Null);
        }

        [Test]
        public void should_recreate_a_corrupt_file()
        {
            File.WriteAllBytes(_folder.TalearrDbPath, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());

            var store = _folder.CreateStore();
            store.UpsertProducts(new[] { Product("us", "B002V5BSLC") });

            Assert.That(store.GetProduct("us", "B002V5BSLC"), Is.Not.Null);
            Assert.That(File.Exists(_folder.TalearrDbPath + ".broken"), Is.True, "the broken file is kept aside once");
        }

        [Test]
        public void should_recreate_a_file_from_a_newer_talearr()
        {
            using (var connection = new SqliteConnection($"Data Source={_folder.TalearrDbPath};Pooling=False"))
            {
                connection.Open();
                connection.Execute("CREATE TABLE AudibleProducts (Something TEXT); PRAGMA user_version = 99;");
            }

            var database = _folder.CreateDatabase();

            Assert.That(database.SchemaVersion, Is.EqualTo(TalearrDatabase.CurrentSchemaVersion));
            _folder.CreateStore(database).UpsertProducts(new[] { Product("us", "B002V5BSLC") });
            Assert.That(_folder.CreateStore(database).GetProduct("us", "B002V5BSLC"), Is.Not.Null);
        }

        [Test]
        public void should_recreate_the_file_when_it_is_deleted_while_running()
        {
            var database = _folder.CreateDatabase();
            var store = _folder.CreateStore(database);
            store.UpsertProducts(new[] { Product("us", "B002V5BSLC") });

            SqliteConnection.ClearAllPools();
            File.Delete(_folder.TalearrDbPath);
            File.Delete(_folder.TalearrDbPath + "-wal");
            File.Delete(_folder.TalearrDbPath + "-shm");

            Assert.That(store.GetProduct("us", "B002V5BSLC"), Is.Null, "a lost cache is a cache miss, not an error");

            store.UpsertProducts(new[] { Product("us", "B002V5BSLC") });
            Assert.That(store.GetProduct("us", "B002V5BSLC"), Is.Not.Null);
        }

        [Test]
        public void should_treat_corruption_found_later_as_a_cache_miss_and_recover()
        {
            var database = _folder.CreateDatabase();
            var store = _folder.CreateStore(database);
            store.UpsertProducts(new[] { Product("us", "B002V5BSLC") });

            SqliteConnection.ClearAllPools();
            File.Delete(_folder.TalearrDbPath + "-wal");
            File.Delete(_folder.TalearrDbPath + "-shm");
            File.WriteAllBytes(_folder.TalearrDbPath, new byte[8192]);

            Assert.DoesNotThrow(() => store.GetProduct("us", "B002V5BSLC"));

            store.UpsertProducts(new[] { Product("us", "B00NIXU8GW") });
            Assert.That(store.GetProduct("us", "B00NIXU8GW"), Is.Not.Null);
        }

        private static AudibleProduct Product(string marketplace, string asin)
        {
            return new AudibleProduct
            {
                Marketplace = marketplace,
                Asin = asin,
                Status = AudibleProductStatus.Ok,
                Title = "The Firm",
                FetchedAt = DateTime.UtcNow
            };
        }
    }
}
