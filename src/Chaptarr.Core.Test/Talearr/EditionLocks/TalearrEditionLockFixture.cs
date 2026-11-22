using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.EditionLocks;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.EditionLocks
{
    internal sealed class FakeEditionLockLibrary : ITalearrEditionLockLibrary
    {
        public List<Book> Books { get; } = new List<Book>();
        public List<Edition> Editions { get; } = new List<Edition>();
        public Dictionary<int, int> Files { get; } = new Dictionary<int, int>();

        public Book GetBook(int bookId) => Books.FirstOrDefault(b => b.Id == bookId);
        public List<Edition> GetEditions(int bookId) => Editions.Where(e => e.BookId == bookId).ToList();
        public int FileCount(int editionId) => Files.TryGetValue(editionId, out var count) ? count : 0;

        public void SaveAnyEditionOk(Book book)
        {
        }

        public void SaveEditions(List<Edition> editions)
        {
        }
    }

    [TestFixture]
    public class TalearrEditionLockFixture
    {
        private TalearrTestFolder _folder;
        private TalearrEditionLockStore _store;
        private FakeEditionLockLibrary _library;
        private TalearrEditionLockService _service;

        private Book _book;
        private Edition _chosen;
        private Edition _other;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = new TalearrEditionLockStore(_folder.CreateDatabase(), LogManager.GetLogger("TalearrTest"));
            _library = new FakeEditionLockLibrary();
            _service = new TalearrEditionLockService(_store, _library, LogManager.GetLogger("TalearrTest"));

            _book = new Book { Id = 5, Title = "Dune", AnyEditionOk = true, MediaType = BookMediaType.Audiobook };
            _library.Books.Add(_book);
            _chosen = AddEdition(50, "hc:edition:500", monitored: true);
            _other = AddEdition(51, "hc:edition:501", monitored: false);
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private Edition AddEdition(int id, string foreignEditionId, bool monitored)
        {
            var edition = new Edition { Id = id, BookId = _book.Id, ForeignEditionId = foreignEditionId, Title = "Dune " + id, Monitored = monitored };
            _library.Editions.Add(edition);
            return edition;
        }

        [Test]
        public void a_book_that_is_not_locked_switches_as_before()
        {
            Assert.That(_service.IsLocked(_book.Id), Is.False);
            Assert.That(_service.AllowsAutomaticSwitch(_other), Is.True);
            Assert.That(_service.KeepLockedEdition(_library.Editions), Is.False);
            Assert.That(_service.Get(_book.Id).Locked, Is.False);
        }

        [Test]
        public void locking_keeps_the_monitored_edition_and_turns_automatic_switching_off()
        {
            var result = _service.Set(_book.Id, true);

            Assert.That(result.Status, Is.EqualTo(TalearrEditionLockStatus.Ok));
            Assert.Multiple(() =>
            {
                Assert.That(result.Details.Locked, Is.True);
                Assert.That(result.Details.EditionId, Is.EqualTo(_chosen.Id));
                Assert.That(_book.AnyEditionOk, Is.False);
                Assert.That(_store.GetAll()[_book.Id].EditionKey, Is.EqualTo("hc:edition:500"));
            });

            Assert.That(_service.AllowsAutomaticSwitch(_other), Is.False);
            Assert.That(_service.AllowsAutomaticSwitch(_chosen), Is.True);
        }

        [Test]
        public void a_book_without_a_single_monitored_edition_cannot_be_locked()
        {
            _chosen.Monitored = false;

            Assert.That(_service.Set(_book.Id, true).Status, Is.EqualTo(TalearrEditionLockStatus.Invalid));
            Assert.That(_service.IsLocked(_book.Id), Is.False);
        }

        [Test]
        public void a_refresh_that_moves_the_monitored_flag_is_put_back_even_for_a_recreated_edition_row()
        {
            _service.Set(_book.Id, true);

            // The refresh recreated the locked edition (new object, same provider id) and its
            // repair picked the other one.
            var recreated = new Edition { Id = 0, BookId = _book.Id, ForeignEditionId = "hc:edition:500", Monitored = false };
            var other = new Edition { Id = 51, BookId = _book.Id, ForeignEditionId = "hc:edition:501", Monitored = true };
            var all = new List<Edition> { other, recreated };

            Assert.That(_service.KeepLockedEdition(all), Is.True);
            Assert.That(recreated.Monitored, Is.True);
            Assert.That(other.Monitored, Is.False);
        }

        [Test]
        public void a_file_imported_for_another_edition_moves_the_lock_so_the_book_is_not_searched_again()
        {
            _service.Set(_book.Id, true);
            _library.Files[_other.Id] = 3;

            Assert.That(_service.AllowsAutomaticSwitch(_other), Is.True);
            Assert.That(_store.GetAll()[_book.Id].EditionIdHint, Is.EqualTo(_other.Id));
        }

        [Test]
        public void the_users_own_pick_moves_the_lock()
        {
            _service.Set(_book.Id, true);
            _service.FollowManualSelection(_book.Id, _other.Id);

            Assert.That(_service.AllowsAutomaticSwitch(_chosen), Is.False);
            Assert.That(_service.Get(_book.Id).EditionId, Is.EqualTo(_other.Id));
        }

        [Test]
        public void reapply_turns_automatic_switching_off_again_and_unlocking_leaves_the_edition()
        {
            _service.Set(_book.Id, true);
            _book.AnyEditionOk = true;
            _chosen.Monitored = false;
            _other.Monitored = true;

            _service.Reapply(new[] { _book });

            Assert.That(_book.AnyEditionOk, Is.False);
            Assert.That(_chosen.Monitored, Is.True);
            Assert.That(_other.Monitored, Is.False);

            _service.Set(_book.Id, false);

            Assert.That(_service.IsLocked(_book.Id), Is.False);
            Assert.That(_chosen.Monitored, Is.True);
            Assert.That(_store.GetAll(), Is.Empty);
        }

        [Test]
        public void edition_service_does_not_switch_a_locked_edition_automatically()
        {
            _service.Set(_book.Id, true);

            var (repository, fake) = InterfaceFake<IEditionRepository>.Create();
            fake.On(nameof(IEditionRepository.FindByBook), _ => _library.Editions.ToList())
                .On(nameof(IEditionRepository.SetMonitored), _ => _library.Editions.ToList());

            var (events, _) = InterfaceFake<IEventAggregator>.Create();
            var editionService = new EditionService(repository, events, LogManager.GetLogger("TalearrTest"), _service);

            editionService.SetMonitored(_other, false);
            Assert.That(fake.Calls, Does.Not.Contain(nameof(IEditionRepository.SetMonitored)));

            editionService.SetMonitored(_other, true);
            Assert.That(fake.Calls, Does.Contain(nameof(IEditionRepository.SetMonitored)));
            Assert.That(_service.Get(_book.Id).EditionId, Is.EqualTo(_other.Id), "a manual pick moves the lock");
        }

        [Test]
        public void schema_fourteen_adds_the_edition_lock_table()
        {
            Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.GreaterThanOrEqualTo(14));
        }
    }
}
