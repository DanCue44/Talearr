using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

using HealthCheckModel = NzbDrone.Core.HealthCheck.HealthCheck;

namespace Chaptarr.Core.Test.Talearr.Notifications
{
    [TestFixture]
    public class TalearrNotificationEventsFixture
    {
        private RecordingProxy _factory;
        private RecordingProxy _status;
        private List<RecordingNotification> _notifications;
        private NotificationService _subject;

        [SetUp]
        public void SetUp()
        {
            _notifications = new List<RecordingNotification>();

            var factory = RecordingProxy.Create<INotificationFactory>(out _factory);
            var status = RecordingProxy.Create<INotificationStatusService>(out _status);
            var editions = RecordingProxy.Create<IEditionService>(out _);

            foreach (var name in new[]
                     {
                         "OnGrabEnabled", "OnHealthIssueEnabled", "OnHealthRestoredEnabled", "OnImportFailureEnabled",
                         "OnDownloadFailureEnabled"
                     })
            {
                _factory.Handlers[name] = _ => _notifications.Cast<INotification>().ToList();
            }

            _subject = new NotificationService(factory, status, editions, LogManager.CreateNullLogger());
        }

        private RecordingNotification Add(int id, bool includeWarnings = false, params int[] tags)
        {
            var notification = new RecordingNotification(id, includeWarnings, tags);
            _notifications.Add(notification);
            return notification;
        }

        [Test]
        public void health_restored_is_sent_to_connections_with_the_flag()
        {
            var notification = Add(1);
            var check = new HealthCheckModel(typeof(IndexerStatusCheck), HealthCheckResult.Error, "Indexers unavailable");

            _subject.Handle(new HealthCheckRestoredEvent(check, false));

            Assert.That(notification.HealthRestored, Is.EqualTo(new[] { check }));
            Assert.That(_status.Count("RecordSuccess"), Is.EqualTo(1));
        }

        [Test]
        public void health_restored_is_not_sent_in_the_startup_grace_period()
        {
            var notification = Add(1);

            _subject.Handle(new HealthCheckRestoredEvent(new HealthCheckModel(typeof(IndexerStatusCheck), HealthCheckResult.Error, "x"), true));

            Assert.That(notification.HealthRestored, Is.Empty);
        }

        [Test]
        public void a_restored_warning_follows_include_health_warnings()
        {
            var quiet = Add(1, includeWarnings: false);
            var loud = Add(2, includeWarnings: true);

            _subject.Handle(new HealthCheckRestoredEvent(new HealthCheckModel(typeof(IndexerStatusCheck), HealthCheckResult.Warning, "x"), false));

            Assert.That(quiet.HealthRestored, Is.Empty);
            Assert.That(loud.HealthRestored, Has.Count.EqualTo(1));
        }

        [Test]
        public void talearr_notices_go_out_with_warnings_but_upstream_notices_do_not()
        {
            var notification = Add(1, includeWarnings: true);

            _subject.Handle(new HealthCheckFailedEvent(new HealthCheckModel(typeof(TalearrMetadataFallbackCheck), HealthCheckResult.Notice, "fallback active"), false));
            _subject.Handle(new HealthCheckFailedEvent(new HealthCheckModel(typeof(PackageGlobalMessageCheck), HealthCheckResult.Notice, "package"), false));

            Assert.That(notification.HealthIssues.Select(h => h.Message), Is.EqualTo(new[] { "fallback active" }));
        }

        [Test]
        public void talearr_notices_stay_quiet_without_include_health_warnings()
        {
            var notification = Add(1, includeWarnings: false);

            _subject.Handle(new HealthCheckFailedEvent(new HealthCheckModel(typeof(TalearrMetadataFallbackCheck), HealthCheckResult.Notice, "fallback active"), false));

            Assert.That(notification.HealthIssues, Is.Empty);
        }

        [Test]
        public void a_blocked_import_is_sent_as_manual_interaction_with_book_and_reasons()
        {
            var notification = Add(1);
            var author = NotificationTestData.Author();
            var book = NotificationTestData.Audiobook(author);
            var tracked = new TrackedDownload
            {
                State = TrackedDownloadState.ImportBlocked,
                DownloadItem = new DownloadClientItem { Title = "Frank Herbert - Dune [M4B]", DownloadId = "abc" },
                RemoteBook = new RemoteBook { Author = author, Books = new List<Book> { book } }
            };
            tracked.Warn(new TrackedDownloadStatusMessage("Dune.m4b", "Not an upgrade for existing book file(s)"));

            _subject.Handle(new BookImportIncompleteEvent(tracked));

            var message = notification.ImportFailures.Single();
            Assert.That(message.ManualInteractionRequired, Is.True);
            Assert.That(message.Book, Is.SameAs(book));
            Assert.That(message.Author, Is.SameAs(author));
            Assert.That(message.DownloadId, Is.EqualTo("abc"));
            Assert.That(message.StatusMessages, Is.EqualTo(new[] { "Dune.m4b: Not an upgrade for existing book file(s)" }));
            Assert.That(message.Message, Does.Contain("Frank Herbert - Dune [M4B]"));
        }

        [Test]
        public void an_import_failure_without_a_known_book_does_not_throw_and_skips_tagged_connections()
        {
            var untagged = Add(1);
            var tagged = Add(2, false, 7);
            var tracked = new TrackedDownload
            {
                State = TrackedDownloadState.ImportPending,
                DownloadItem = new DownloadClientItem { Title = "Unknown release" }
            };

            _subject.Handle(new BookImportIncompleteEvent(tracked));

            Assert.That(untagged.ImportFailures.Single().ManualInteractionRequired, Is.False);
            Assert.That(tagged.ImportFailures, Is.Empty);
            Assert.That(_status.Count("RecordFailure"), Is.EqualTo(0));
        }

        [Test]
        public void a_manual_mark_as_failed_without_a_tracked_download_still_notifies()
        {
            var notification = Add(1);

            _subject.Handle(new DownloadFailedEvent
            {
                SourceTitle = "Dune",
                Message = "Manually marked as failed",
                Quality = new QualityModel(Quality.M4B)
            });

            Assert.That(notification.DownloadFailures.Single().Message, Is.EqualTo("Manually marked as failed"));
            Assert.That(_status.Count("RecordFailure"), Is.EqualTo(0));
        }

        [TestCase(false, false, "")]
        [TestCase(true, false, " [bundle]")]
        [TestCase(true, true, " [bundle, last resort]")]
        public void a_grab_says_when_it_was_a_bundle(bool isBundle, bool isFallback, string suffix)
        {
            var notification = Add(1);
            var author = NotificationTestData.Author();
            var remoteBook = new RemoteBook
            {
                Author = author,
                Books = new List<Book> { new Book { Id = 0, Title = "Dune" } },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                Release = new ReleaseInfo { Title = "Dune Chronicles Box Set" },
                IsBundle = isBundle,
                IsBundleFallback = isFallback
            };

            _subject.Handle(new BookGrabbedEvent(remoteBook));

            Assert.That(notification.Grabs.Single().Message, Is.EqualTo("Frank Herbert - Dune - [M4B]" + suffix));
        }

        [Test]
        public void health_check_service_publishes_restored_when_a_failing_check_passes()
        {
            var events = RecordingProxy.Create<IEventAggregator>(out var eventProxy);
            var serverChecks = RecordingProxy.Create<IServerSideNotificationService>(out _);
            var gate = RecordingProxy.Create<IMetadataServerHealthGate>(out _);
            var runtime = RecordingProxy.Create<IRuntimeInfo>(out var runtimeProxy);
            runtimeProxy.Handlers["get_StartTime"] = _ => DateTime.UtcNow.AddHours(-1);

            var check = new SwitchableCheck();
            var service = new HealthCheckService(new IProvideHealthCheck[] { check }, serverChecks, gate, events, new CacheManager(), runtime, LogManager.CreateNullLogger());

            check.Result = new HealthCheckModel(typeof(SwitchableCheck), HealthCheckResult.Error, "broken");
            service.Execute(new CheckHealthCommand());
            check.Result = new HealthCheckModel(typeof(SwitchableCheck));
            service.Execute(new CheckHealthCommand());
            service.Execute(new CheckHealthCommand());

            var published = eventProxy.Args("PublishEvent");
            Assert.That(published.OfType<HealthCheckFailedEvent>().Count(), Is.EqualTo(1));
            var restored = published.OfType<HealthCheckRestoredEvent>().ToList();
            Assert.That(restored, Has.Count.EqualTo(1));
            Assert.That(restored[0].PreviousCheck.Message, Is.EqualTo("broken"));
        }

        private class SwitchableCheck : IProvideHealthCheck
        {
            public HealthCheckModel Result { get; set; }
            public bool CheckOnStartup => true;
            public bool CheckOnSchedule => true;

            public HealthCheckModel Check()
            {
                return Result;
            }
        }
    }
}
