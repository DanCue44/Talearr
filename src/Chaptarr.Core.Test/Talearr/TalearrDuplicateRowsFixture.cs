using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr
{
    // Talearr: one author add stored the same audiobook works twice (2026-10-16, Riley Sager).
    // Rows below are the live ones from GET /api/v1/book: ebook pass 7486.., audiobook pass
    // 7501.. (from the add's reconcile) and a second audiobook pass 7516.. (from the
    // RefreshAuthorCommand that ran at the same time).
    [TestFixture]
    [NonParallelizable]
    public class TalearrDuplicateRowsFixture
    {
        private const int AuthorId = 16;
        private const string OnlyOneLeft = "The Only One Left";
        private const string Bundle = "The Only One Left / The House Across the Lake";
        private const string FourBooks = "Home Before Dark, Survive the Night, The House Across the Lake, The Only One Left 4 Books Collection Set";

        private static Book Row(int id, string title, BookMediaType mediaType, string hc = null, string gr = null, bool monitored = false, BookAddType addType = BookAddType.Automatic)
        {
            return new Book
            {
                Id = id,
                AuthorId = AuthorId,
                Title = title,
                MediaType = mediaType,
                HardcoverBookId = hc,
                GoodreadsWorkId = gr,
                Monitored = monitored,
                AudiobookMonitored = monitored && mediaType == BookMediaType.Audiobook,
                EbookMonitored = monitored && mediaType == BookMediaType.Ebook,
                AnyEditionOk = true,
                AddOptions = new AddBookOptions { AddType = addType }
            };
        }

        private static TalearrBookRowFacts Facts(Book book, bool hasFiles = false, params Edition[] editions)
        {
            return new TalearrBookRowFacts { Book = book, HasFiles = hasFiles, Editions = editions.ToList() };
        }

        private static List<TalearrBookRowFacts> LiveRows()
        {
            return new List<TalearrBookRowFacts>
            {
                Facts(Row(7486, OnlyOneLeft, BookMediaType.Ebook, "hc:584720", "gr:303372801", monitored: true), hasFiles: true),
                Facts(Row(7496, Bundle, BookMediaType.Ebook, "hc:1697362")),
                Facts(Row(7497, FourBooks, BookMediaType.Ebook, gr: "gr:205927041")),
                Facts(Row(7501, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", "gr:303372801", monitored: true), hasFiles: true),
                Facts(Row(7511, Bundle, BookMediaType.Audiobook, "hc:1697362")),
                Facts(Row(7512, FourBooks, BookMediaType.Audiobook, gr: "gr:205927041")),
                Facts(Row(7516, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", "gr:303372801")),
                Facts(Row(7526, Bundle, BookMediaType.Audiobook, "hc:1697362")),
                Facts(Row(7527, FourBooks, BookMediaType.Audiobook, gr: "gr:205927041"))
            };
        }

        // ---- rules ----

        [Test]
        public void live_rows_fold_the_second_audiobook_pass_only()
        {
            var groups = TalearrDuplicateBooks.FindGroups(LiveRows());

            Assert.That(groups.SelectMany(g => g.Remove).Select(r => r.Book.Id).OrderBy(id => id), Is.EqualTo(new[] { 7516, 7526, 7527 }));

            var single = groups.Single(g => g.Title == OnlyOneLeft);
            Assert.That(single.MediaType, Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(single.Keep.Book.Id, Is.EqualTo(7501), "the monitored row with the file is kept");

            Assert.That(groups.Single(g => g.Title == Bundle).Keep.Book.Id, Is.EqualTo(7511), "the oldest of two plain rows is kept");
            Assert.That(groups.Any(g => g.MediaType == BookMediaType.Ebook), Is.False, "one ebook row per work is not a duplicate");
        }

        [Test]
        public void ebook_and_audiobook_rows_of_one_work_are_not_duplicates()
        {
            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(LiveRows()[0].Book, LiveRows()[3].Book), Is.False);
        }

        [Test]
        public void distinct_works_are_never_merged()
        {
            var single = Row(1, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720");
            var bundle = Row(2, Bundle, BookMediaType.Audiobook, "hc:584720");
            var sharesOneId = Row(3, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", "gr:1");
            var otherAuthor = Row(4, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720");
            otherAuthor.AuthorId = 99;
            var noIds = Row(5, OnlyOneLeft, BookMediaType.Audiobook);
            var noIdsToo = Row(6, OnlyOneLeft, BookMediaType.Audiobook);

            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(single, bundle), Is.False, "same id, different title");
            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(single, sharesOneId), Is.False, "only one of the work ids in common");
            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(single, otherAuthor), Is.False, "different author");
            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(noIds, noIdsToo), Is.False, "no stable work id, title alone is not enough");

            var groups = TalearrDuplicateBooks.FindGroups(new[] { single, bundle, sharesOneId, otherAuthor, noIds, noIdsToo }.Select(b => Facts(b)));
            Assert.That(groups, Is.Empty);
        }

        [Test]
        public void title_format_noise_does_not_hide_a_duplicate()
        {
            var first = Row(1, "The Only One Left", BookMediaType.Audiobook, "hc:584720");
            var second = Row(2, "The Only One Left (Audiobook)", BookMediaType.Audiobook, "hc:584720");

            Assert.That(TalearrDuplicateBooks.AreSameStoredWork(first, second), Is.True);
        }

        [Test]
        public void rows_with_files_monitoring_or_manual_adds_are_never_removed()
        {
            var keep = Facts(Row(1, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"));
            var withFiles = Facts(Row(2, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"), hasFiles: true);
            var monitored = Facts(Row(3, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", monitored: true));
            var manual = Facts(Row(4, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", addType: BookAddType.Manual));
            var pinned = Facts(Row(5, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"));
            pinned.Book.AnyEditionOk = false;
            var copy = Facts(Row(6, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"));
            copy.Book.UnitKeyHash = "recording-abridged";
            var manualEdition = Facts(Row(7, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"), false, new Edition { BookId = 7, ManualAdd = true });
            var plain = Facts(Row(8, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"));

            var group = TalearrDuplicateBooks.FindGroups(new[] { keep, withFiles, monitored, manual, pinned, copy, manualEdition, plain }).Single();

            Assert.That(group.Keep.Book.Id, Is.EqualTo(2));
            Assert.That(group.Remove.Select(r => r.Book.Id).OrderBy(id => id), Is.EqualTo(new[] { 1, 8 }),
                "only the plain, unmonitored, file-less copies go");
            Assert.That(group.Kept.Select(r => r.Book.Id).OrderBy(id => id), Is.EqualTo(new[] { 3, 4, 5, 6, 7 }));
        }

        [Test]
        public void a_recordings_copy_never_costs_the_primary_row()
        {
            // An unmonitored primary audiobook without files next to a manual recording copy.
            var primary = Facts(Row(10, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720"));
            var recording = Facts(Row(11, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", monitored: true, addType: BookAddType.Manual), true);
            recording.Book.AnyEditionOk = false;

            var group = TalearrDuplicateBooks.FindGroups(new[] { primary, recording }).Single();

            Assert.That(group.Remove, Is.Empty);
        }

        [Test]
        public void add_uses_one_row_when_a_work_resolves_to_duplicates()
        {
            var first = Row(7501, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", "gr:303372801");
            var second = Row(7516, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720", "gr:303372801", monitored: true);

            Assert.That(TalearrDuplicateBooks.PickRequestedBook(new[] { first, second })?.Id, Is.EqualTo(7516), "the monitored row");
            second.AudiobookMonitored = false;
            second.Monitored = false;
            Assert.That(TalearrDuplicateBooks.PickRequestedBook(new[] { second, first })?.Id, Is.EqualTo(7501), "else the oldest");
        }

        [Test]
        public void add_still_refuses_distinct_works_behind_one_id()
        {
            var single = Row(1, OnlyOneLeft, BookMediaType.Audiobook, "hc:584720");
            var bundle = Row(2, Bundle, BookMediaType.Audiobook, "hc:584720");

            Assert.That(TalearrDuplicateBooks.PickRequestedBook(new[] { single, bundle }), Is.Null);
        }

        // ---- cleanup service ----

        [Test]
        public void cleanup_is_a_dry_run_by_default_and_reports_what_it_would_remove()
        {
            var world = new CleanupWorld(LiveRows());

            var report = world.Service.Run(dryRun: true);

            Assert.That(report.DryRun, Is.True);
            Assert.That(report.RowsToRemove, Is.EqualTo(3));
            Assert.That(report.RowsRemoved, Is.EqualTo(0));
            Assert.That(report.Groups.SelectMany(g => g.Remove).Select(r => r.BookId).OrderBy(id => id), Is.EqualTo(new[] { 7516, 7526, 7527 }));
            Assert.That(world.Deleted, Is.Empty);
            Assert.That(new TalearrDuplicateBookCleanupCommand().DryRun, Is.True, "the command is a dry run unless told otherwise");
        }

        [Test]
        public void cleanup_removes_only_unmonitored_file_less_copies_without_deleting_files()
        {
            var world = new CleanupWorld(LiveRows());

            var report = world.Service.Run(dryRun: false);

            Assert.That(report.RowsRemoved, Is.EqualTo(3));
            Assert.That(world.Deleted.Select(d => d.BookId).OrderBy(id => id), Is.EqualTo(new[] { 7516, 7526, 7527 }));
            Assert.That(world.Deleted.All(d => !d.DeleteFiles), Is.True);
        }

        [Test]
        public void cleanup_rechecks_a_row_before_deleting_it()
        {
            var world = new CleanupWorld(LiveRows());
            world.FilesAppearOnRecheck.Add(7516);

            var report = world.Service.Run(dryRun: false);

            Assert.That(world.Deleted.Select(d => d.BookId), Does.Not.Contain(7516));
            Assert.That(report.RowsRemoved, Is.EqualTo(2));
        }

        [Test]
        public void cleanup_can_be_limited_to_one_author()
        {
            var world = new CleanupWorld(LiveRows());

            var report = world.Service.Run(dryRun: true, authorId: 99);

            Assert.That(report.AuthorsScanned, Is.EqualTo(0));
            Assert.That(report.Groups, Is.Empty);
        }

        // ---- add reconcile shares the refresh gate ----

        [Test]
        public void gate_waits_for_a_running_refresh_and_releases_after()
        {
            var gate = new SemaphoreSlim(1, 1);
            gate.Wait();

            var entered = Task.Run(() =>
            {
                using (TalearrAuthorRefreshGate.Enter(gate, LogManager.GetCurrentClassLogger(), "test"))
                {
                    return gate.CurrentCount;
                }
            });

            Assert.That(entered.Wait(200), Is.False, "waits while the refresh holds the gate");
            gate.Release();
            Assert.That(entered.Wait(5000), Is.True);
            Assert.That(entered.Result, Is.EqualTo(0), "held while inside");
            Assert.That(gate.CurrentCount, Is.EqualTo(1), "released after");
        }

        [Test]
        public void gate_wait_is_bounded()
        {
            var previous = TalearrAuthorRefreshGate.Timeout;
            TalearrAuthorRefreshGate.Timeout = TimeSpan.FromMilliseconds(100);

            try
            {
                var gate = new SemaphoreSlim(1, 1);
                gate.Wait();

                using (TalearrAuthorRefreshGate.Enter(gate, LogManager.GetCurrentClassLogger(), "test"))
                {
                }

                Assert.That(gate.CurrentCount, Is.EqualTo(0), "a gate that was not acquired is not released");
            }
            finally
            {
                TalearrAuthorRefreshGate.Timeout = previous;
            }
        }

        [Test]
        public void add_reconcile_waits_for_the_author_refresh_gate()
        {
            var gateField = typeof(RefreshAuthorService).GetField("AuthorMetadataRefreshGate", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(gateField, Is.Not.Null, "upstream gate still exists");
            var gate = (SemaphoreSlim)gateField.GetValue(null);

            var service = new RefreshAuthorService(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                LogManager.GetCurrentClassLogger());

            Assert.That(gate.Wait(5000), Is.True);
            var reconcile = Task.Run(() =>
            {
                try
                {
                    service.ReconcileAuthorBlob(new Author { Id = AuthorId, Name = "Riley Sager" }, new Author { Id = AuthorId, Name = "Riley Sager" });
                }
                catch (Exception)
                {
                    // The services are not wired here; only the ordering matters.
                }
            });

            try
            {
                Assert.That(reconcile.Wait(300), Is.False, "the reconcile does not run while a refresh holds the gate");
            }
            finally
            {
                gate.Release();
            }

            Assert.That(reconcile.Wait(10000), Is.True, "and runs once the refresh is done");
            Assert.That(gate.CurrentCount, Is.EqualTo(1), "and gives the gate back");
        }

        private sealed class CleanupWorld
        {
            public CleanupWorld(List<TalearrBookRowFacts> rows)
            {
                Rows = rows;

                var authors = DispatchProxy.Create<IAuthorService, Proxy<IAuthorService>>();
                ((Proxy<IAuthorService>)(object)authors).Handler = (name, args) => name switch
                {
                    nameof(IAuthorService.GetAllAuthors) => new List<Author> { new Author { Id = AuthorId, Name = "Riley Sager" } },
                    nameof(IAuthorService.GetAuthor) => (int)args[0] == AuthorId ? new Author { Id = AuthorId, Name = "Riley Sager" } : null,
                    _ => throw new NotImplementedException(name)
                };

                var books = DispatchProxy.Create<IBookService, Proxy<IBookService>>();
                ((Proxy<IBookService>)(object)books).Handler = (name, args) =>
                {
                    switch (name)
                    {
                        case nameof(IBookService.GetBooksByAuthor):
                            return Rows.Where(r => r.Book.AuthorId == (int)args[0] && Deleted.All(d => d.BookId != r.Book.Id)).Select(r => r.Book).ToList();
                        case nameof(IBookService.GetBook):
                            return Rows.Select(r => r.Book).FirstOrDefault(b => b.Id == (int)args[0]);
                        case nameof(IBookService.DeleteBook):
                            Deleted.Add(((int)args[0], (bool)args[1]));
                            return null;
                        default:
                            throw new NotImplementedException(name);
                    }
                };

                var editions = DispatchProxy.Create<IEditionService, Proxy<IEditionService>>();
                ((Proxy<IEditionService>)(object)editions).Handler = (name, args) =>
                {
                    if (name != nameof(IEditionService.GetEditionsByBook))
                    {
                        throw new NotImplementedException(name);
                    }

                    var ids = args[0] is int single ? new List<int> { single } : ((IEnumerable<int>)args[0]).ToList();
                    return Rows.Where(r => ids.Contains(r.Book.Id)).SelectMany(r => r.Editions).ToList();
                };

                var files = DispatchProxy.Create<IMediaFileService, Proxy<IMediaFileService>>();
                ((Proxy<IMediaFileService>)(object)files).Handler = (name, args) =>
                {
                    if (name != nameof(IMediaFileService.GetFilesByBook))
                    {
                        throw new NotImplementedException(name);
                    }

                    var id = (int)args[0];
                    var calls = FileChecks.TryGetValue(id, out var c) ? c : 0;
                    FileChecks[id] = calls + 1;

                    var hasFiles = Rows.Single(r => r.Book.Id == id).HasFiles || (calls > 0 && FilesAppearOnRecheck.Contains(id));
                    return hasFiles ? new List<BookFile> { new BookFile { Id = id } } : new List<BookFile>();
                };

                Service = new TalearrDuplicateBookCleanupService(authors, books, editions, files, LogManager.GetCurrentClassLogger());
            }

            public List<TalearrBookRowFacts> Rows { get; }
            public List<(int BookId, bool DeleteFiles)> Deleted { get; } = new();
            public HashSet<int> FilesAppearOnRecheck { get; } = new();
            public Dictionary<int, int> FileChecks { get; } = new();
            public TalearrDuplicateBookCleanupService Service { get; }
        }

        public class Proxy<T> : DispatchProxy
        {
            public Func<string, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return Handler(targetMethod.Name, args);
            }
        }
    }
}
