using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Talearr.Catalog;
using NzbDrone.Core.Talearr.Recordings;
using Chaptarr.Core.Test.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    [TestFixture]
    [NonParallelizable]
    public class TalearrMergeRedirectFixture
    {
        private TalearrMigratedDatabase _db;
        private TalearrMergeRedirectStore _store;
        private TalearrMergeService _subject;

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
            _db = new TalearrMigratedDatabase("talearr_merge_redirect");
            var sidecar = new TalearrDatabase(new TestAppFolderInfo(_db.Folder), LogManager.GetLogger("TalearrMergeRedirectFixture"));
            _store = new TalearrMergeRedirectStore(sidecar, LogManager.GetLogger("TalearrMergeRedirectFixture"));
            _subject = new TalearrMergeService(_db.MainDatabase, new TalearrRecordingEventAggregator(), LogManager.GetLogger("TalearrMergeRedirectFixture"), _store);

            using var conn = _db.Open();
            _db.Insert(conn, "Authors", new { Id = 1, Name = "Riley Sager", CleanName = "rileysager", Path = "/books/Riley Sager", HardcoverAuthorId = "hc:100" });
            _db.Insert(conn, "Authors", new { Id = 2, Name = "Sager, Riley", CleanName = "sagerriley", Path = "/books/Sager, Riley", GoodreadsAuthorId = "gr:200" });
            _db.Insert(conn, "Books", new { Id = 10, AuthorId = 1, Title = "The Only One Left", CleanTitle = "theonlyoneleft", MediaType = 0, HardcoverBookId = "hc:584720", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 11, AuthorId = 1, Title = "The Only One Left", CleanTitle = "theonlyoneleft", MediaType = 0, HardcoverBookId = "hc:999", GoodreadsWorkId = "gr:303372801", AudibleASIN = "B0C1234567", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 12, AuthorId = 1, Title = "Home Before Dark", CleanTitle = "homebeforedark", MediaType = 0, HardcoverBookId = "hc:1", AnyEditionOk = true });
            _db.Insert(conn, "Editions", new { Id = 100, BookId = 10, Title = "The Only One Left", ForeignEditionId = "e1", Monitored = true });
            _db.Insert(conn, "Editions", new { Id = 110, BookId = 11, Title = "The Only One Left", ForeignEditionId = "e2", Monitored = true });
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            _db.Dispose();
        }

        private static Book Local(int id, string hc, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            return new Book { Id = id, Title = "Local " + id, HardcoverBookId = hc, MediaType = mediaType, Editions = new List<Edition>() };
        }

        private static Book Remote(string title, string hc, string gr = null, BookMediaType mediaType = BookMediaType.Audiobook)
        {
            return new Book { Title = title, HardcoverBookId = hc, GoodreadsWorkId = gr, MediaType = mediaType, Editions = new List<Edition>() };
        }

        [Test]
        public void merging_books_records_the_source_ids_the_target_does_not_have()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });

            var redirects = _store.GetBookRedirects();

            Assert.Multiple(() =>
            {
                Assert.That(_store.FindBook("hc:999", 0), Is.EqualTo(10));
                Assert.That(_store.FindBook("gr:303372801", 0), Is.EqualTo(10));
                Assert.That(_store.FindBook("az:B0C1234567", 0), Is.EqualTo(10), "book-level ASIN is an edition redirect");
                Assert.That(_store.FindBook("hc:999", 1), Is.Null, "redirects are per media type");
                Assert.That(_store.FindBook("hc:584720", 0), Is.Null, "the target's own id is not a redirect");
                Assert.That(redirects.Select(r => r.SourceId).Distinct(), Is.EqualTo(new int?[] { 11 }));
            });
        }

        [Test]
        public void a_dry_run_records_nothing()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 }, DryRun = true });

            Assert.That(_store.GetBookRedirects(), Is.Empty);
        }

        [Test]
        public void merging_authors_records_author_ids()
        {
            _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 2 }, Force = true });

            Assert.That(_store.FindAuthor("gr:200"), Is.EqualTo(1));
            Assert.That(_store.FindAuthor("hc:100"), Is.Null);
        }

        [Test]
        public void a_chain_of_merges_ends_at_the_last_target()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 12, SourceIds = { 10 }, Force = true });

            Assert.Multiple(() =>
            {
                Assert.That(_store.FindBook("hc:999", 0), Is.EqualTo(12));
                Assert.That(_store.FindBook("hc:584720", 0), Is.EqualTo(12));
            });
        }

        [Test]
        public void refresh_after_merge_does_not_recreate_the_row_when_the_server_still_lists_both()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });

            var local = new List<Book> { Local(10, "hc:584720"), Local(12, "hc:1") };
            var kept = Remote("The Only One Left", "hc:584720");
            var merged = Remote("The Only One Left (Audible)", "hc:999", "gr:303372801");
            var other = Remote("Home Before Dark", "hc:1");
            var remote = new List<Book> { kept, merged, other };

            var changed = TalearrMergeRedirects.Apply(local, remote, _store.GetBookRedirects());

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.EqualTo(1));
                Assert.That(remote, Is.EqualTo(new[] { kept, other }), "the merged-away work is not added again");
            });
        }

        [Test]
        public void refresh_after_merge_with_only_the_old_id_updates_the_kept_row()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });

            var local = new List<Book> { Local(10, "hc:584720") };
            var merged = Remote("The Only One Left", "hc:999");
            var remote = new List<Book> { merged };

            TalearrMergeRedirects.Apply(local, remote, _store.GetBookRedirects());

            // The refresh's own matcher (RefreshAuthorService.GetMatchingExistingChildren) now pairs it with row 10.
            var match = BookIdentity.FindWorkFirstMatchResult(local, merged);
            Assert.Multiple(() =>
            {
                Assert.That(remote, Has.Count.EqualTo(1));
                Assert.That(match.Matches.Select(b => b.Id), Is.EqualTo(new[] { 10 }));
            });
        }

        [Test]
        public void redirects_never_cross_media_types_or_touch_unrelated_books()
        {
            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });

            var local = new List<Book> { Local(10, "hc:584720") };
            var ebook = Remote("The Only One Left", "hc:999", mediaType: BookMediaType.Ebook);
            var unrelated = Remote("Lock Every Door", "hc:777");
            var remote = new List<Book> { ebook, unrelated };

            var changed = TalearrMergeRedirects.Apply(local, remote, _store.GetBookRedirects());

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.Zero);
                Assert.That(remote, Has.Count.EqualTo(2));
                Assert.That(ebook.RemoteProviderIds, Is.Null);
            });
        }

        [Test]
        public void a_broken_sidecar_means_no_redirects()
        {
            var store = new TalearrMergeRedirectStore(new BrokenDatabase(), LogManager.GetLogger("TalearrMergeRedirectFixture"));

            Assert.Multiple(() =>
            {
                Assert.That(store.GetBookRedirects(), Is.Empty);
                Assert.That(store.FindAuthor("gr:200"), Is.Null);
                Assert.That(store.Record(TalearrMergeRedirect.Book, 11, 10, new[] { new TalearrMergeRedirect { EntityType = TalearrMergeRedirect.Book, ProviderId = "hc:1" } }), Is.False);
            });
        }

        private sealed class BrokenDatabase : ITalearrDatabase
        {
            public string DatabasePath => "broken";
            public int SchemaVersion => 0;
            public System.Data.IDbConnection OpenConnection() => throw new System.IO.IOException("disk gone");
            public void Reset(string reason)
            {
            }

            public bool IsCorruption(System.Exception ex) => false;
        }
    }
}
