using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    [TestFixture]
    [NonParallelizable]
    public class TalearrMergeFixture
    {
        // Answers GetAuthor with one stored author; nothing else is called by the merge.
        public class AuthorServiceProxy : DispatchProxy
        {
            public Author Author { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return targetMethod?.Name == nameof(IAuthorService.GetAuthor) && (int)args[0] == Author?.Id ? Author : null;
            }
        }

        private TalearrMigratedDatabase _db;
        private TalearrRecordingEventAggregator _events;
        private TalearrMergeService _subject;
        private string _fileA;
        private string _fileB;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            // Production type handlers (List<int> as JSON etc.) must be active, as in the app.
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _db = new TalearrMigratedDatabase("talearr_merge");
            _events = new TalearrRecordingEventAggregator();
            _subject = new TalearrMergeService(_db.MainDatabase, _events, LogManager.GetLogger("TalearrMergeFixture"));

            _fileA = Path.Combine(_db.Folder, "Riley Sager", "The Only One Left", "part1.m4b");
            _fileB = Path.Combine(_db.Folder, "Sager, Riley", "The Only One Left", "part2.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(_fileA));
            Directory.CreateDirectory(Path.GetDirectoryName(_fileB));
            File.WriteAllText(_fileA, "a");
            File.WriteAllText(_fileB, "b");

            Seed();
        }

        [TearDown]
        public void TearDown()
        {
            _db.Dispose();
        }

        // Author 1 "Riley Sager" (hc:100) and author 2 "Sager, Riley" (gr:200) are one person.
        // Book 10 (author 1) and book 11 (author 1, second audiobook pass) are one work; 12 is the
        // ebook sibling of 10. Book 20 belongs to author 2.
        private void Seed()
        {
            using var conn = _db.Open();

            _db.Insert(conn, "Authors", new { Id = 1, Name = "Riley Sager", CleanName = "rileysager", Path = "/books/Riley Sager", AudiobookPath = "/books/Riley Sager", HardcoverAuthorId = "hc:100", AudiobookMonitored = true });
            _db.Insert(conn, "Authors", new { Id = 2, Name = "Sager, Riley", CleanName = "sagerriley", Path = "/books/Sager, Riley", EbookPath = "/ebooks/Sager, Riley", GoodreadsAuthorId = "gr:200", EbookMonitored = true });
            _db.Insert(conn, "Authors", new { Id = 3, Name = "Stephen King", CleanName = "stephenking", Path = "/books/Stephen King" });

            _db.Insert(conn, "Books", new { Id = 10, AuthorId = 1, Title = "The Only One Left", CleanTitle = "theonlyoneleft", MediaType = 0, AudiobookMonitored = false, EbookMonitored = false, HardcoverBookId = "hc:584720", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 11, AuthorId = 1, Title = "The Only One Left", CleanTitle = "theonlyoneleft", MediaType = 0, AudiobookMonitored = true, EbookMonitored = false, HardcoverBookId = "hc:999", GoodreadsWorkId = "gr:303372801", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 12, AuthorId = 1, Title = "The Only One Left", CleanTitle = "theonlyoneleft", MediaType = 1, EbookMonitored = true, AudiobookMonitored = false, HardcoverBookId = "hc:584720", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 13, AuthorId = 1, Title = "Home Before Dark", CleanTitle = "homebeforedark", MediaType = 0, AudiobookMonitored = false, EbookMonitored = false, HardcoverBookId = "hc:1", AnyEditionOk = true });
            _db.Insert(conn, "Books", new { Id = 20, AuthorId = 2, Title = "Survive the Night", CleanTitle = "survivethenight", MediaType = 0, AudiobookMonitored = true, EbookMonitored = false, AnyEditionOk = true });

            _db.Insert(conn, "Editions", new { Id = 100, BookId = 10, Title = "The Only One Left", ForeignEditionId = "e1", Monitored = true });
            _db.Insert(conn, "Editions", new { Id = 110, BookId = 11, Title = "The Only One Left", ForeignEditionId = "e1", Monitored = false });
            _db.Insert(conn, "Editions", new { Id = 111, BookId = 11, Title = "The Only One Left", ForeignEditionId = "e2", Monitored = true });
            _db.Insert(conn, "Editions", new { Id = 120, BookId = 12, Title = "The Only One Left", ForeignEditionId = "e3", Monitored = true });
            _db.Insert(conn, "Editions", new { Id = 200, BookId = 20, Title = "Survive the Night", ForeignEditionId = "e20", Monitored = true });

            _db.Insert(conn, "BookFiles", new { Id = 1, EditionId = 110, Path = _fileA, Size = 1 });
            _db.Insert(conn, "BookFiles", new { Id = 2, EditionId = 111, Path = _fileB, Size = 1 });
            _db.Insert(conn, "BookFiles", new { Id = 3, EditionId = 200, Path = "/books/Sager, Riley/Survive the Night/a.m4b", Size = 1 });

            _db.Insert(conn, "History", new { Id = 1, BookId = 11, AuthorId = 1, EditionId = 110, SourceTitle = "grab" });
            _db.Insert(conn, "History", new { Id = 2, BookId = 20, AuthorId = 2, EditionId = 200, SourceTitle = "grab" });
            _db.Insert(conn, "DownloadHistory", new { Id = 1, BookId = 11, AuthorId = 1, DownloadId = "abc", SourceTitle = "grab" });
            _db.Insert(conn, "PendingReleases", new { Id = 1, AuthorId = 2, Title = "Survive the Night", Release = "{}" });

            _db.Insert(conn, "Series", new { Id = 5, Title = "Standalone" });
            _db.Insert(conn, "Series", new { Id = 6, Title = "Other" });
            _db.Insert(conn, "SeriesBookLink", new { BookId = 10, SeriesId = 5 });
            _db.Insert(conn, "SeriesBookLink", new { BookId = 11, SeriesId = 5 });
            _db.Insert(conn, "SeriesBookLink", new { BookId = 11, SeriesId = 6 });
            _db.Insert(conn, "AuthorSeries", new { AuthorId = 1, SeriesId = 5 });
            _db.Insert(conn, "AuthorSeries", new { AuthorId = 2, SeriesId = 5 });
            _db.Insert(conn, "AuthorSeries", new { AuthorId = 2, SeriesId = 6 });

            _db.Insert(conn, "Blocklist", new { Id = 1, SourceTitle = "bad release", AuthorProviderIds = "[\"gr:200\"]", BookProviderIds = "[\"hc:999\"]" });
        }

        private T Scalar<T>(string sql, object param = null)
        {
            using var conn = _db.Open();
            return conn.ExecuteScalar<T>(sql, param);
        }

        private bool FtsIsHealthy()
        {
            using var conn = _db.Open();
            conn.Execute("INSERT INTO edition_fts(edition_fts) VALUES('integrity-check')");
            conn.Execute("INSERT INTO author_fts(author_fts) VALUES('integrity-check')");
            return true;
        }

        private string Snapshot()
        {
            using var conn = _db.Open();
            var tables = new[] { "Authors", "Books", "Editions", "BookFiles", "History", "DownloadHistory", "PendingReleases", "SeriesBookLink", "AuthorSeries", "Blocklist" };
            return string.Join("\n", tables.Select(t => t + ":" + string.Join(";", conn.Query($"SELECT * FROM \"{t}\" ORDER BY \"Id\"").Select(r => string.Join(",", ((System.Collections.Generic.IDictionary<string, object>)r).Values)))));
        }

        // ---- books ----

        [Test]
        public void merge_books_moves_editions_files_and_references_and_keeps_target_ids()
        {
            var report = _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });

            Assert.Multiple(() =>
            {
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Books WHERE Id = 11"), Is.Zero, "source row removed");
                Assert.That(Scalar<string>("SELECT HardcoverBookId FROM Books WHERE Id = 10"), Is.EqualTo("hc:584720"), "target keeps its foreign ids");
                Assert.That(Scalar<string>("SELECT GoodreadsWorkId FROM Books WHERE Id = 10"), Is.Null);
                Assert.That(Scalar<bool>("SELECT AudiobookMonitored FROM Books WHERE Id = 10"), Is.True, "monitoring is OR-ed");

                Assert.That(Scalar<int>("SELECT EditionId FROM BookFiles WHERE Id = 1"), Is.EqualTo(100), "file of the twin edition folds into the target's edition");
                Assert.That(Scalar<int>("SELECT EditionId FROM BookFiles WHERE Id = 2"), Is.EqualTo(111));
                Assert.That(Scalar<int>("SELECT BookId FROM Editions WHERE Id = 111"), Is.EqualTo(10), "distinct edition moves");
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Editions WHERE Id = 110"), Is.Zero, "twin edition removed");
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Editions WHERE BookId = 10 AND Monitored = 1"), Is.EqualTo(1), "one monitored edition");
                Assert.That(Scalar<bool>("SELECT Monitored FROM Editions WHERE Id = 100"), Is.True, "the target's own choice wins");

                Assert.That(Scalar<string>("SELECT BookId || ':' || EditionId FROM History WHERE Id = 1"), Is.EqualTo("10:100"));
                Assert.That(Scalar<int>("SELECT BookId FROM DownloadHistory WHERE Id = 1"), Is.EqualTo(10));
                Assert.That(Scalar<string>("SELECT group_concat(SeriesId) FROM (SELECT SeriesId FROM SeriesBookLink WHERE BookId = 10 ORDER BY SeriesId)"), Is.EqualTo("5,6"));
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM SeriesBookLink WHERE BookId = 11"), Is.Zero);
                Assert.That(Scalar<string>("SELECT BookProviderIds FROM Blocklist WHERE Id = 1"), Does.Contain("hc:999").And.Contain("hc:584720"));

                Assert.That(Scalar<int>("SELECT COUNT(*) FROM BookFiles"), Is.EqualTo(3), "no book file row deleted");
                Assert.That(File.Exists(_fileA) && File.Exists(_fileB), Is.True, "nothing deleted on disk");
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Books WHERE Id = 12"), Is.EqualTo(1), "the ebook sibling is untouched");

                Assert.That(FtsIsHealthy(), Is.True, "search index still consistent");
                Assert.That(report.MergedIds, Is.EqualTo(new[] { 11 }));
                Assert.That(report.Changes["booksRemoved"], Is.EqualTo(1));
                Assert.That(report.Changes["bookFiles"], Is.EqualTo(2));
                Assert.That(report.Changes["editions"], Is.EqualTo(1));
                Assert.That(report.Changes["editionsFolded"], Is.EqualTo(1));
                Assert.That(_events.Events.OfType<BookInfoRefreshedEvent>().Single().Removed.Select(b => b.Id), Is.EqualTo(new[] { 11 }));
                Assert.That(_events.Events.OfType<CachesClearedEvent>(), Is.Not.Empty);
            });
        }

        [Test]
        public void merge_books_prefers_a_real_edition_over_the_targets_placeholder()
        {
            // Caught Up shape: the kept row's monitored edition is a placeholder (no provider id).
            using (var conn = _db.Open())
            {
                _db.Insert(conn, "Books", new { Id = 30, AuthorId = 1, Title = "Caught Up", CleanTitle = "caughtup", MediaType = 1, EbookMonitored = true, HardcoverBookId = "hc:1581190", AnyEditionOk = true });
                _db.Insert(conn, "Books", new { Id = 31, AuthorId = 1, Title = "Caught Up", CleanTitle = "caughtup", MediaType = 1, EbookMonitored = true, HardcoverBookId = "hc:1581190", AnyEditionOk = true });
                _db.Insert(conn, "Editions", new { Id = 300, BookId = 30, Title = "Caught Up", Monitored = true });
                _db.Insert(conn, "Editions", new { Id = 310, BookId = 31, Title = "Caught Up", ForeignEditionId = "hc:edition:32092258-ebook", Monitored = true });
            }

            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 30, SourceIds = { 31 } });

            Assert.That(Scalar<int>("SELECT COUNT(*) FROM Editions WHERE BookId = 30 AND Monitored = 1"), Is.EqualTo(1));
            Assert.That(Scalar<bool>("SELECT Monitored FROM Editions WHERE Id = 310"), Is.True, "the real edition stays monitored");
        }

        [Test]
        public void dry_run_reports_the_same_counts_and_changes_nothing()
        {
            var before = Snapshot();

            var preview = _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 }, DryRun = true });

            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(_events.Events, Is.Empty, "no events for a dry run");

            var real = _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } });
            Assert.That(preview.Changes, Is.EqualTo(real.Changes));
        }

        [Test]
        public void a_failure_inside_the_merge_rolls_everything_back()
        {
            var before = Snapshot();
            _subject.BeforeCommit = (conn, tran) => throw new InvalidOperationException("boom");

            Assert.Throws<InvalidOperationException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } }));
            Assert.Throws<InvalidOperationException>(() => _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 2 } }));

            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(_events.Events, Is.Empty);
        }

        [Test]
        public void ebook_and_audiobook_siblings_are_never_merged()
        {
            // The Poppy War shape: ebook row 4029 and audiobook row 4050 of one work.
            var ex = Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 12 }, Force = true }));

            Assert.That(ex.Message, Does.Contain("siblings"));
            Assert.That(Scalar<int>("SELECT COUNT(*) FROM Books WHERE Id IN (10, 12)"), Is.EqualTo(2));
        }

        [Test]
        public void books_of_different_authors_are_refused()
        {
            var ex = Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 20 }, Force = true }));

            Assert.That(ex.Message, Does.Contain("Merge the authors first"));
        }

        [Test]
        public void different_works_need_force()
        {
            var ex = Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 13 } }));
            Assert.That(ex.Message, Does.Contain("different works"));
            Assert.That(Scalar<int>("SELECT COUNT(*) FROM Books WHERE Id = 13"), Is.EqualTo(1));

            _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 13 }, Force = true });
            Assert.That(Scalar<int>("SELECT COUNT(*) FROM Books WHERE Id = 13"), Is.Zero);
        }

        [Test]
        public void recording_copies_need_force()
        {
            using (var conn = _db.Open())
            {
                conn.Execute("UPDATE Books SET UnitKeyHash = 'narrator-b' WHERE Id = 11");
            }

            var ex = Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 11 } }));
            Assert.That(ex.Message, Does.Contain("recording copy"));
        }

        [Test]
        public void self_merge_and_missing_rows_are_refused()
        {
            Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 10 } }));
            Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10 }));
            Assert.Throws<TalearrMergeException>(() => _subject.MergeBooks(new TalearrMergeRequest { TargetId = 10, SourceIds = { 999 } }));
            Assert.Throws<TalearrMergeException>(() => _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 999, SourceIds = { 1 } }));
        }

        // ---- authors ----

        [Test]
        public void merge_authors_moves_books_and_references_without_touching_files()
        {
            var report = _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 2 } });

            Assert.Multiple(() =>
            {
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Authors WHERE Id = 2"), Is.Zero, "source author removed");
                Assert.That(Scalar<int>("SELECT AuthorId FROM Books WHERE Id = 20"), Is.EqualTo(1));
                Assert.That(Scalar<int>("SELECT AuthorId FROM History WHERE Id = 2"), Is.EqualTo(1));
                Assert.That(Scalar<int>("SELECT AuthorId FROM PendingReleases WHERE Id = 1"), Is.EqualTo(1));
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM AuthorSeries WHERE AuthorId = 1"), Is.EqualTo(2), "series links deduplicated");
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM AuthorSeries WHERE AuthorId = 2"), Is.Zero);

                Assert.That(Scalar<string>("SELECT HardcoverAuthorId FROM Authors WHERE Id = 1"), Is.EqualTo("hc:100"), "target keeps its ids");
                Assert.That(Scalar<string>("SELECT Name FROM Authors WHERE Id = 1"), Is.EqualTo("Riley Sager"));
                Assert.That(Scalar<string>("SELECT EbookPath FROM Authors WHERE Id = 1"), Is.EqualTo("/ebooks/Sager, Riley"), "inherits a folder it did not have");
                Assert.That(Scalar<string>("SELECT AudiobookPath FROM Authors WHERE Id = 1"), Is.EqualTo("/books/Riley Sager"));
                Assert.That(Scalar<bool>("SELECT EbookMonitored FROM Authors WHERE Id = 1"), Is.True);

                Assert.That(Scalar<string>("SELECT Path FROM BookFiles WHERE Id = 3"), Is.EqualTo("/books/Sager, Riley/Survive the Night/a.m4b"), "file rows keep their paths");
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM BookFiles"), Is.EqualTo(3));
                Assert.That(File.Exists(_fileA) && File.Exists(_fileB), Is.True);
                Assert.That(Scalar<string>("SELECT AuthorProviderIds FROM Blocklist WHERE Id = 1"), Does.Contain("gr:200").And.Contain("hc:100"));

                Assert.That(FtsIsHealthy(), Is.True, "search index still consistent");
                Assert.That(report.Changes["books"], Is.EqualTo(1));
                Assert.That(report.Changes["authorsRemoved"], Is.EqualTo(1));
                Assert.That(report.Notes, Has.Some.Contains("nothing on disk was moved or deleted"));
            });
        }

        [Test]
        public void merge_authors_sends_the_stored_author_not_an_empty_one()
        {
            var stored = new Author { Id = 1, Name = "Riley Sager" };
            var authors = DispatchProxy.Create<IAuthorService, AuthorServiceProxy>();
            ((AuthorServiceProxy)(object)authors).Author = stored;
            var subject = new TalearrMergeService(_db.MainDatabase, _events, LogManager.GetLogger("TalearrMergeFixture"), authorService: authors);

            subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 2 } });

            // The author page builds the folder from the name; an id-only author has none.
            Assert.That(_events.Events.OfType<AuthorUpdatedEvent>().Single().Author, Is.SameAs(stored));
        }

        [Test]
        public void unrelated_authors_need_force()
        {
            var ex = Assert.Throws<TalearrMergeException>(() => _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 3 } }));
            Assert.That(ex.Message, Does.Contain("force=true"));
            Assert.That(Scalar<int>("SELECT COUNT(*) FROM Authors WHERE Id = 3"), Is.EqualTo(1));
        }

        [Test]
        public void middle_name_variant_merges_without_force_and_previews_its_files()
        {
            using (var conn = _db.Open())
            {
                _db.Insert(conn, "Authors", new { Id = 4, Name = "Riley A. Sager", CleanName = "rileyasager", Path = "/books/Riley A. Sager" });
                _db.Insert(conn, "Books", new { Id = 40, AuthorId = 4, Title = "Lock Every Door", CleanTitle = "lockeverydoor", MediaType = 0, AudiobookMonitored = false, EbookMonitored = false, AnyEditionOk = true });
                _db.Insert(conn, "Editions", new { Id = 400, BookId = 40, Title = "Lock Every Door", ForeignEditionId = "e40", Monitored = true });
                _db.Insert(conn, "BookFiles", new { Id = 4, EditionId = 400, Path = "/books/Riley A. Sager/Lock Every Door/a.m4b", Size = 1 });
            }

            var preview = _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 4 }, DryRun = true });

            Assert.Multiple(() =>
            {
                Assert.That(preview.Changes["books"], Is.EqualTo(1));
                Assert.That(preview.Changes["bookFilesWithBooks"], Is.EqualTo(1));
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Authors WHERE Id = 4"), Is.EqualTo(1), "dry run changes nothing");
            });

            _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 4 } });

            Assert.Multiple(() =>
            {
                Assert.That(Scalar<int>("SELECT COUNT(*) FROM Authors WHERE Id = 4"), Is.Zero);
                Assert.That(Scalar<int>("SELECT AuthorId FROM Books WHERE Id = 40"), Is.EqualTo(1));
                Assert.That(Scalar<int>("SELECT EditionId FROM BookFiles WHERE Id = 4"), Is.EqualTo(400));
            });
        }

        [Test]
        public void author_preview_counts_files_that_come_with_the_books()
        {
            var preview = _subject.MergeAuthors(new TalearrMergeRequest { TargetId = 1, SourceIds = { 2 }, DryRun = true });

            Assert.That(preview.Changes["bookFilesWithBooks"], Is.EqualTo(1));
        }

        // ---- finder ----

        [Test]
        public void finder_lists_duplicate_authors_and_same_media_type_books_only()
        {
            var candidates = new TalearrDuplicateFinder(_db.MainDatabase).Find();

            var authors = candidates.Authors.Single();
            Assert.That(authors.Authors.Select(a => a.Id), Is.EquivalentTo(new[] { 1, 2 }));

            var books = candidates.Books.Single();
            Assert.That(books.MediaType, Is.EqualTo("audiobook"));
            Assert.That(books.Books.Select(b => b.Id), Is.EqualTo(new[] { 11, 10 }), "rows with files first");
            Assert.That(books.Books.First().FileCount, Is.EqualTo(2));
            Assert.That(books.Books.First().EditionCount, Is.EqualTo(2));
        }

        [Test]
        public void finder_lists_middle_name_variants_as_possible_sets()
        {
            using (var conn = _db.Open())
            {
                _db.Insert(conn, "Authors", new { Id = 4, Name = "Riley A. Sager", CleanName = "rileyasager", Path = "/books/Riley A. Sager" });
                _db.Insert(conn, "Authors", new { Id = 5, Name = "Riley B. Sager", CleanName = "rileybsager", Path = "/books/Riley B. Sager" });
                _db.Insert(conn, "Authors", new { Id = 6, Name = "Dietmar Wehr", CleanName = "dietmarwehr", Path = "/books/Dietmar Wehr" });
                _db.Insert(conn, "Authors", new { Id = 7, Name = "Dietmar Arthur Wehr", CleanName = "dietmararthurwehr", Path = "/books/Dietmar Arthur Wehr" });
                _db.Insert(conn, "Authors", new { Id = 8, Name = "Wehr, Dietmar A.", CleanName = "wehrdietmara", Path = "/books/Wehr, Dietmar A." });
                _db.Insert(conn, "Authors", new { Id = 9, Name = "Stephen A. Kingsley", CleanName = "stephenakingsley", Path = "/books/Stephen A. Kingsley" });
                _db.Insert(conn, "Authors", new { Id = 10, Name = "Stephen B. Kingsley", CleanName = "stephenbkingsley", Path = "/books/Stephen B. Kingsley" });
            }

            var authors = new TalearrDuplicateFinder(_db.MainDatabase).Find().Authors;

            var exact = authors.Where(a => a.Match == TalearrDuplicateAuthorSet.ExactMatch).ToList();
            var loose = authors.Where(a => a.Match == TalearrDuplicateAuthorSet.MiddleNameMatch)
                .Select(a => a.Authors.Select(x => x.Id).OrderBy(id => id).ToArray())
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(exact.Single().Authors.Select(a => a.Id), Is.EquivalentTo(new[] { 1, 2 }), "exact set unchanged");
                Assert.That(loose, Has.Count.EqualTo(3));
                Assert.That(loose, Has.Some.EqualTo(new[] { 1, 2, 4 }), "plain name with the A reading");
                Assert.That(loose, Has.Some.EqualTo(new[] { 1, 2, 5 }), "plain name with the B reading");
                Assert.That(loose, Has.Some.EqualTo(new[] { 6, 7, 8 }));
                Assert.That(loose, Has.None.Contains(9).Or.Contains(10), "conflicting middles, no plain name");
                Assert.That(loose, Has.None.Contains(3));
                Assert.That(authors.Select(a => a.Key), Is.Unique);
            });
        }

        [TestCase("Dietmar Wehr", "Dietmar Arthur Wehr", true)]
        [TestCase("Dietmar Wehr", "Dietmar A. Wehr", true)]
        [TestCase("Dietmar A. Wehr", "Dietmar Arthur Wehr", true)]
        [TestCase("Wehr, Dietmar", "Dietmar Arthur Wehr", true)]
        [TestCase("Wehr, Dietmar A.", "Dietmar Arthur Wehr", true)]
        [TestCase("Ursula Le Guin", "Ursula K. Le Guin", true)]
        [TestCase("George R. R. Martin", "George Martin", true)]
        [TestCase("George R. R. Martin", "George Raymond Richard Martin", true)]
        [TestCase("Gabriel Garcia Marquez", "Gabriel José García Márquez", true)]
        [TestCase("John A. Smith", "John B. Smith", false)]
        [TestCase("John Arthur Smith", "John Albert Smith", false)]
        [TestCase("John Arthur Smith", "John B. Smith", false)]
        [TestCase("John Smith", "Jane Smith", false)]
        [TestCase("John Smith", "John Smyth", false)]
        [TestCase("John A. Smith", "Jane A. Smith", false)]
        [TestCase("Smith, John", "John Smithers", false)]
        [TestCase("Martin Luther King Jr.", "Martin King Jr.", true)]
        [TestCase("Martin Luther King Jr.", "Martin Luther King", false)]
        [TestCase("Prince", "Prince", false)]
        [TestCase("", "John Smith", false)]
        public void middle_names_agree(string left, string right, bool expected)
        {
            Assert.That(TalearrDuplicateFinder.MiddleNamesAgree(left, right), Is.EqualTo(expected));
            Assert.That(TalearrDuplicateFinder.MiddleNamesAgree(right, left), Is.EqualTo(expected), "symmetric");
        }

        [TestCase("Sager, Riley", "riley sager")]
        [TestCase("J.R.R. Tolkien", "jrr tolkien")]
        [TestCase("J. R. R. Tolkien", "jrr tolkien")]
        [TestCase("Gabriel García Márquez", "gabriel garcia marquez")]
        public void author_names_normalize(string name, string expected)
        {
            Assert.That(TalearrDuplicateFinder.NormalizeAuthorName(name), Is.EqualTo(expected));
        }
    }
}
