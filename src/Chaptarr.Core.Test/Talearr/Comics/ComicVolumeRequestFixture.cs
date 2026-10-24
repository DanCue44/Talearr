using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Talearr.Recordings;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Metadata;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    // Request apps asking for named volumes: on add (only those monitored) and on a series already
    // in the library (POST series/{id}/want).
    [TestFixture]
    public class ComicVolumeRequestFixture
    {
        private const int HardcoverId = 77;

        private string _folder;
        private TalearrComicsStore _store;
        private ComicSettingsService _settings;
        private RecordingQueue _queue;
        private ComicSeriesService _service;
        private int _rootFolderId;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "talearr-comics-want-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _store = new TalearrComicsStore(new TalearrComicsDatabase(new TestAppFolderInfo(_folder), LogManager.GetLogger("TalearrTest")));
            _settings = new ComicSettingsService(_store);
            _queue = new RecordingQueue();
            _service = new ComicSeriesService(_store, _settings, new NoAniList(), new FakeHardcover(), new NoMangaDex(), null, _queue, LogManager.GetLogger("TalearrTest"));
            _rootFolderId = _store.AddRootFolder(new ComicRootFolder { Path = Path.Combine(_folder, "comics"), Kind = "any" }).Id;
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private ComicAddRequest Request(string monitor = "all", params decimal[] volumes)
        {
            return new ComicAddRequest
            {
                HardcoverSeriesId = HardcoverId,
                Kind = ComicKinds.Comic,
                RootFolderId = _rootFolderId,
                Monitor = monitor,
                Volumes = volumes.Length > 0 ? volumes.ToList() : null
            };
        }

        private Dictionary<decimal, ComicVolume> Volumes(int seriesId)
        {
            return _store.GetVolumes(seriesId).ToDictionary(v => v.Number);
        }

        private List<SearchMissingComicsCommand> Searches()
        {
            return _queue.Pushed.OfType<SearchMissingComicsCommand>().ToList();
        }

        private void GiveFile(int seriesId, decimal number)
        {
            var volume = Volumes(seriesId)[number];
            _store.AddFile(new ComicFile { SeriesId = seriesId, VolumeId = volume.Id, Path = Path.Combine(_folder, $"v{number}.cbz"), Size = 10, Format = ComicFormats.Cbz, Source = ComicSources.Digital, DateAdded = DateTime.UtcNow });
        }

        [Test]
        public void add_with_volumes_should_monitor_only_those_and_search_only_them()
        {
            var series = _service.Add(Request("all", 1m, 2.0m, 0m));

            var volumes = Volumes(series.Id);
            Assert.That(volumes.Keys, Is.EquivalentTo(new[] { 0m, 1m, 1.5m, 2m, 3m, 4m }), "the volume rows exist when Add returns");
            Assert.That(volumes.Values.Where(v => v.Monitored).Select(v => v.Number), Is.EquivalentTo(new[] { 0m, 1m, 2m }));

            var stored = _store.GetSeries(series.Id);
            Assert.That(stored.Monitored, Is.True);
            Assert.That(stored.MonitorNewVolumes, Is.False);

            Assert.That(series.Wanted, Is.EqualTo(new[] { 1m, 2m, 0m }));
            Assert.That(series.Unknown, Is.Empty);
            Assert.That(series.Id, Is.GreaterThan(0));
            Assert.That(series.Title, Is.EqualTo("Saga"));

            var search = Searches().Single();
            Assert.That(search.SeriesId, Is.EqualTo(series.Id));
            Assert.That(search.VolumeIds, Is.EquivalentTo(new[] { volumes[0m].Id, volumes[1m].Id, volumes[2m].Id }));
        }

        [Test]
        public void add_with_volumes_should_report_unknown_numbers_and_skip_unreleased_in_the_search()
        {
            var series = _service.Add(Request("none", 1m, 4m, 9m, 1.25m));

            Assert.That(series.Wanted, Is.EqualTo(new[] { 1m, 4m }));
            Assert.That(series.Unknown, Is.EqualTo(new[] { 9m, 1.25m }));

            var volumes = Volumes(series.Id);
            Assert.That(volumes.Values.Where(v => v.Monitored).Select(v => v.Number), Is.EquivalentTo(new[] { 1m, 4m }));
            Assert.That(_store.GetSeries(series.Id).Monitored, Is.True, "named volumes win over Monitor none");

            // Volume 4 is not out yet: wanted, monitored, but not searched now.
            Assert.That(Searches().Single().VolumeIds, Is.EqualTo(new[] { volumes[1m].Id }));
        }

        [Test]
        public void add_with_volumes_should_search_even_when_automatic_search_is_off()
        {
            var settings = _settings.Get();
            settings.AutomaticSearch = false;
            _settings.Save(settings);

            _service.Add(Request("all", 3m));

            Assert.That(Searches(), Has.Count.EqualTo(1));
        }

        [Test]
        public void add_with_only_unknown_volumes_should_monitor_nothing_and_not_search()
        {
            var series = _service.Add(Request("all", 42m));

            Assert.That(series.Wanted, Is.Empty);
            Assert.That(series.Unknown, Is.EqualTo(new[] { 42m }));
            Assert.That(Volumes(series.Id).Values.Any(v => v.Monitored), Is.False);
            Assert.That(Searches(), Is.Empty);
        }

        [TestCase("all", true, true, 6, true)]
        [TestCase("future", true, true, 0, false)]
        [TestCase("none", false, false, 0, false)]
        public void add_without_volumes_should_keep_the_monitor_rules(string monitor, bool seriesMonitored, bool monitorNew, int monitoredVolumes, bool searched)
        {
            var series = _service.Add(Request(monitor));

            var stored = _store.GetSeries(series.Id);
            Assert.That(stored.Monitored, Is.EqualTo(seriesMonitored));
            Assert.That(stored.MonitorNewVolumes, Is.EqualTo(monitorNew));
            Assert.That(Volumes(series.Id).Values.Count(v => v.Monitored), Is.EqualTo(monitoredVolumes));
            Assert.That(series.Wanted, Is.Null);
            Assert.That(series.Unknown, Is.Null);

            var search = Searches();
            Assert.That(search, Has.Count.EqualTo(searched ? 1 : 0));
            if (searched)
            {
                Assert.That(search[0].SeriesId, Is.EqualTo(series.Id));
                Assert.That(search[0].VolumeIds, Is.Null, "the whole series, as before");
            }
        }

        [Test]
        public void add_with_an_empty_volume_list_should_behave_like_no_list()
        {
            var request = Request("all");
            request.Volumes = new List<decimal>();
            var series = _service.Add(request);

            Assert.That(Volumes(series.Id).Values.All(v => v.Monitored), Is.True);
            Assert.That(_store.GetSeries(series.Id).MonitorNewVolumes, Is.True);
            Assert.That(Searches().Single().VolumeIds, Is.Null);
        }

        [Test]
        public void want_should_monitor_named_volumes_keep_others_and_split_have_and_unknown()
        {
            var series = _service.Add(Request("all", 1m));
            _store.SetVolumesMonitored(new[] { Volumes(series.Id)[1.5m].Id }, true);
            GiveFile(series.Id, 2m);
            _queue.Pushed.Clear();

            var result = _service.WantVolumes(series.Id, new[] { 2m, 3.0m, 7m, 0m, 3m }, search: true);

            Assert.That(result.SeriesId, Is.EqualTo(series.Id));
            Assert.That(result.Wanted, Is.EqualTo(new[] { 3m, 0m }), "1 and 1.0 style duplicates count once");
            Assert.That(result.AlreadyHave, Is.EqualTo(new[] { 2m }));
            Assert.That(result.Unknown, Is.EqualTo(new[] { 7m }));

            var volumes = Volumes(series.Id);
            Assert.That(volumes.Values.Where(v => v.Monitored).Select(v => v.Number), Is.EquivalentTo(new[] { 0m, 1m, 1.5m, 2m, 3m }), "earlier choices are kept");
            Assert.That(volumes[4m].Monitored, Is.False);

            var search = Searches().Single();
            Assert.That(search.SeriesId, Is.EqualTo(series.Id));
            Assert.That(search.VolumeIds, Is.EquivalentTo(new[] { volumes[3m].Id, volumes[0m].Id }), "only the missing wanted volumes");
        }

        [Test]
        public void want_without_search_should_only_monitor()
        {
            var series = _service.Add(Request("none"));
            _queue.Pushed.Clear();

            var result = _service.WantVolumes(series.Id, new[] { 1m, 2m }, search: false);

            Assert.That(result.Wanted, Is.EqualTo(new[] { 1m, 2m }));
            Assert.That(Volumes(series.Id).Values.Where(v => v.Monitored).Select(v => v.Number), Is.EquivalentTo(new[] { 1m, 2m }));
            Assert.That(_store.GetSeries(series.Id).Monitored, Is.True, "wanting volumes monitors the series");
            Assert.That(_store.GetSeries(series.Id).MonitorNewVolumes, Is.False, "new-volume monitoring is left alone");
            Assert.That(Searches(), Is.Empty);
        }

        [Test]
        public void want_should_not_search_when_every_named_volume_is_already_here()
        {
            var series = _service.Add(Request("none"));
            GiveFile(series.Id, 1m);
            _queue.Pushed.Clear();

            var result = _service.WantVolumes(series.Id, new[] { 1m }, search: true);

            Assert.That(result.AlreadyHave, Is.EqualTo(new[] { 1m }));
            Assert.That(result.Wanted, Is.Empty);
            Assert.That(Volumes(series.Id)[1m].Monitored, Is.True);
            Assert.That(Searches(), Is.Empty);
        }

        [Test]
        public void want_should_refuse_an_unknown_series_and_an_empty_list()
        {
            Assert.Throws<ComicSeriesNotFoundException>(() => _service.WantVolumes(999, new[] { 1m }, search: true));

            var series = _service.Add(Request("none"));
            Assert.Throws<ComicsException>(() => _service.WantVolumes(series.Id, new decimal[0], search: true));
            Assert.Throws<ComicsException>(() => _service.WantVolumes(series.Id, null, search: true));
        }

        private class FakeHardcover : IComicHardcoverClient
        {
            public bool IsConfigured => true;

            public void FillCovers(IList<HardcoverComicSeries> series)
            {
            }

            public List<HardcoverComicSeries> SearchSeries(string query, bool withCovers = true) => new List<HardcoverComicSeries>();

            public HardcoverComicSeries GetSeries(int id)
            {
                if (id != HardcoverId)
                {
                    return null;
                }

                var series = new HardcoverComicSeries { Id = HardcoverId, Name = "Saga", AuthorName = "Brian K. Vaughan", BooksCount = 6, PrimaryBooksCount = 6 };
                series.Volumes.Add(new HardcoverComicVolume { BookId = 100, Number = 0, Title = "Saga: Prologue", ReleaseDate = new DateTime(2012, 1, 1) });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 101, Number = 1, Title = "Saga, Vol. 1", ReleaseDate = new DateTime(2012, 10, 1) });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 102, Number = 1.5m, Title = "Saga, Vol. 1.5" });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 103, Number = 2, Title = "Saga, Vol. 2", ReleaseDate = new DateTime(2013, 7, 1) });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 104, Number = 3, Title = "Saga, Vol. 3" });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 105, Number = 4, Title = "Saga, Vol. 4", ReleaseDate = DateTime.UtcNow.AddYears(1) });
                return series;
            }
        }

        private class NoAniList : IAniListClient
        {
            public List<AniListManga> Search(string query) => throw new NotSupportedException();

            public AniListManga Get(int id) => throw new NotSupportedException();

            public AniListManga GetPreview(int id) => throw new NotSupportedException();

            public List<AniListManga> Browse(string sort, string status, int page, int perPage, int? minPopularity = null) => throw new NotSupportedException();
        }

        private class NoMangaDex : IMangaDexClient
        {
            public List<MangaDexManga> Search(string title) => new List<MangaDexManga>();
        }

        private sealed class RecordingQueue : IManageCommandQueue
        {
            public List<Command> Pushed { get; } = new List<Command>();

            public List<CommandModel> PushMany<TCommand>(List<TCommand> commands)
                where TCommand : Command => commands.Select(c => Push(c)).ToList();

            public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
                where TCommand : Command
            {
                Pushed.Add(command);
                return new CommandModel { Body = command };
            }

            public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotImplementedException();
            public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotImplementedException();
            public List<CommandModel> All() => throw new NotImplementedException();
            public CommandModel Get(int id) => throw new NotImplementedException();
            public List<CommandModel> GetStarted() => throw new NotImplementedException();
            public void SetMessage(CommandModel command, string message) => throw new NotImplementedException();
            public void TouchProgress(CommandModel command) => throw new NotImplementedException();
            public void SetResult(CommandModel command, CommandResult result) => throw new NotImplementedException();
            public void Start(CommandModel command) => throw new NotImplementedException();
            public void Complete(CommandModel command, string message) => throw new NotImplementedException();
            public void Fail(CommandModel command, string message, Exception e) => throw new NotImplementedException();
            public void Requeue() => throw new NotImplementedException();
            public void Cancel(int id) => throw new NotImplementedException();
            public void Pause(int id) => throw new NotImplementedException();
            public void Resume(int id) => throw new NotImplementedException();
            public void CleanCommands() => throw new NotImplementedException();
            public CancellationToken GetCancellationToken(int commandId) => throw new NotImplementedException();
            public void RegisterCancellationToken(int commandId, CancellationTokenSource cancellationTokenSource) => throw new NotImplementedException();
            public void UnregisterCancellationToken(int commandId) => throw new NotImplementedException();
        }
    }
}
