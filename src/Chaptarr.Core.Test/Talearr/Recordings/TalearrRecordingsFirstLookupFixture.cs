using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // TaleArr: AllSeerr's "both" mode asks for a book's recordings right after adding it, before
    // the background Audible lookup has run. The API GET does that first lookup itself, briefly.
    [TestFixture]
    public class TalearrRecordingsFirstLookupFixture
    {
        private RecordingsWorld _world;
        private int _resolveCalls;
        private Action _onResolve;
        private TalearrRecordingsService _service;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _resolveCalls = 0;
            _onResolve = () => { };

            var (facts, fake) = InterfaceFake<ITalearrAudibleFactsService>.Create();
            fake.On(nameof(ITalearrAudibleFactsService.GetCachedFacts), args => _world.Facts.GetCachedFacts((IEnumerable<Edition>)args[0], (string)args[1]));
            fake.On(nameof(ITalearrAudibleFactsService.Resolve), _ =>
            {
                Interlocked.Increment(ref _resolveCalls);
                _onResolve();
                return new AudibleResolveSummary();
            });

            var resolver = new TalearrAbridgedResolver(facts, _world.MediaFiles, LogManager.GetCurrentClassLogger());
            _service = new TalearrRecordingsService(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Groups, facts, resolver, _world.Store, _world.Settings, _world.CommandQueue, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private void LinkAll()
        {
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
            _world.LinkAudible(FirmEditions.ScottBrickUk(), "B00NIXU8GW", "unabridged", 1029, "Scott Brick");
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            _world.LinkAudible(FirmEditions.TitledAbridged(), "B000TITLED", "abridged", 355, "Unknown");
        }

        [Test]
        public void a_new_book_gets_its_first_audible_lookup_and_lists_abridged_candidates()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            book.BaseBookId = "hc:first-lookup-" + Guid.NewGuid().ToString("N"); // lookups are remembered per work
            _onResolve = LinkAll;

            var view = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(5));

            Assert.That(_resolveCalls, Is.EqualTo(1));
            Assert.That(view.FactsPending, Is.False);
            var moffett = view.Candidates.Single(c => c.EditionIds.Contains(FirmEditions.MoffettAbridged().Id));
            Assert.That(moffett.Abridged, Is.EqualTo("abridged"));
            Assert.That(moffett.AbridgedSource, Is.EqualTo(AbridgedSources.Audible));
        }

        [Test]
        public void a_failed_first_lookup_is_not_repeated_on_every_visit()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            book.BaseBookId = "hc:cooldown-" + Guid.NewGuid().ToString("N");

            _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(5));
            var second = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(5));

            Assert.That(_resolveCalls, Is.EqualTo(1), "Audible answered nothing (throttled); the next visit does not wait again");
            Assert.That(second.FactsPending, Is.False);
        }

        [Test]
        public void a_book_already_looked_up_answers_from_the_cache()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            LinkAll();

            var view = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(5));

            Assert.That(_resolveCalls, Is.EqualTo(0));
            Assert.That(view.FactsPending, Is.False);
        }

        [Test]
        public void a_slow_lookup_answers_in_time_and_says_it_is_pending()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            book.BaseBookId = "hc:slow-lookup-test"; // its background lookup must not be shared with other tests
            using var gate = new ManualResetEventSlim(false);
            _onResolve = () => gate.Wait(TimeSpan.FromSeconds(10));

            var started = DateTime.UtcNow;
            var view = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromMilliseconds(200));

            Assert.That(DateTime.UtcNow - started, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(view.FactsPending, Is.True);
            Assert.That(view.Recordings, Is.Not.Empty, "the answer still lists what is known");
            gate.Set();
        }

        [Test]
        public void a_book_whose_editions_are_not_known_yet_says_it_is_pending()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            _world.Editions.RemoveAll(e => e.BookId == book.Id);

            var view = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(1));

            Assert.That(view.FactsPending, Is.True);
            Assert.That(view.Candidates, Is.Empty);
            Assert.That(_resolveCalls, Is.EqualTo(0));
        }

        [Test]
        public void no_lookup_when_audible_facts_are_off()
        {
            var book = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            var settings = _world.Settings.Get();
            settings.Provider = TalearrAudibleProvider.Off;
            _world.Settings.Save(settings);

            var view = _service.GetRecordingsWithFacts(book, true, TimeSpan.FromSeconds(1));

            Assert.That(_resolveCalls, Is.EqualTo(0));
            Assert.That(view.FactsPending, Is.False);
        }
    }
}
