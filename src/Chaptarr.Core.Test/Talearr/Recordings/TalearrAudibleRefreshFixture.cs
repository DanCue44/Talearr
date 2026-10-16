using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrAudibleRefreshFixture
    {
        private TalearrTestFolder _folder;
        private FakeAudibleHttp _http;
        private TalearrRecordingsSettingsService _settings;
        private TalearrAudibleRefreshService _subject;
        private List<Edition> _editions;
        private Book _book;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            var store = _folder.CreateStore();
            var (http, fake) = FakeAudibleHttp.Create();
            _http = fake;
            _http.On("api.audible.com/1.0/catalog/products?asins=", AudibleRecordings.UsBatch);
            _http.On("api.audible.com/1.0/catalog/products?title=", AudibleRecordings.UsSearchTheFirm);

            var (repository, _) = ConfigRepositoryProxy.Create();
            _settings = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());
            var client = new TalearrAudibleClient(http, LogManager.GetCurrentClassLogger());
            var facts = new TalearrAudibleFactsService(client, store, _settings, LogManager.GetCurrentClassLogger());

            _book = new Book { Id = 3401, AuthorId = 7, Title = "The Firm", MediaType = BookMediaType.Audiobook, Monitored = true, AudiobookMonitored = true };
            _editions = FirmEditions.All();

            var (books, bookFake) = InterfaceFake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBooks), args => new List<Book> { _book }.Where(b => ((IEnumerable<int>)args[0]).Contains(b.Id)).ToList());
            bookFake.On(nameof(IBookService.GetAllBooks), _ => new List<Book> { _book });

            var (editions, editionFake) = InterfaceFake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetAllMonitoredEditions), _ => _editions.Where(e => e.Monitored).ToList());
            editionFake.On(nameof(IEditionService.GetEditionsByBook), args => _editions.Where(e => ((IEnumerable<int>)args[0]).Contains(e.BookId)).ToList());

            var (authors, authorFake) = InterfaceFake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAuthor), _ => new Author { Id = 7, Name = "John Grisham" });

            _subject = new TalearrAudibleRefreshService(books, editions, authors, facts, store, _settings, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void scheduled_run_covers_monitored_editions_only_by_default()
        {
            _subject.Execute(new RefreshAudibleRecordingFactsCommand());

            Assert.That(_http.CountTo("asins="), Is.EqualTo(1));
            Assert.That(_http.CountTo("title="), Is.EqualTo(0), "the monitored edition resolves through its ASIN list");
        }

        [Test]
        public void scheduled_run_is_incremental()
        {
            _subject.Execute(new RefreshAudibleRecordingFactsCommand());
            var after = _http.Requests.Count;

            _subject.Execute(new RefreshAudibleRecordingFactsCommand());

            Assert.That(_http.Requests.Count, Is.EqualTo(after), "fresh cache rows mean no requests");
        }

        [Test]
        public void enrich_all_covers_every_audiobook_edition_of_monitored_books()
        {
            _settings.Save(new TalearrRecordingsSettings { Enrich = TalearrEnrichScope.All });

            _subject.Execute(new RefreshAudibleRecordingFactsCommand());

            Assert.That(_http.CountTo("asins="), Is.EqualTo(1));
            Assert.That(_http.CountTo("title="), Is.EqualTo(1));
        }

        [Test]
        public void enrich_off_skips_the_scheduled_run_but_not_an_on_demand_refresh()
        {
            _settings.Save(new TalearrRecordingsSettings { Enrich = TalearrEnrichScope.Off });

            _subject.Execute(new RefreshAudibleRecordingFactsCommand());
            Assert.That(_http.Requests, Is.Empty);

            _subject.Execute(new RefreshAudibleRecordingFactsCommand(new List<int> { 3401 }, force: true));
            Assert.That(_http.Requests, Is.Not.Empty);
        }

        [Test]
        public void on_demand_refresh_ignores_the_cache()
        {
            _subject.Execute(new RefreshAudibleRecordingFactsCommand(new List<int> { 3401 }, force: true));
            var first = _http.CountTo("asins=");

            _subject.Execute(new RefreshAudibleRecordingFactsCommand(new List<int> { 3401 }, force: true));

            Assert.That(_http.CountTo("asins="), Is.EqualTo(first * 2));
        }

        [Test]
        public void provider_off_does_nothing()
        {
            _settings.Save(new TalearrRecordingsSettings { Provider = TalearrAudibleProvider.Off });

            _subject.Execute(new RefreshAudibleRecordingFactsCommand(new List<int> { 3401 }, force: true));

            Assert.That(_http.Requests, Is.Empty);
        }

        [Test]
        public void command_name_shown_in_tasks_has_no_misspelled_brand()
        {
            var command = new RefreshAudibleRecordingFactsCommand();

            Assert.That(command.Name, Is.EqualTo("RefreshAudibleRecordingFacts"));
            Assert.That(command.UpdateScheduledTask, Is.True);
            Assert.That(new RefreshAudibleRecordingFactsCommand(new List<int> { 1 }, true).UpdateScheduledTask, Is.False);
        }
    }
}
