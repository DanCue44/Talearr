using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Chaptarr.Core.Test.Talearr.Recordings;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Download;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Search;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicDownloadTrackerFixture
    {
        private string _folder;
        private TalearrComicsDatabase _database;
        private TalearrComicsStore _store;
        private RecordingQueue _queue;
        private ComicDownloadTracker _tracker;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "talearr-comics-tracker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _database = new TalearrComicsDatabase(new TestAppFolderInfo(_folder), LogManager.GetLogger("TalearrTest"));
            _store = new TalearrComicsStore(_database);
            _queue = new RecordingQueue();
            _tracker = new ComicDownloadTracker(_database, _store, _queue, new ComicFailedDownloadService(_store, _queue, LogManager.GetLogger("TalearrTest")), LogManager.GetLogger("TalearrTest"));
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

        private static DownloadClientItem Item(string id, DownloadItemStatus status, string output = null)
        {
            return new DownloadClientItem { DownloadId = id, Title = id, Status = status, OutputPath = new OsPath(output ?? "/downloads/" + id) };
        }

        [Test]
        public void should_leave_downloads_it_did_not_grab_to_the_book_import()
        {
            Assert.That(_tracker.Claim(null, Item("book_download", DownloadItemStatus.Completed)), Is.False);
            Assert.That(File.Exists(_database.DatabasePath), Is.False, "checking a book download never creates the comics database");
        }

        [Test]
        public void should_claim_comic_downloads_and_queue_an_import_when_they_finish()
        {
            _store.SaveDownload(new ComicDownload { DownloadId = "comic_1", DownloadClientId = 1, Protocol = "usenet", SeriesId = 1, VolumeIds = "5", ReleaseTitle = "Saga v01", Status = ComicDownloadStatuses.Grabbed });
            _tracker.Track("comic_1");

            Assert.That(_tracker.Claim(null, Item("comic_1", DownloadItemStatus.Downloading)), Is.True);
            Assert.That(_queue.Pushed, Is.Empty);

            Assert.That(_tracker.Claim(null, Item("comic_1", DownloadItemStatus.Completed, "/downloads/Saga v01")), Is.True);
            Assert.That(_queue.Pushed.OfType<ImportComicDownloadCommand>().Single().DownloadId, Is.EqualTo("comic_1"));
            Assert.That(_store.GetDownload("comic_1").OutputPath, Is.EqualTo("/downloads/Saga v01"));

            // Seen again while it seeds: still claimed, not imported twice.
            Assert.That(_tracker.Claim(null, Item("comic_1", DownloadItemStatus.Completed)), Is.True);
            Assert.That(_queue.Pushed.Count, Is.EqualTo(1));
        }

        [Test]
        public void should_blocklist_a_failed_download()
        {
            _store.SaveDownload(new ComicDownload { DownloadId = "comic_2", DownloadClientId = 1, Protocol = "usenet", SeriesId = 1, VolumeIds = "5", ReleaseTitle = "Saga v02", Status = ComicDownloadStatuses.Grabbed });
            _tracker.Track("comic_2");

            var item = Item("comic_2", DownloadItemStatus.Failed);
            item.Message = "Unpacking failed";
            Assert.That(_tracker.Claim(null, item), Is.True);

            Assert.That(_store.GetDownload("comic_2").Status, Is.EqualTo(ComicDownloadStatuses.Failed));
            Assert.That(_store.GetBlocklist(1).Single().SourceTitle, Is.EqualTo("Saga v02"));
        }

        [Test]
        public void should_search_again_for_the_volume_of_a_failed_download()
        {
            var series = _store.AddSeries(new ComicSeries { Kind = ComicKinds.Comic, Title = "Saga", Path = Path.Combine(_folder, "Saga"), Monitored = true });
            var volume = _store.MergeVolumes(series.Id, new[] { new ComicVolume { Number = 3 } }, true).Single();
            _store.SaveDownload(new ComicDownload { DownloadId = "comic_3", DownloadClientId = 1, Protocol = "usenet", SeriesId = series.Id, VolumeIds = volume.Id.ToString(), ReleaseTitle = "Saga.Vol.03-BitBook", Status = ComicDownloadStatuses.Grabbed });
            _tracker.Track("comic_3");

            var item = Item("comic_3", DownloadItemStatus.Failed);
            item.Message = "Delete Status: COPY";
            _tracker.Claim(null, item);

            var search = _queue.Pushed.OfType<SearchMissingComicsCommand>().Single();
            Assert.That(search.SeriesId, Is.EqualTo(series.Id));
            Assert.That(search.VolumeIds, Is.EqualTo(new[] { volume.Id }));
            Assert.That(_store.GetHistory(series.Id, 10).Single().VolumeId, Is.EqualTo(volume.Id));
        }

        internal sealed class RecordingQueue : IManageCommandQueue
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
