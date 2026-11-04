using System;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Configuration
{
    [TestFixture]
    public class ConfigRepositoryFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        private string _databasePath;
        private ConfigRepository _subject;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _databasePath = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"config_repository_{Guid.NewGuid():N}.db");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                connection.Execute(@"CREATE TABLE ""Config"" (""Id"" INTEGER PRIMARY KEY AUTOINCREMENT, ""Key"" TEXT NOT NULL UNIQUE, ""Value"" TEXT NOT NULL);");
            }

            var database = new Database("main", () =>
            {
                var conn = new SqliteConnection(connectionString);
                conn.Open();
                return conn;
            });

            _subject = new ConfigRepository(new MainDatabase(database), new StubEventAggregator());
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(_databasePath))
            {
                File.Delete(_databasePath);
            }
        }

        // Talearr issue #1: a mixed-case key was stored in lower case but looked up as written, so
        // the second write tried to insert it again and failed on the unique key.
        [Test]
        public void should_find_and_update_a_mixed_case_key()
        {
            _subject.Upsert("TalearrReadOnlyRootFolders", "[\"/audiobooks/\"]");

            Assert.That(_subject.Get("TalearrReadOnlyRootFolders")?.Value, Is.EqualTo("[\"/audiobooks/\"]"));

            Assert.DoesNotThrow(() => _subject.Upsert("TalearrReadOnlyRootFolders", "[]"));
            Assert.That(_subject.Get("TalearrReadOnlyRootFolders")?.Value, Is.EqualTo("[]"));
            Assert.That(_subject.All(), Has.Exactly(1).Items);
        }
    }
}
