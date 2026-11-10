using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    [TestFixture]
    public class StalledDownloadFixture
    {
        private const string HemingwayTitle = "Ernest.Hemingway.-.The.Old.Man.and.the.Sea.Hemingway.Library.Edition";

        private HistoryServiceProxy _history;
        private FailedDownloadServiceRecorder _failed;
        private FakeGrabPolicyService _policy;
        private DateTime _now;
        private TalearrStalledDownloadService _subject;

        [SetUp]
        public void SetUp()
        {
            var (historyService, history) = HistoryServiceProxy.Create();
            _history = history;
            _failed = new FailedDownloadServiceRecorder();
            _policy = new FakeGrabPolicyService();
            _now = new DateTime(2026, 10, 31, 16, 51, 0, DateTimeKind.Utc);
            _subject = new TalearrStalledDownloadService(_failed, historyService, _policy, LogManager.GetCurrentClassLogger(), () => _now);

            _history.Rows.Add(Grab("nzb-hemingway", "ReleasePush"));
        }

        [Test]
        public void should_fail_a_download_paused_with_nothing_changing_for_six_hours()
        {
            var download = Paused("nzb-hemingway", 31 * 1024 * 1024);

            Refresh(download);
            _now = _now.AddHours(5);
            Refresh(download);
            Assert.That(_failed.Failed, Is.Empty);

            _now = _now.AddHours(1);
            Refresh(download);

            Assert.That(_failed.Failed, Has.Count.EqualTo(1));
            Assert.That(_failed.Failed[0].Reason, Is.EqualTo("Stalled: paused in NZBGet for over 6 hours with nothing changing"));
            Assert.That(_failed.SkipRedownload[0], Is.False);
        }

        [Test]
        public void should_start_over_when_the_remaining_size_changes()
        {
            Refresh(Paused("nzb-hemingway", 100));
            _now = _now.AddHours(5);
            Refresh(Paused("nzb-hemingway", 60));
            _now = _now.AddHours(5);
            Refresh(Paused("nzb-hemingway", 60));

            Assert.That(_failed.Failed, Is.Empty);
        }

        [Test]
        public void should_start_over_after_the_download_resumes()
        {
            var download = Paused("nzb-hemingway", 100);
            Refresh(download);
            _now = _now.AddHours(4);

            download.DownloadItem.Status = DownloadItemStatus.Downloading;
            download.DownloadItem.ItemPaused = false;
            Refresh(download);

            _now = _now.AddHours(3);
            Refresh(Paused("nzb-hemingway", 100));

            Assert.That(_failed.Failed, Is.Empty);
        }

        [Test]
        public void should_leave_a_paused_client_alone()
        {
            var download = Paused("nzb-hemingway", 100);
            download.DownloadItem.ItemPaused = false;

            Refresh(download);
            _now = _now.AddHours(12);
            Refresh(download);

            Assert.That(_failed.Failed, Is.Empty);
        }

        [Test]
        public void should_respect_the_setting()
        {
            _policy.Policy = new TalearrGrabPolicy { FailStalledDownloads = false };
            var download = Paused("nzb-hemingway", 100);

            Refresh(download);
            _now = _now.AddHours(12);
            Refresh(download);

            Assert.That(_failed.Failed, Is.Empty);
        }

        [Test]
        public void should_only_warn_for_an_explicit_user_grab()
        {
            _history.Rows.Add(Grab("nzb-user", "InteractiveSearch"));
            var download = Paused("nzb-user", 100);

            Refresh(download);
            _now = _now.AddHours(7);
            Refresh(download);

            Assert.That(_failed.Failed, Is.Empty);
            Assert.That(download.Status, Is.EqualTo(TrackedDownloadStatus.Warning));
            Assert.That(download.StatusMessages[0].Messages[0], Does.StartWith("Paused in NZBGet for over 6 hours"));
        }

        private void Refresh(params TrackedDownload[] downloads)
        {
            _subject.Handle(new TrackedDownloadRefreshedEvent(new List<TrackedDownload>(downloads)));
        }

        private static TrackedDownload Paused(string downloadId, long remaining)
        {
            return new TrackedDownload
            {
                DownloadClient = 1,
                State = TrackedDownloadState.Downloading,
                DownloadItem = new DownloadClientItem
                {
                    DownloadId = downloadId,
                    Title = HemingwayTitle,
                    Status = DownloadItemStatus.Paused,
                    ItemPaused = true,
                    RemainingSize = remaining,
                    DownloadClientInfo = new DownloadClientItemClientInfo { Type = "NZBGet", Name = "NZBGet" }
                }
            };
        }

        private static EntityHistory Grab(string downloadId, string releaseSource)
        {
            return new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                BookId = 8304,
                DownloadId = downloadId,
                Date = DateTime.UtcNow.AddMinutes(-30),
                Data = new Dictionary<string, string> { { "releaseSource", releaseSource }, { "downloadForced", "False" } }
            };
        }
    }
}
