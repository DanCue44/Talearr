using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Talearr.Audiobookshelf;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    // Drop-in guard for Audiobookshelf awareness:
    //   - the connection is upstream's own AudioBookShelf row; TaleArr only adds keys to its
    //     Settings JSON, which stock Chaptarr's System.Text.Json read skips;
    //   - the library cache lives in talearr.db; chaptarr.db keeps its schema and migration version.
    // A provider implementation Chaptarr does not know would instead be deleted by its
    // ProviderFactory.RemoveMissingImplementations on the first start after a rollback, which
    // is why TaleArr extends the existing connection rather than adding a new one.
    [TestFixture]
    public class TalearrAbsRollbackFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        // Upstream Chaptarr's AudioBookShelfSettings as it is without TaleArr: same public
        // properties, none of the talearr* ones.
        private sealed class StockAudioBookShelfSettings
        {
            public string ServerUrl { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public bool UseSsl { get; set; }
            public string UrlBase { get; set; }
            public string ApiKey { get; set; }
            public string AudiobookLibraryId { get; set; }
            public string EbookLibraryId { get; set; }
            public string LibraryMappingsJson { get; set; }
            public string SignIn { get; set; }
            public string LibraryId { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
        }

        // The options upstream ProviderRepository reads and writes provider Settings with
        // (src/NzbDrone.Core/ThingiProvider/ProviderRepository.cs).
        private static JsonSerializerOptions ProviderRepositoryOptions()
        {
            var options = new JsonSerializerOptions
            {
                AllowTrailingCommas = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNameCaseInsensitive = true,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, true));
            options.Converters.Add(new STJTimeSpanConverter());
            options.Converters.Add(new STJUtcConverter());
            return options;
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
        public void stock_chaptarr_should_read_a_connection_saved_by_taleArr()
        {
            var settings = new AudioBookShelfSettings
            {
                Host = "audiobookshelf",
                Port = 13378,
                ApiKey = "abs-key",
                TalearrSyncLibrary = true,
                TalearrSkipOwned = false,
                TalearrLinkUrl = "http://192.168.1.10:13378"
            };

            var json = JsonSerializer.Serialize(settings, settings.GetType(), ProviderRepositoryOptions());
            Assert.That(json, Does.Contain("\"talearrSyncLibrary\"").And.Contain("\"talearrSkipOwned\"").And.Contain("\"talearrLinkUrl\""));

            var stock = JsonSerializer.Deserialize<StockAudioBookShelfSettings>(json, ProviderRepositoryOptions());

            Assert.Multiple(() =>
            {
                Assert.That(stock.Host, Is.EqualTo("audiobookshelf"));
                Assert.That(stock.Port, Is.EqualTo(13378));
                Assert.That(stock.ApiKey, Is.EqualTo("abs-key"));
            });

            // And Chaptarr writing it back only drops the TaleArr keys.
            var rewritten = JsonSerializer.Serialize(stock, ProviderRepositoryOptions());
            Assert.That(rewritten, Does.Not.Contain("talearr"));
        }

        [Test]
        public void taleArr_should_default_to_on_for_a_connection_saved_by_stock_chaptarr()
        {
            const string chaptarrJson = "{ \"host\": \"audiobookshelf\", \"port\": 13378, \"useSsl\": false, \"apiKey\": \"abs-key\" }";

            var settings = JsonSerializer.Deserialize<AudioBookShelfSettings>(chaptarrJson, ProviderRepositoryOptions());

            Assert.Multiple(() =>
            {
                Assert.That(settings.TalearrSyncLibrary, Is.True);
                Assert.That(settings.TalearrSkipOwned, Is.True);
                Assert.That(settings.TalearrLinkUrl, Is.Null);
                Assert.That(settings.GetTalearrWebBaseUrl(), Is.EqualTo("http://audiobookshelf:13378"));
            });
        }

        [Test]
        public void should_keep_the_chaptarr_schema_and_store_the_connection_in_the_upstream_row()
        {
            using var folder = new TalearrTestFolder();
            var chaptarrDb = Path.Combine(folder.Folder, "chaptarr.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = chaptarrDb,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            new MigrationController(LogManager.GetLogger("TalearrAbsRollbackFixture"), null)
                .Migrate(connectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            var schemaBefore = Schema(connectionString);
            var versionBefore = MigrationVersion(connectionString);

            var database = new Database("main", () =>
            {
                var connection = new SqliteConnection(connectionString);
                connection.Open();
                return connection;
            });

            var notifications = new NotificationRepository(new MainDatabase(database), new StubEventAggregator());
            notifications.Insert(new NotificationDefinition
            {
                Name = "Audiobookshelf",
                Implementation = "AudioBookShelf",
                ConfigContract = nameof(AudioBookShelfSettings),
                OnReleaseImport = true,
                Settings = new AudioBookShelfSettings { Host = "audiobookshelf", Port = 13378, ApiKey = "abs-key", TalearrSkipOwned = false }
            });

            var store = new TalearrAbsStore(folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            var result = new TalearrAbsSyncResult { ConnectionId = 1 };
            result.Items.Add(new TalearrAbsItem { ItemId = "li-1", LibraryId = "lib", Title = "The Firm", HasAudio = true, SyncedAt = DateTime.UtcNow });
            result.Matches.Add(new TalearrAbsItemBook { ItemId = "li-1", BookId = 3401, Method = "title" });
            store.ReplaceConnection(result, new TalearrAbsSyncState { ConnectionId = 1, LastSyncAt = DateTime.UtcNow, ItemCount = 1 });

            var readBack = (AudioBookShelfSettings)notifications.All().Single().Settings;

            using var raw = new SqliteConnection(connectionString);
            raw.Open();
            var stored = raw.ExecuteScalar<string>("SELECT \"Settings\" FROM \"Notifications\"");
            var tablesWithAbs = raw.Query<string>("SELECT name FROM sqlite_master WHERE name IN ('AbsItems', 'AbsItemBooks', 'AbsSyncState')").ToList();

            Assert.Multiple(() =>
            {
                Assert.That(Schema(connectionString), Is.EqualTo(schemaBefore), "chaptarr.db schema must not change");
                Assert.That(MigrationVersion(connectionString), Is.EqualTo(versionBefore), "no TaleArr migration versions");
                Assert.That(tablesWithAbs, Is.Empty, "the library cache is not in chaptarr.db");
                Assert.That(stored, Does.Contain("\"talearrSkipOwned\": false"));
                Assert.That(readBack.TalearrSkipOwned, Is.False);
                Assert.That(readBack.TalearrSyncLibrary, Is.True);
                Assert.That(store.GetItems().Single().ItemId, Is.EqualTo("li-1"));
            });
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
