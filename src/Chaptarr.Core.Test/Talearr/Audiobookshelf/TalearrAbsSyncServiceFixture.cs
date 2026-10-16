using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.ThingiProvider.Events;
using FakeHttp = Chaptarr.Core.Test.Talearr.Recordings.FakeAudibleHttp;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrAbsSyncServiceFixture
    {
        private AbsWorld _world;
        private FakeHttp _http;
        private TalearrAbsSyncService _service;
        private TalearrAbsConnection _connection;

        [SetUp]
        public void SetUp()
        {
            _world = new AbsWorld();

            var firmEbook = _world.AddBook(3402, 7, "The Firm", BookMediaType.Ebook, "hc:378630");
            _world.AddEdition(firmEbook, 3, true, isbn13: "9780307576149");
            var animalFarm = _world.AddBook(4001, 8, "Animal Farm", BookMediaType.Audiobook, "hc:animal-farm");
            _world.AddEdition(animalFarm, 41, true, asin: "B0000545BA");
            var poppy = _world.AddBook(3600, 9, "The Poppy War", BookMediaType.Ebook, "hc:poppy");
            _world.AddEdition(poppy, 30, true);

            var settings = new AudioBookShelfSettings { Host = "audiobookshelf", Port = 13378, ApiKey = "abs-key" };
            settings.SetLibraryMappings(new List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping { RootFolderId = 3, MediaType = "ebook", LibraryId = AbsFixtures.EbookLibraryId, LibraryFolderId = AbsFixtures.EbookFolderId, LibraryFolderPath = "/books" },
                new AudioBookShelfLibraryMapping { RootFolderId = 4, MediaType = "audiobook", LibraryId = AbsFixtures.AudiobookLibraryId, LibraryFolderId = AbsFixtures.AudiobookFolderId, LibraryFolderPath = "/audiobooks" }
            });
            _connection = _world.AddConnection(settings: settings);

            var (client, fake) = FakeHttp.Create();
            _http = fake;
            ServeLibrary();

            var (proxy, proxyFake) = InterfaceFake<IAudioBookShelfProxy>.Create();
            proxyFake.On(nameof(IAudioBookShelfProxy.GetLibraries), _ => Json.Deserialize<AbsLibrariesResponse>(AbsFixtures.Libraries).Libraries);

            _service = new TalearrAbsSyncService(_world.Connections,
                                                 proxy,
                                                 new TalearrAbsClient(client, LogManager.GetCurrentClassLogger()),
                                                 _world.Store,
                                                 _world.Library,
                                                 _world.RootFolderService,
                                                 _world.AuthorService,
                                                 _world.BookService,
                                                 _world.EditionService,
                                                 _world.MediaFiles,
                                                 _world.CommandQueue,
                                                 LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private void ServeLibrary()
        {
            _http.Routes.Clear();
            _http.On($"/api/libraries/{AbsFixtures.AudiobookLibraryId}/items", AbsFixtures.AudiobookItems);
            _http.On($"/api/libraries/{AbsFixtures.EbookLibraryId}/items", AbsFixtures.EbookItemsSinglePage);
        }

        [Test]
        public void should_cache_the_mapped_libraries_and_match_them_to_books()
        {
            _service.Execute(new SyncAudiobookshelfLibraryCommand());

            var items = _world.Store.GetItems();
            var state = _world.Store.GetStates().Single();

            Assert.Multiple(() =>
            {
                Assert.That(items.Select(i => i.ItemId), Is.EquivalentTo(new[] { "li-steve-jobs", "li-animal-farm-cosham", "li-firm-abridged", "li-poppy-war-ebook", "li-firm-ebook" }), "missing items and the podcast library are skipped");
                Assert.That(items.Single(i => i.ItemId == "li-poppy-war-ebook").LocalPath, Is.EqualTo("/ebooks/R.F. Kuang/The Poppy War"));
                Assert.That(_world.Library.GetItems(3402).Single().Match.Method, Is.EqualTo(TalearrAbsMatchMethods.Isbn));
                Assert.That(_world.Library.GetItems(4001).Single().Match.Method, Is.EqualTo(TalearrAbsMatchMethods.Asin));
                Assert.That(_world.Library.GetItems(3600).Single().Match.Method, Is.EqualTo(TalearrAbsMatchMethods.Title));
                Assert.That(state.ItemCount, Is.EqualTo(5));
                Assert.That(state.MatchedCount, Is.EqualTo(3));
                Assert.That(state.LastError, Is.Null);
                Assert.That(_http.Requests.All(r => r.Method == HttpMethod.Get), Is.True, "read only");
                Assert.That(_http.Requests.Any(r => r.Url.FullUri.Contains("lib-podcasts")), Is.False);
            });
        }

        [Test]
        public void should_keep_the_previous_copy_when_audiobookshelf_fails()
        {
            _service.Execute(new SyncAudiobookshelfLibraryCommand());

            _http.Routes.Clear();
            _http.On("/items", "oops", HttpStatusCode.InternalServerError);

            Assert.Throws<TalearrAbsSyncException>(() => _service.Execute(new SyncAudiobookshelfLibraryCommand()));

            var state = _world.Store.GetStates().Single();
            Assert.Multiple(() =>
            {
                Assert.That(_world.Store.GetItems().Count, Is.EqualTo(5));
                Assert.That(state.LastError, Does.Contain("500"));
                Assert.That(state.ItemCount, Is.EqualTo(5));
                Assert.That(_world.Library.GetItems(3402), Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void should_forget_a_connection_when_sync_is_turned_off()
        {
            _service.Execute(new SyncAudiobookshelfLibraryCommand());
            _connection.Settings.TalearrSyncLibrary = false;

            _service.Execute(new SyncAudiobookshelfLibraryCommand());

            Assert.That(_world.Store.GetItems(), Is.Empty);
            Assert.That(_world.Store.GetStates(), Is.Empty);
        }

        [Test]
        public void should_queue_a_sync_when_an_audiobookshelf_connection_is_saved()
        {
            _service.Handle(new ProviderUpdatedEvent<INotification>(new NotificationDefinition { Id = 1, Implementation = "AudioBookShelf", Settings = _connection.Settings }));
            _service.Handle(new ProviderUpdatedEvent<INotification>(new NotificationDefinition { Id = 2, Implementation = "Pushover" }));

            Assert.That(_world.Commands.OfType<SyncAudiobookshelfLibraryCommand>().Select(c => c.ConnectionId), Is.EqualTo(new int?[] { 1 }));
        }

        [Test]
        public void should_read_every_book_library_when_nothing_is_mapped()
        {
            var settings = new AudioBookShelfSettings();
            var libraries = Json.Deserialize<AbsLibrariesResponse>(AbsFixtures.Libraries).Libraries.Where(l => l.MediaType == "book").ToList();

            Assert.That(TalearrAbsSyncService.SelectLibraries(settings, libraries).Select(l => l.Name), Is.EqualTo(new[] { "Audiobooks", "Ebooks" }));

            settings.EbookLibraryId = AbsFixtures.EbookLibraryId;
            Assert.That(TalearrAbsSyncService.SelectLibraries(settings, libraries).Select(l => l.Name), Is.EqualTo(new[] { "Ebooks" }), "legacy single library pick");
        }
    }
}
