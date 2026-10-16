using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    [TestFixture]
    public class AutomaticRetryFixture
    {
        private const string FirmUsenetTitle = "John.Grisham.-.The.Firm.2010.Retail.EPUB.eBook-BitBook";
        private const string HealthFailure = "PAR Status: NONE - Unpack Status: NONE - Move Status: NONE - Script Status: NONE - Delete Status: HEALTH - Mark Status: NONE";

        private HistoryServiceProxy _history;
        private CommandQueueProxy _commands;
        private DownloadConfigProxy _config;
        private FakeGrabPolicyService _policy;
        private RedownloadFailedDownloadService _subject;

        [SetUp]
        public void SetUp()
        {
            var (historyService, history) = HistoryServiceProxy.Create();
            var (queue, commands) = CommandQueueProxy.Create();
            var (config, configProxy) = DownloadConfigProxy.Create();

            _history = history;
            _commands = commands;
            _config = configProxy;
            _policy = new FakeGrabPolicyService();

            var retryService = new TalearrRetryService(historyService, _policy, LogManager.GetCurrentClassLogger());
            _subject = new RedownloadFailedDownloadService(config, null, queue, LogManager.GetCurrentClassLogger(), retryService);
        }

        [Test]
        public void should_search_again_and_record_the_step_in_history()
        {
            AddFailure("nzb-firm-1", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-firm-1", FirmUsenetTitle, ReleaseSourceType.Search));

            var pushed = _commands.Pushed.Single();
            Assert.That(pushed.Command, Is.TypeOf<BookSearchCommand>());
            Assert.That(((BookSearchCommand)pushed.Command).BookIds, Is.EqualTo(new[] { 3129 }));
            Assert.That(pushed.Trigger, Is.EqualTo(CommandTrigger.Unspecified));

            var message = _history.Updated.Single().Data["Message"];
            Assert.That(message, Does.StartWith(HealthFailure));
            Assert.That(message, Does.Contain("TaleArr: release blocklisted, searching all indexers for a replacement (attempt 1 of 3)"));
        }

        [Test]
        public void should_retry_a_user_grab_as_a_user_search()
        {
            // The Firm was grabbed from interactive search; its replacement search must get the
            // same leniency (unmonitored single book, no delay profile wait).
            AddFailure("nzb-firm-1", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-firm-1", FirmUsenetTitle, ReleaseSourceType.InteractiveSearch));

            Assert.That(_commands.Pushed.Single().Trigger, Is.EqualTo(CommandTrigger.Manual));
        }

        [Test]
        public void should_stop_searching_once_the_retry_limit_is_reached()
        {
            AddFailure("nzb-1", "release one", HealthFailure, minutesAgo: 600);
            AddFailure("nzb-2", "release two", HealthFailure, minutesAgo: 400);
            AddFailure("nzb-3", "release three", HealthFailure, minutesAgo: 200);
            AddFailure("nzb-4", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-4", FirmUsenetTitle, ReleaseSourceType.Search));

            Assert.That(_commands.Pushed, Is.Empty);
            Assert.That(_history.Updated.Single().Data["Message"], Does.Contain("automatic retry limit reached (4 failed downloads in 7 days, limit 3)"));
        }

        [Test]
        public void should_honour_a_higher_retry_limit()
        {
            _policy.Policy = new TalearrGrabPolicy { MaxAutomaticRetries = 5 };
            AddFailure("nzb-1", "release one", HealthFailure, minutesAgo: 600);
            AddFailure("nzb-2", "release two", HealthFailure, minutesAgo: 400);
            AddFailure("nzb-3", "release three", HealthFailure, minutesAgo: 200);
            AddFailure("nzb-4", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-4", FirmUsenetTitle, ReleaseSourceType.Search));

            Assert.That(_commands.Pushed, Has.Count.EqualTo(1));
            Assert.That(_history.Updated.Single().Data["Message"], Does.Contain("attempt 4 of 5"));
        }

        [Test]
        public void should_not_count_failures_older_than_the_retry_window()
        {
            AddFailure("nzb-1", "release one", HealthFailure, minutesAgo: 60 * 24 * 10);
            AddFailure("nzb-2", "release two", HealthFailure, minutesAgo: 60 * 24 * 9);
            AddFailure("nzb-3", "release three", HealthFailure, minutesAgo: 60 * 24 * 8);
            AddFailure("nzb-4", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-4", FirmUsenetTitle, ReleaseSourceType.Search));

            Assert.That(_commands.Pushed, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_record_when_automatic_redownload_is_off()
        {
            _config.AutoRedownloadFailed = false;
            AddFailure("nzb-firm-1", FirmUsenetTitle, HealthFailure);

            _subject.Handle(FailedEvent("nzb-firm-1", FirmUsenetTitle, ReleaseSourceType.Search));

            Assert.That(_commands.Pushed, Is.Empty);
            Assert.That(_history.Updated.Single().Data["Message"], Does.Contain("automatic redownload is off"));
        }

        [Test]
        public void should_keep_upstream_behaviour_without_the_retry_service()
        {
            var (queue, commands) = CommandQueueProxy.Create();
            var (config, _) = DownloadConfigProxy.Create();
            var subject = new RedownloadFailedDownloadService(config, null, queue, LogManager.GetCurrentClassLogger());

            subject.Handle(FailedEvent("nzb-firm-1", FirmUsenetTitle, ReleaseSourceType.Search));

            Assert.That(commands.Pushed.Single().Command, Is.TypeOf<BookSearchCommand>());
        }

        [Test]
        public void classifier_should_fail_a_wrong_book()
        {
            var reason = ImportRejectionClassifier.Classify(Status(
                "Ali.Hazelwood.-.Bride.Series.-.02.-.Mate.[AUDIOBOOK].m4b",
                "Rejected: Completed download was grabbed for 'Bride' (BookId 3756, work hc:801806), but import matched 'Mate' (BookId 3754, work hc:1785259)."));

            Assert.That(reason, Is.EqualTo("release contains 'Mate', not the wanted book"));
        }

        [Test]
        public void classifier_should_fail_a_bundle_that_imported_as_a_different_book()
        {
            var reason = ImportRejectionClassifier.Classify(Status(
                "The Complete Novels - George Orwell.mobi",
                "Rejected: Completed download was grabbed for '1984' (BookId 4095, work hc:379760), but import matched 'The Complete Works of George Orwell: Novels, Poetry, Essays:' (BookId 4170, work hc:1388176)."));

            Assert.That(reason, Does.Contain("not the wanted book"));
        }

        [Test]
        public void classifier_should_leave_same_title_matches_for_the_user()
        {
            // Duplicate metadata rows ("Animal Farm" 5003 vs 5524) are a library problem, not a bad release.
            var reason = ImportRejectionClassifier.Classify(Status(
                "01 - Animal Farm.m4a",
                "Rejected: Completed download was grabbed for 'Animal Farm' (BookId 5003, work hc:356467), but import matched 'Animal Farm' (BookId 5524)."));

            Assert.That(reason, Is.Null);
        }

        [Test]
        public void classifier_should_leave_archives_to_the_importer()
        {
            var reason = ImportRejectionClassifier.Classify(Status(
                "John.Grisham.The.Pelican.Brief.2010.RETAiL.ePub.eBook-LiBRiCiDE-xpost",
                "No supported audio or ebook files were found in /downloads/completed/Books/John.Grisham.The.Pelican.Brief.2010.RETAiL.ePub.eBook-LiBRiCiDE-xpost. Chaptarr did see unsupported file(s): lc000sba.zip, libricide.nfo, file_id.diz. Unsupported extension(s): .diz, .nfo, .zip. Check that the download contains supported audio or ebook files and that Chaptarr can read the completed download folder."));

            Assert.That(reason, Is.Null);
        }

        [Test]
        public void classifier_should_fail_a_download_without_media_or_archives()
        {
            var reason = ImportRejectionClassifier.Classify(Status(
                "Some.Book.2024.EPUB",
                "NO_ELIGIBLE_FILES",
                "No supported audio or ebook files were found in /downloads/completed/Books/Some.Book.2024.EPUB. Chaptarr did see unsupported file(s): setup.exe, readme.nfo. Unsupported extension(s): .exe, .nfo. Check that the download contains supported audio or ebook files and that Chaptarr can read the completed download folder."));

            Assert.That(reason, Is.EqualTo("release contains no audio or ebook files"));
        }

        [Test]
        public void classifier_should_leave_unknown_and_mixed_rejections_alone()
        {
            Assert.That(ImportRejectionClassifier.Classify(Status("x", "IMPORT_EXCEPTION", "IOException: disk full")), Is.Null);
            Assert.That(ImportRejectionClassifier.Classify(new[]
            {
                new TrackedDownloadStatusMessage("a.m4b", "Rejected: Completed download was grabbed for 'Bride' (BookId 1), but import matched 'Mate' (BookId 2)."),
                new TrackedDownloadStatusMessage("b.m4b", "Not enough free space")
            }), Is.Null);
            Assert.That(ImportRejectionClassifier.Classify(null), Is.Null);
        }

        [Test]
        public void rejected_import_should_fail_the_download()
        {
            var (historyService, history) = HistoryServiceProxy.Create();
            history.Rows.Add(Grab("nzb-mate", "UserInvokedSearch"));
            var failed = new FailedDownloadServiceRecorder();
            var subject = new TalearrImportRejectionRetryService(failed, historyService, new FakeGrabPolicyService(), LogManager.GetCurrentClassLogger());

            var trackedDownload = BlockedDownload("nzb-mate",
                "Rejected: Completed download was grabbed for 'Bride' (BookId 3756, work hc:801806), but import matched 'Mate' (BookId 3754, work hc:1785259).");

            subject.Handle(new BookImportIncompleteEvent(trackedDownload));

            Assert.That(failed.Failed.Single().Reason, Is.EqualTo("Import rejected: release contains 'Mate', not the wanted book"));
        }

        [Test]
        public void rejected_import_should_not_fail_an_interactive_grab()
        {
            var (historyService, history) = HistoryServiceProxy.Create();
            history.Rows.Add(Grab("nzb-mate", "InteractiveSearch"));
            var failed = new FailedDownloadServiceRecorder();
            var subject = new TalearrImportRejectionRetryService(failed, historyService, new FakeGrabPolicyService(), LogManager.GetCurrentClassLogger());

            subject.Handle(new BookImportIncompleteEvent(BlockedDownload("nzb-mate",
                "Rejected: Completed download was grabbed for 'Bride' (BookId 3756), but import matched 'Mate' (BookId 3754).")));

            Assert.That(failed.Failed, Is.Empty);
        }

        [Test]
        public void rejected_import_should_respect_the_setting()
        {
            var (historyService, history) = HistoryServiceProxy.Create();
            history.Rows.Add(Grab("nzb-mate", "Search"));
            var failed = new FailedDownloadServiceRecorder();
            var policy = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { FailRejectedImports = false } };
            var subject = new TalearrImportRejectionRetryService(failed, historyService, policy, LogManager.GetCurrentClassLogger());

            subject.Handle(new BookImportIncompleteEvent(BlockedDownload("nzb-mate",
                "Rejected: Completed download was grabbed for 'Bride' (BookId 3756), but import matched 'Mate' (BookId 3754).")));

            Assert.That(failed.Failed, Is.Empty);
        }

        [Test]
        public void policy_store_should_default_and_round_trip()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var store = new TalearrGrabPolicyService(repository, LogManager.GetCurrentClassLogger());

            var defaults = store.Get();
            Assert.That(defaults.AbridgedPolicy, Is.EqualTo(AbridgedAudiobookPolicy.UnabridgedOnly));
            Assert.That(defaults.AllowBundles, Is.False);
            Assert.That(defaults.FailRejectedImports, Is.True);
            Assert.That(defaults.EffectiveMaxAutomaticRetries, Is.EqualTo(3));

            store.Save(new TalearrGrabPolicy
            {
                AbridgedPolicy = AbridgedAudiobookPolicy.PreferUnabridged,
                AllowBundles = true,
                FailRejectedImports = false,
                MaxAutomaticRetries = 50
            });

            Assert.That(proxy.Values.Keys, Is.All.StartsWith("talearr.grabpolicy."));
            Assert.That(proxy.Values[TalearrGrabPolicyService.AbridgedPolicyKey], Is.EqualTo("preferunabridged"));

            var reloaded = new TalearrGrabPolicyService(repository, LogManager.GetCurrentClassLogger()).Get();
            Assert.That(reloaded.AbridgedPolicy, Is.EqualTo(AbridgedAudiobookPolicy.PreferUnabridged));
            Assert.That(reloaded.AllowBundles, Is.True);
            Assert.That(reloaded.FailRejectedImports, Is.False);
            Assert.That(reloaded.EffectiveMaxAutomaticRetries, Is.EqualTo(TalearrGrabPolicy.MaxAutomaticRetriesLimit));
        }

        [Test]
        public void policy_store_should_ignore_garbage_values()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            proxy.Values[TalearrGrabPolicyService.AbridgedPolicyKey] = "sometimes";
            proxy.Values[TalearrGrabPolicyService.AllowBundlesKey] = "maybe";
            proxy.Values[TalearrGrabPolicyService.MaxAutomaticRetriesKey] = "-4";

            var policy = new TalearrGrabPolicyService(repository, LogManager.GetCurrentClassLogger()).Get();

            Assert.That(policy.AbridgedPolicy, Is.EqualTo(AbridgedAudiobookPolicy.UnabridgedOnly));
            Assert.That(policy.AllowBundles, Is.False);
            Assert.That(policy.EffectiveMaxAutomaticRetries, Is.EqualTo(3));
        }

        private void AddFailure(string downloadId, string title, string message, int minutesAgo = 0)
        {
            _history.Rows.Add(new EntityHistory
            {
                EventType = EntityHistoryEventType.DownloadFailed,
                BookId = 3129,
                AuthorId = 4,
                DownloadId = downloadId.ToUpperInvariant(),
                SourceTitle = title,
                Date = DateTime.UtcNow.AddMinutes(-minutesAgo),
                Data = new Dictionary<string, string> { { "Message", message } }
            });
        }

        private static DownloadFailedEvent FailedEvent(string downloadId, string title, ReleaseSourceType source)
        {
            return new DownloadFailedEvent
            {
                AuthorId = 4,
                BookIds = new List<int> { 3129 },
                DownloadId = downloadId,
                SourceTitle = title,
                Message = HealthFailure,
                ReleaseSource = source
            };
        }

        private static EntityHistory Grab(string downloadId, string releaseSource)
        {
            return new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                BookId = 3756,
                DownloadId = downloadId,
                Date = DateTime.UtcNow.AddMinutes(-30),
                Data = new Dictionary<string, string> { { "releaseSource", releaseSource }, { "downloadForced", "False" } }
            };
        }

        private static TrackedDownload BlockedDownload(string downloadId, string message)
        {
            var trackedDownload = new TrackedDownload
            {
                DownloadClient = 1,
                DownloadItem = new DownloadClientItem { DownloadId = downloadId, Title = "Ali.Hazelwood.-.Bride.Series.-.02.-.Mate.[AUDIOBOOK]-xpost" }
            };

            trackedDownload.Warn(new TrackedDownloadStatusMessage("Mate.m4b", message));
            trackedDownload.State = TrackedDownloadState.ImportBlocked;
            return trackedDownload;
        }

        private static TrackedDownloadStatusMessage[] Status(string title, params string[] messages)
        {
            return new[] { new TrackedDownloadStatusMessage(title, messages.ToList()) };
        }
    }
}
