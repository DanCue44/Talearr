using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.QualityLadder;

namespace Chaptarr.Core.Test.Talearr.QualityLadder
{
    [TestFixture]
    public class TalearrUpgradeCooldownFixture
    {
        private TalearrTestFolder _folder;
        private FailedDownloadServiceRecorder _failed;
        private HistoryServiceProxy _history;
        private TalearrUpgradeCooldownService _subject;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _failed = new FailedDownloadServiceRecorder();
            var (historyService, history) = HistoryServiceProxy.Create();
            _history = history;
            _now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            _subject = new TalearrUpgradeCooldownService(_folder.CreateDatabase(), _failed, historyService, LogManager.GetCurrentClassLogger())
            {
                UtcNow = () => _now
            };
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private static Book Firm(bool withFiles = true)
        {
            return new Book
            {
                Id = 3401,
                Title = "The Firm",
                MediaType = BookMediaType.Audiobook,
                LazyBookFiles = new LazyLoaded<List<BookFile>>(withFiles ? new List<BookFile> { new BookFile { Id = 1, Path = "/books/The Firm.m4b" } } : new List<BookFile>())
            };
        }

        private static DownloadClientItem Item(string id = "dl-1") => new DownloadClientItem { DownloadId = id, Title = "John Grisham - The Firm [64k]" };

        private static TrackedDownload Blocked(string id = "dl-1")
        {
            var trackedDownload = new TrackedDownload { DownloadClient = 1, DownloadItem = Item(id) };
            trackedDownload.Warn(new TrackedDownloadStatusMessage("The Firm.m4b", "Talearr: not an upgrade, same quality and no higher bitrate; kept the existing files"));
            trackedDownload.State = TrackedDownloadState.ImportBlocked;
            return trackedDownload;
        }

        private void GrabbedBy(string source, string id = "dl-1")
        {
            _history.Rows.Add(new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                BookId = 3401,
                DownloadId = id,
                Date = _now.AddHours(-1),
                Data = new Dictionary<string, string> { { "releaseSource", source }, { "downloadForced", "False" } }
            });
        }

        private static RemoteBook Release(Book book) => new RemoteBook { Books = new List<Book> { book }, Release = new ReleaseInfo { Title = "John Grisham - The Firm [128k]" } };

        [Test]
        public void a_rejected_upgrade_pauses_upgrades_for_seven_days()
        {
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade, same quality and no higher bitrate; kept the existing files", Item());

            var entry = _subject.GetActive(3401);
            Assert.Multiple(() =>
            {
                Assert.That(entry.Until, Is.EqualTo(_now.AddDays(7)));
                Assert.That(entry.Reason, Is.EqualTo("not an upgrade, same quality and no higher bitrate; kept the existing files"));
                Assert.That(entry.DownloadId, Is.EqualTo("dl-1"));
                Assert.That(_subject.GetActive(9999), Is.Null);
            });

            _now = _now.AddDays(7).AddMinutes(1);
            Assert.That(_subject.GetActive(3401), Is.Null, "the pause ends by itself");
        }

        [Test]
        public void the_cooldown_survives_a_restart()
        {
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item());

            var restarted = new TalearrUpgradeCooldownService(_folder.CreateDatabase(), _failed, null, LogManager.GetCurrentClassLogger()) { UtcNow = () => _now };

            Assert.That(restarted.GetActive(3401), Is.Not.Null);
        }

        [Test]
        public void automatic_upgrade_grabs_are_soft_rejected_while_paused()
        {
            var spec = new TalearrUpgradeCooldownSpecification(_subject);
            Assert.That(spec.IsSatisfiedBy(Release(Firm()), null).Accepted, Is.True, "no pause yet");

            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item());
            var decision = spec.IsSatisfiedBy(Release(Firm()), null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Accepted, Is.False);
                Assert.That(decision.Reason, Does.Contain("paused until 2026-10-24"));
                Assert.That(decision.Reason, Does.Not.Contain("\u2014"));
            });
        }

        [Test]
        public void a_book_without_files_is_not_an_upgrade_and_is_not_paused()
        {
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item());

            Assert.That(new TalearrUpgradeCooldownSpecification(_subject).IsSatisfiedBy(Release(Firm(withFiles: false)), null).Accepted, Is.True);
        }

        [Test]
        public void the_rejected_download_is_blocklisted_without_a_new_search()
        {
            GrabbedBy("RssSync");
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade, same quality and no higher bitrate; kept the existing files", Item());

            _subject.Handle(new BookImportIncompleteEvent(Blocked()));

            Assert.Multiple(() =>
            {
                Assert.That(_failed.Failed.Single().Reason, Is.EqualTo("Import rejected: not an upgrade, same quality and no higher bitrate; kept the existing files"));
                Assert.That(_failed.SkipRedownload.Single(), Is.True, "no search: upgrades are paused anyway");
            });
        }

        [Test]
        public void other_blocked_downloads_and_user_grabs_are_left_alone()
        {
            GrabbedBy("InteractiveSearch", "dl-user");
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item("dl-user"));

            _subject.Handle(new BookImportIncompleteEvent(Blocked("dl-user")));
            _subject.Handle(new BookImportIncompleteEvent(Blocked("dl-unrelated")));

            Assert.That(_failed.Failed, Is.Empty);
        }

        [Test]
        public void loop_grab_rejected_then_the_next_search_does_not_grab_again()
        {
            GrabbedBy("RssSync");
            var spec = new TalearrUpgradeCooldownSpecification(_subject);

            // 1: an estimate-only "upgrade" is grabbed and the guard rejects it at import.
            Assert.That(spec.IsSatisfiedBy(Release(Firm()), null).Accepted, Is.True);
            _subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item());
            _subject.Handle(new BookImportIncompleteEvent(Blocked()));

            // 2: the next automatic search finds a similar release and does not grab it.
            Assert.That(spec.IsSatisfiedBy(Release(Firm()), null).Accepted, Is.False);
            Assert.That(_failed.Failed, Has.Count.EqualTo(1));
        }

        [Test]
        public void a_broken_sidecar_means_no_pause()
        {
            var subject = new TalearrUpgradeCooldownService(new BrokenDatabase(), _failed, null, LogManager.GetCurrentClassLogger());

            subject.RecordRejectedUpgrade(Firm(), "Talearr: not an upgrade", Item());

            Assert.That(subject.GetActive(3401), Is.Null);
            Assert.That(new TalearrUpgradeCooldownSpecification(subject).IsSatisfiedBy(Release(Firm()), null).Accepted, Is.True);
        }

        private sealed class BrokenDatabase : NzbDrone.Core.Talearr.Recordings.ITalearrDatabase
        {
            public string DatabasePath => "broken";
            public int SchemaVersion => 0;
            public System.Data.IDbConnection OpenConnection() => throw new System.IO.IOException("disk gone");
            public void Reset(string reason)
            {
            }

            public bool IsCorruption(Exception ex) => false;
        }
    }
}
