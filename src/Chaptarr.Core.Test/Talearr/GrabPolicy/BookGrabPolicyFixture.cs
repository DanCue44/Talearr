using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Api.V1.Talearr;
using Chaptarr.Core.Test.Talearr.Audiobookshelf;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    // TaleArr: per-book abridged overrides end to end on an in-memory library run through the
    // real upstream BookService: talearr.db storage, the API, the edition switch before a search,
    // the retry note and the Audiobookshelf owned-copy check.
    [TestFixture]
    public class BookGrabPolicyFixture
    {
        private RecordingsWorld _world;
        private FakeGrabPolicyService _global;
        private TalearrBookGrabPolicyStore _store;
        private TalearrBookGrabPolicyService _service;
        private List<EntityHistory> _history;
        private IHistoryRepository _historyRepository;
        private TalearrAbridgedEditionSwitch _switch;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _global = new FakeGrabPolicyService();
            _store = new TalearrBookGrabPolicyStore(_world.Folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            _service = new TalearrBookGrabPolicyService(_store, _world.BookService, _world.Groups, _global, LogManager.GetCurrentClassLogger());

            _history = new List<EntityHistory>();
            var (historyRepository, historyFake) = InterfaceFake<IHistoryRepository>.Create();
            historyFake.On(nameof(IHistoryRepository.GetByBook), args => _history.Where(h => h.BookId == (int)args[0] && (args[1] == null || h.EventType == (EntityHistoryEventType)args[1])).ToList());
            historyFake.On(nameof(IHistoryRepository.Insert), args =>
            {
                _history.Add((EntityHistory)args[0]);
                return args[0];
            });
            _historyRepository = historyRepository;

            _switch = new TalearrAbridgedEditionSwitch(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Recordings, _world.Resolver,
                _global, _service, _historyRepository, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private Book Firm(Edition monitored, bool withFiles = false)
        {
            var book = _world.AddFirm(monitored, withFiles);
            book.Author = new Author { Id = 7, Name = "John Grisham", AudiobookMonitored = true };
            return book;
        }

        // ReleaseSearchService hands the switch a book with its author loaded.
        private void Search(Book book)
        {
            book.Author = new Author { Id = 7, Name = "John Grisham", AudiobookMonitored = true };
            _switch.Apply(book);
        }

        private Edition Monitored(int bookId)
        {
            return _world.Editions.Where(e => e.BookId == bookId && e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
        }

        // Storage

        [Test]
        public void talearr_db_schema_three_adds_the_overrides_table()
        {
            var database = _world.Folder.CreateDatabase();
            using var connection = database.OpenConnection();

            Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.EqualTo(3));
            Assert.That(connection.ExecuteScalar<int>("PRAGMA user_version;"), Is.EqualTo(3));
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'BookGrabPolicies'"), Is.EqualTo(1));
        }

        [Test]
        public void store_round_trips_and_deletes()
        {
            Assert.That(_store.Upsert(new BookGrabPolicyOverride { WorkKey = "hc:378630", AbridgedPolicy = AbridgedAudiobookPolicy.AbridgedOnly, BookIdHint = 3401, Title = "The Firm" }), Is.True);

            var read = _store.Get("HC:378630");
            Assert.That(read.AbridgedPolicy, Is.EqualTo(AbridgedAudiobookPolicy.AbridgedOnly));
            Assert.That(read.BookIdHint, Is.EqualTo(3401));
            Assert.That(_store.GetAll()["hc:378630"], Is.EqualTo(AbridgedAudiobookPolicy.AbridgedOnly));

            _store.Upsert(new BookGrabPolicyOverride { WorkKey = "hc:378630", AbridgedPolicy = AbridgedAudiobookPolicy.Any });
            Assert.That(_store.Get("hc:378630").AbridgedPolicy, Is.EqualTo(AbridgedAudiobookPolicy.Any));

            _store.Delete("hc:378630");
            Assert.That(_store.Get("hc:378630"), Is.Null);
            Assert.That(_store.GetAll(), Is.Empty);
        }

        [Test]
        public void stored_values_use_the_same_spelling_as_the_global_setting()
        {
            _store.Upsert(new BookGrabPolicyOverride { WorkKey = "hc:378630", AbridgedPolicy = AbridgedAudiobookPolicy.PreferUnabridged });

            using var connection = _world.Folder.CreateDatabase().OpenConnection();
            Assert.That(connection.ExecuteScalar<string>("SELECT AbridgedPolicy FROM BookGrabPolicies"), Is.EqualTo("preferunabridged"));
        }

        [Test]
        public void losing_talearr_db_only_loses_the_overrides()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _global.Policy = new TalearrGrabPolicy { AbridgedPolicy = AbridgedAudiobookPolicy.PreferUnabridged };
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_world.Folder.TalearrDbPath);

            var fresh = new TalearrBookGrabPolicyService(new TalearrBookGrabPolicyStore(_world.Folder.CreateDatabase(), LogManager.GetCurrentClassLogger()),
                _world.BookService, _world.Groups, _global, LogManager.GetCurrentClassLogger());

            var result = fresh.Get(book.Id);
            Assert.That(result.AbridgedPolicy, Is.Null);
            Assert.That(result.EffectivePolicy, Is.EqualTo(AbridgedAudiobookPolicy.PreferUnabridged));
            Assert.That(result.Source, Is.EqualTo(AbridgedPolicySources.Global));
            Assert.That(fresh.GetOverride(book), Is.Null);
        }

        [Test]
        public void an_unusable_database_means_no_override_instead_of_an_error()
        {
            var (database, fake) = InterfaceFake<ITalearrDatabase>.Create();
            fake.On(nameof(ITalearrDatabase.OpenConnection), _ => throw new IOException("disk gone"));
            fake.On(nameof(ITalearrDatabase.IsCorruption), _ => false);
            var store = new TalearrBookGrabPolicyStore(database, LogManager.GetCurrentClassLogger());

            Assert.That(store.GetAll(), Is.Empty);
            Assert.That(store.Get("hc:378630"), Is.Null);
            Assert.That(store.Upsert(new BookGrabPolicyOverride { WorkKey = "hc:378630", AbridgedPolicy = AbridgedAudiobookPolicy.Any }), Is.False);
        }

        // Keys

        [Test]
        public void override_is_keyed_by_the_work_so_it_survives_a_new_row_id()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            var rebuilt = new Book { Id = 5501, AuthorId = 7, BaseBookId = "hc:378630", TitleSlug = "the-firm_3", MediaType = BookMediaType.Audiobook };

            Assert.That(_service.GetOverride(rebuilt), Is.EqualTo(AbridgedAudiobookPolicy.AbridgedOnly));
            Assert.That(_store.Get("hc:378630").BookIdHint, Is.EqualTo(book.Id));
            Assert.That(_store.Get("hc:378630").ForeignEditionId, Is.EqualTo(TalearrEditionKey.For(FirmEditions.ScottBrickUs())));
        }

        [Test]
        public void row_id_key_is_only_the_fallback_without_a_provider_work_id()
        {
            Assert.That(TalearrBookGrabPolicyService.WorkKey(new Book { Id = 12, BaseBookId = " hc:378630 " }), Is.EqualTo("hc:378630"));
            Assert.That(TalearrBookGrabPolicyService.WorkKey(new Book { Id = 12, BaseBookId = null }), Is.EqualTo("book:12"));
        }

        [Test]
        public void ebook_rows_and_additional_recordings_take_no_override()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Assert.That(_service.GetOverride(_world.Books[3402]), Is.Null, "ebook row");
            Assert.That(_service.GetOverride(new Book { Id = 3410, BaseBookId = "hc:378630", TitleSlug = "the-firm_wanted_1", MediaType = BookMediaType.Audiobook }), Is.Null, "copy row");
        }

        // Service, every policy value per book and the global fallback

        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase(AbridgedAudiobookPolicy.Any)]
        public void each_value_can_be_set_read_and_cleared(AbridgedAudiobookPolicy policy)
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _global.Policy = new TalearrGrabPolicy { AbridgedPolicy = policy == AbridgedAudiobookPolicy.Any ? AbridgedAudiobookPolicy.UnabridgedOnly : AbridgedAudiobookPolicy.Any };

            var set = _service.Set(book.Id, policy);
            Assert.That(set.Status, Is.EqualTo(BookGrabPolicyStatus.Ok));
            Assert.That(set.AbridgedPolicy, Is.EqualTo(policy));
            Assert.That(set.EffectivePolicy, Is.EqualTo(policy));
            Assert.That(set.Source, Is.EqualTo(AbridgedPolicySources.Book));
            Assert.That(_service.GetOverride(book), Is.EqualTo(policy));

            var cleared = _service.Set(book.Id, null);
            Assert.That(cleared.AbridgedPolicy, Is.Null);
            Assert.That(cleared.EffectivePolicy, Is.EqualTo(_global.Policy.AbridgedPolicy));
            Assert.That(cleared.Source, Is.EqualTo(AbridgedPolicySources.Global));
            Assert.That(_service.GetOverride(book), Is.Null);
        }

        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase(AbridgedAudiobookPolicy.Any)]
        public void a_book_without_an_override_reports_the_global_policy(AbridgedAudiobookPolicy global)
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _global.Policy = new TalearrGrabPolicy { AbridgedPolicy = global };

            var result = _service.Get(book.Id);

            Assert.That(result.Status, Is.EqualTo(BookGrabPolicyStatus.Ok));
            Assert.That(result.BookId, Is.EqualTo(book.Id));
            Assert.That(result.AbridgedPolicy, Is.Null);
            Assert.That(result.EffectivePolicy, Is.EqualTo(global));
            Assert.That(result.Source, Is.EqualTo(AbridgedPolicySources.Global));
        }

        [Test]
        public void the_ebook_row_of_a_work_with_an_audiobook_answers_for_the_audiobook()
        {
            var book = Firm(FirmEditions.ScottBrickUs());

            var set = _service.Set(3402, AbridgedAudiobookPolicy.AbridgedOnly);

            Assert.That(set.Status, Is.EqualTo(BookGrabPolicyStatus.Ok));
            Assert.That(set.BookId, Is.EqualTo(3402));
            Assert.That(_service.GetOverride(book), Is.EqualTo(AbridgedAudiobookPolicy.AbridgedOnly));
        }

        [Test]
        public void unknown_books_and_ebook_only_works_are_refused()
        {
            _world.Books[4801] = new Book { Id = 4801, AuthorId = 8, Title = "Animal Farm", TitleSlug = "animal-farm", BaseBookId = "hc:4801", MediaType = BookMediaType.Ebook, AddOptions = new AddBookOptions() };

            Assert.That(_service.Get(424242).Status, Is.EqualTo(BookGrabPolicyStatus.NotFound));
            Assert.That(_service.Set(424242, AbridgedAudiobookPolicy.Any).Status, Is.EqualTo(BookGrabPolicyStatus.NotFound));
            Assert.That(_service.Get(4801).Status, Is.EqualTo(BookGrabPolicyStatus.Invalid));
            Assert.That(_service.Set(4801, AbridgedAudiobookPolicy.AbridgedOnly).Status, Is.EqualTo(BookGrabPolicyStatus.Invalid));
            Assert.That(_service.Set(4801, (AbridgedAudiobookPolicy)42).Status, Is.EqualTo(BookGrabPolicyStatus.Invalid));
        }

        // API

        private static int Status<T>(ActionResult<T> result)
        {
            return result.Result switch
            {
                null => 200,
                ObjectResult o => o.StatusCode ?? 200,
                StatusCodeResult s => s.StatusCode,
                _ => 0
            };
        }

        [Test]
        public void api_get_put_and_clear()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            var controller = new TalearrGrabPolicyController(_service);

            var before = controller.GetBook(book.Id);
            Assert.That(Status(before), Is.EqualTo(200));
            Assert.That(before.Value.AbridgedPolicy, Is.Null);
            Assert.That(before.Value.EffectivePolicy, Is.EqualTo("unabridgedOnly"));
            Assert.That(before.Value.Source, Is.EqualTo("global"));

            var put = controller.SetBook(book.Id, new TalearrBookGrabPolicyBody { AbridgedPolicy = "abridgedOnly" });
            Assert.That(Status(put), Is.EqualTo(200));
            Assert.That(put.Value.BookId, Is.EqualTo(book.Id));
            Assert.That(put.Value.AbridgedPolicy, Is.EqualTo("abridgedOnly"));
            Assert.That(put.Value.EffectivePolicy, Is.EqualTo("abridgedOnly"));
            Assert.That(put.Value.Source, Is.EqualTo("book"));
            Assert.That(controller.GetBook(book.Id).Value.Source, Is.EqualTo("book"));

            var cleared = controller.SetBook(book.Id, new TalearrBookGrabPolicyBody { AbridgedPolicy = null });
            Assert.That(Status(cleared), Is.EqualTo(200));
            Assert.That(cleared.Value.AbridgedPolicy, Is.Null);
            Assert.That(cleared.Value.Source, Is.EqualTo("global"));
        }

        [TestCase("unabridgedOnly", "unabridgedOnly")]
        [TestCase("ABRIDGEDONLY", "abridgedOnly")]
        [TestCase("preferUnabridged", "preferUnabridged")]
        [TestCase("any", "any")]
        public void api_accepts_every_value(string sent, string stored)
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            var controller = new TalearrGrabPolicyController(_service);

            Assert.That(controller.SetBook(book.Id, new TalearrBookGrabPolicyBody { AbridgedPolicy = sent }).Value.AbridgedPolicy, Is.EqualTo(stored));
        }

        [TestCase("sometimes")]
        [TestCase("1")]
        [TestCase("")]
        [TestCase(" ")]
        public void api_refuses_a_bad_value_with_400(string value)
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            var controller = new TalearrGrabPolicyController(_service);

            Assert.That(Status(controller.SetBook(book.Id, new TalearrBookGrabPolicyBody { AbridgedPolicy = value })), Is.EqualTo(400));
            Assert.That(_service.GetOverride(book), Is.Null, "nothing stored");
        }

        [Test]
        public void api_answers_404_and_400()
        {
            _world.Books[4801] = new Book { Id = 4801, AuthorId = 8, Title = "Animal Farm", TitleSlug = "animal-farm", BaseBookId = "hc:4801", MediaType = BookMediaType.Ebook, AddOptions = new AddBookOptions() };
            var controller = new TalearrGrabPolicyController(_service);

            Assert.That(Status(controller.GetBook(424242)), Is.EqualTo(404));
            Assert.That(Status(controller.SetBook(424242, new TalearrBookGrabPolicyBody { AbridgedPolicy = "any" })), Is.EqualTo(404));
            Assert.That(Status(controller.GetBook(4801)), Is.EqualTo(400));
            Assert.That(Status(controller.SetBook(4801, new TalearrBookGrabPolicyBody { AbridgedPolicy = "any" })), Is.EqualTo(400));
        }

        [Test]
        public void api_writes_a_null_override_explicitly()
        {
            var json = NzbDrone.Common.Serializer.STJson.ToJson(new TalearrBookGrabPolicyResource { BookId = 1, EffectivePolicy = "any", Source = "global" });

            Assert.That(json, Does.Contain("\"abridgedPolicy\": null").Or.Contain("\"abridgedPolicy\":null"));
        }

        // Edition choice before a search

        [Test]
        public void abridged_only_book_switches_to_an_abridged_edition_before_searching()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.MoffettAbridged().Id), "the best abridged edition, Audible-confirmed and in the book's language");
            Assert.That(book.Editions.Single(e => e.Monitored).Id, Is.EqualTo(FirmEditions.MoffettAbridged().Id), "the search sees the new edition");
            Assert.That(_history.Single().Data["Message"], Does.StartWith("TaleArr: monitored edition switched to the abridged edition"));
        }

        [Test]
        public void global_abridged_only_switches_too()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _global.Policy = new TalearrGrabPolicy { AbridgedPolicy = AbridgedAudiobookPolicy.AbridgedOnly };

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.TitledAbridged().Id), "the edition whose title says abridged");
        }

        [Test]
        public void per_book_unabridged_only_switches_away_from_an_abridged_edition()
        {
            var book = Firm(FirmEditions.TitledAbridged());
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
            _service.Set(book.Id, AbridgedAudiobookPolicy.UnabridgedOnly);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.ScottBrickUs().Id));
        }

        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase(AbridgedAudiobookPolicy.Any)]
        public void other_global_policies_never_switch(AbridgedAudiobookPolicy global)
        {
            var book = Firm(FirmEditions.TitledAbridged());
            _global.Policy = new TalearrGrabPolicy { AbridgedPolicy = global };

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.TitledAbridged().Id));
            Assert.That(_history, Is.Empty);
        }

        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase(AbridgedAudiobookPolicy.Any)]
        public void soft_per_book_policies_never_switch(AbridgedAudiobookPolicy policy)
        {
            var book = Firm(FirmEditions.TitledAbridged());
            _service.Set(book.Id, policy);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.TitledAbridged().Id));
        }

        [Test]
        public void an_already_abridged_edition_is_kept()
        {
            var book = Firm(FirmEditions.TitledAbridged());
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.TitledAbridged().Id));
            Assert.That(_history, Is.Empty);
        }

        [Test]
        public void no_abridged_edition_means_no_switch_and_a_history_note_once_a_day()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _world.Editions.RemoveAll(e => e.Id == FirmEditions.MoffettAbridged().Id || e.Id == FirmEditions.TitledAbridged().Id);
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Search(book);
            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.ScottBrickUs().Id));
            var note = _history.Single();
            Assert.That(note.EventType, Is.EqualTo(EntityHistoryEventType.DownloadIgnored), "an event type stock Chaptarr knows");
            Assert.That(note.BookId, Is.EqualTo(book.Id));
            Assert.That(note.Data["Message"], Is.EqualTo("TaleArr: no abridged edition of this book is known, so the search can only find releases marked abridged (abridged policy abridged only for this book)"));
        }

        [Test]
        public void a_book_with_files_keeps_its_edition()
        {
            var book = Firm(FirmEditions.ScottBrickUs(), withFiles: true);
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.ScottBrickUs().Id));
        }

        [Test]
        public void an_unmonitored_book_keeps_its_edition()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            book.AudiobookMonitored = false;
            book.Monitored = false;
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.ScottBrickUs().Id));
        }

        [Test]
        public void additional_recordings_keep_their_edition()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);
            book.TitleSlug = "the-firm_wanted_1";

            Search(book);

            Assert.That(Monitored(book.Id).Id, Is.EqualTo(FirmEditions.ScottBrickUs().Id));
        }

        [Test]
        public void the_search_hook_never_throws()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            var broken = new Lazy<ITalearrAbridgedEditionSwitch>(() => throw new InvalidOperationException("container cycle"));

            Assert.DoesNotThrow(() => TalearrAbridgedEditionSwitch.ApplyBeforeSearch(broken, book, LogManager.GetCurrentClassLogger()));
            Assert.DoesNotThrow(() => TalearrAbridgedEditionSwitch.ApplyBeforeSearch(null, book, LogManager.GetCurrentClassLogger()));
        }

        // Automatic retry

        [Test]
        public void retry_note_names_the_book_policy()
        {
            var book = Firm(FirmEditions.ScottBrickUs());
            var (historyService, _) = HistoryServiceProxy.Create();
            var retry = new TalearrRetryService(historyService, _global, LogManager.GetCurrentClassLogger(), _service, _world.BookService);
            var failed = new DownloadFailedEvent { BookIds = new List<int> { book.Id }, SourceTitle = "John Grisham - The Firm" };

            Assert.That(retry.Evaluate(failed).Note, Does.Not.Contain("abridged policy"));

            _service.Set(book.Id, AbridgedAudiobookPolicy.AbridgedOnly);
            var decision = retry.Evaluate(failed);

            Assert.That(decision.Allowed, Is.True);
            Assert.That(decision.Note, Does.EndWith("; abridged policy for this book: abridged only"));
        }

        // Audiobookshelf owned copies

        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedMarker.Unabridged, false)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedMarker.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedMarker.Abridged, false)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedMarker.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, AbridgedMarker.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, AbridgedMarker.Abridged, false)]
        public void audiobookshelf_copy_counts_by_the_book_policy(AbridgedAudiobookPolicy policy, AbridgedMarker copy, bool owned)
        {
            using var abs = new AbsWorld();
            abs.AddConnection();
            var firm = abs.AddBook(3401, 7, "The Firm", BookMediaType.Audiobook, "hc:378630");
            abs.AddEdition(firm, 1, true, durationSeconds: 61800, narrator: "Scott Brick");

            var books = new FakeBookGrabPolicyService();
            books.Overrides["hc:378630"] = policy;
            var library = new TalearrAbsLibraryService(abs.Store, abs.Connections, abs.Groups, abs.MediaFiles, abs.EditionService, LogManager.GetCurrentClassLogger(), abs.Resolver, abs.GrabPolicy, books);
            abs.Cache(1, AbsItems.Audio("li-firm", "The Firm", "John Grisham", abridged: copy));

            Assert.That(library.FindOwnedCopy(firm) != null, Is.EqualTo(owned));
        }
    }
}
