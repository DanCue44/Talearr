using System;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrAbsStoreFixture
    {
        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrAbsStore _store;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrAbsStore(_database, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private static TalearrAbsSyncResult Result(int connectionId, params string[] itemIds)
        {
            var result = new TalearrAbsSyncResult { ConnectionId = connectionId };
            foreach (var id in itemIds)
            {
                result.Items.Add(new TalearrAbsItem
                {
                    ItemId = id,
                    LibraryId = "lib",
                    Path = "/books/" + id,
                    LocalPath = "/ebooks/" + id,
                    Title = "Title " + id,
                    Narrators = "Ralph Cosham",
                    Asin = "B0000545BA",
                    DurationSeconds = 10040.5,
                    Abridged = AbridgedMarker.Abridged,
                    HasAudio = true,
                    UpdatedAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
                    SyncedAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)
                });
                result.Matches.Add(new TalearrAbsItemBook { ItemId = id, BookId = 100, Method = TalearrAbsMatchMethods.Asin });
            }

            return result;
        }

        [Test]
        public void should_create_the_audiobookshelf_tables_in_talearr_db_version_2()
        {
            Assert.That(_database.SchemaVersion, Is.EqualTo(2));
            using var connection = _database.OpenConnection();
            var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToList();
            Assert.That(tables, Does.Contain("AbsItems").And.Contain("AbsItemBooks").And.Contain("AbsSyncState"));
        }

        [Test]
        public void should_round_trip_items_matches_and_state()
        {
            _store.ReplaceConnection(Result(1, "a"), new TalearrAbsSyncState { ConnectionId = 1, LastSyncAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc), ItemCount = 1, MatchedCount = 1 });

            var item = _store.GetItems().Single();
            var match = _store.GetMatches().Single();
            var state = _store.GetStates().Single();

            Assert.Multiple(() =>
            {
                Assert.That(item.ConnectionId, Is.EqualTo(1));
                Assert.That(item.LocalPath, Is.EqualTo("/ebooks/a"));
                Assert.That(item.Abridged, Is.EqualTo(AbridgedMarker.Abridged));
                Assert.That(item.DurationSeconds, Is.EqualTo(10040.5));
                Assert.That(item.HasAudio, Is.True);
                Assert.That(item.HasEbook, Is.False);
                Assert.That(item.UpdatedAt, Is.EqualTo(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc)));
                Assert.That(match.BookId, Is.EqualTo(100));
                Assert.That(match.Method, Is.EqualTo("asin"));
                Assert.That(match.Managed, Is.False);
                Assert.That(state.LastSyncAt, Is.EqualTo(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)));
                Assert.That(state.ItemCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void should_replace_only_the_synced_connection()
        {
            _store.ReplaceConnection(Result(1, "a", "b"), null);
            _store.ReplaceConnection(Result(2, "c"), null);
            _store.ReplaceConnection(Result(1, "d"), null);

            Assert.That(_store.GetItems().Select(i => $"{i.ConnectionId}:{i.ItemId}"), Is.EquivalentTo(new[] { "1:d", "2:c" }));
            Assert.That(_store.GetMatches().Count, Is.EqualTo(2));
        }

        [Test]
        public void should_remove_connections_that_are_gone()
        {
            _store.ReplaceConnection(Result(1, "a"), new TalearrAbsSyncState { ConnectionId = 1 });
            _store.ReplaceConnection(Result(2, "b"), new TalearrAbsSyncState { ConnectionId = 2 });

            _store.RemoveConnectionsExcept(new[] { 2 });
            Assert.That(_store.GetItems().Select(i => i.ConnectionId).Distinct(), Is.EqualTo(new[] { 2 }));
            Assert.That(_store.GetStates().Select(s => s.ConnectionId), Is.EqualTo(new[] { 2 }));

            _store.RemoveConnectionsExcept(Array.Empty<int>());
            Assert.That(_store.GetItems(), Is.Empty);
            Assert.That(_store.GetMatches(), Is.Empty);
        }

        [Test]
        public void should_drop_matches_for_items_not_in_the_result_and_duplicate_items()
        {
            var result = Result(1, "a");
            result.Items.Add(result.Items[0]);
            result.Matches.Add(new TalearrAbsItemBook { ItemId = "ghost", BookId = 5, Method = "title" });

            _store.ReplaceConnection(result, null);

            Assert.That(_store.GetItems().Count, Is.EqualTo(1));
            Assert.That(_store.GetMatches().Select(m => m.ItemId), Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void should_read_nothing_instead_of_failing_when_talearr_db_is_unusable()
        {
            var broken = new TalearrAbsStore(new BrokenDatabase(), LogManager.GetCurrentClassLogger());

            Assert.DoesNotThrow(() => broken.ReplaceConnection(Result(1, "a"), null));
            Assert.That(broken.GetItems(), Is.Empty);
            Assert.That(broken.GetMatches(), Is.Empty);
        }

        private sealed class BrokenDatabase : ITalearrDatabase
        {
            public string DatabasePath => "/nowhere/talearr.db";
            public int SchemaVersion => 0;

            public System.Data.IDbConnection OpenConnection()
            {
                throw new InvalidOperationException("disk gone");
            }

            public void Reset(string reason)
            {
            }

            public bool IsCorruption(Exception ex)
            {
                return false;
            }
        }
    }
}
