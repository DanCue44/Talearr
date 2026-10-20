using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Talearr.Seeding;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Seeding;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    // Talearr: the seed-only / ratio guard (MyAnonaMouseAccountStanding), the account refresh
    // that feeds it, and the Power User progress card.
    [TestFixture]
    public class MamAccountStandingFixture
    {
        private const long GiB = 1024L * 1024 * 1024;
        private static readonly DateTime Now = new DateTime(2026, 10, 17, 21, 0, 0, DateTimeKind.Utc);

        private static MyAnonaMouseSettings Account(string userClass = "User", long? uploaded = 50 * GiB, long? downloaded = 10 * GiB, double hoursOld = 0.5)
        {
            return new MyAnonaMouseSettings
            {
                MamId = "OK",
                UserClass = userClass,
                UploadedBytes = uploaded,
                DownloadedBytes = downloaded,
                UnsatisfiedCount = 10,
                UnsatisfiedLimit = 20,
                UnsatisfiedSnapshotUtc = Now.AddHours(-hoursOld),
                UnsatisfiedStatusRefreshedUtc = Now.AddHours(-hoursOld)
            };
        }

        [Test]
        public void seed_only_mouse_account_blocks_every_grab_including_freeleech()
        {
            var settings = Account("Mouse");

            Assert.Multiple(() =>
            {
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, GiB / 100, Now), Is.EqualTo(MyAnonaMouseAccountStanding.SeedOnlyMessage));
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, GiB / 100, Now), Is.EqualTo(MyAnonaMouseAccountStanding.SeedOnlyMessage));
            });
        }

        [Test]
        public void seed_only_flag_blocks_even_with_a_normal_class_and_a_stale_status()
        {
            var settings = Account("User", hoursOld: 30);
            settings.SeedOnly = true;

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, 1, Now), Is.EqualTo(MyAnonaMouseAccountStanding.SeedOnlyMessage));
        }

        [Test]
        public void non_freeleech_grab_needs_the_ratio_after_it_to_stay_at_two()
        {
            // 40 GiB up, 10 GiB down: a 10 GiB grab leaves 40 / 20 = 2.0 (ok), 11 GiB leaves 1.9.
            var settings = Account(uploaded: 40 * GiB, downloaded: 10 * GiB);

            Assert.Multiple(() =>
            {
                Assert.That(MyAnonaMouseAccountStanding.DefaultMinimumRatioAfterGrab, Is.EqualTo(2.0));
                Assert.That(new MyAnonaMouseSettings().MinimumRatioAfterGrab, Is.EqualTo(2.0));
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 10 * GiB, Now), Is.Null);
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 11 * GiB, Now), Does.StartWith("MAM ratio guard").And.Contain("1.90"));
            });
        }

        [Test]
        public void the_incident_new_account_with_no_upload_cannot_grab_a_normal_ebook()
        {
            var settings = Account(uploaded: 0, downloaded: 0);

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 2 * 1024 * 1024, Now), Does.StartWith("MAM ratio guard"));
        }

        [Test]
        public void freeleech_is_exempt_from_the_ratio_rule()
        {
            var settings = Account(uploaded: 0, downloaded: 5 * GiB);

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, 5 * GiB, Now), Is.Null);
        }

        [Test]
        public void custom_minimum_ratio_is_used()
        {
            var settings = Account(uploaded: 12 * GiB, downloaded: 10 * GiB);
            settings.MinimumRatioAfterGrab = 1.0;

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, GiB, Now), Is.Null);
        }

        [TestCase(3.5)]
        [TestCase(48)]
        public void status_older_than_three_hours_allows_only_freeleech(double hoursOld)
        {
            var settings = Account(hoursOld: hoursOld);

            Assert.Multiple(() =>
            {
                Assert.That(MyAnonaMouseAccountStanding.HasFreshStatus(settings, Now), Is.False);
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, GiB, Now), Is.Null);
                Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 1, Now), Does.Contain("unknown or older than 3 hours"));
            });
        }

        [Test]
        public void never_refreshed_status_allows_only_freeleech()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, GiB, Now), Is.Null);
            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 1, Now), Is.Not.Null);
        }

        [Test]
        public void unknown_totals_allow_only_freeleech()
        {
            var settings = Account(uploaded: null, downloaded: null);

            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, false, 1, Now), Does.Contain("only freeleech"));
            Assert.That(MyAnonaMouseAccountStanding.Evaluate(settings, true, 1, Now), Is.Null);
        }

        private static RemoteBook Remote(IndexerFlags flags = 0, string url = "https://www.myanonamouse.net/tor/download.php?tid=1", long size = GiB)
        {
            return new RemoteBook { Release = new TorrentInfo { Title = "Book", IndexerId = 9, Indexer = "MyAnonaMouse", IndexerFlags = flags, DownloadUrl = url, Size = size } };
        }

        private static MyAnonaMouseStandingGuard Guard(MyAnonaMouseSettings settings)
        {
            FakeMamIndexerFactory.Create(out var factory).Definitions.Add(new IndexerDefinition { Id = 9, Name = "MyAnonaMouse", EnableAutomaticSearch = true, Settings = settings });
            return new MyAnonaMouseStandingGuard(new MyAnonaMouseReleasePolicy(factory)) { UtcNow = () => Now };
        }

        [Test]
        public void guard_and_specification_reject_normal_mam_releases_on_a_low_ratio()
        {
            var guard = Guard(Account(uploaded: GiB, downloaded: GiB));
            var spec = new MyAnonaMouseAccountStandingSpecification(guard, LogManager.GetCurrentClassLogger());

            Assert.Multiple(() =>
            {
                Assert.That(spec.IsSatisfiedBy(Remote(), null).Accepted, Is.False);
                Assert.That(spec.IsSatisfiedBy(Remote(IndexerFlags.Freeleech), null).Accepted, Is.True);
                Assert.That(spec.IsSatisfiedBy(new RemoteBook { Release = new TorrentInfo { Title = "x", IndexerId = 4, Indexer = "Other" } }, null).Accepted, Is.True, "not MAM");
            });
        }

        [Test]
        public void wedge_requested_at_grab_time_counts_as_freeleech_only_while_wedges_are_left()
        {
            var settings = Account(uploaded: GiB, downloaded: GiB);
            settings.UseFreeleechWedge = (int)MyAnonaMouseFreeleechWedgeAction.Preferred;
            settings.UseFreeleechOnlyForAudiobooks = true;
            settings.FreeleechWedges = 5;
            var guard = Guard(settings);
            const string audiobook = "https://www.myanonamouse.net/tor/download.php?tid=1&canUseToken=true&isAudiobook=true";
            const string ebook = "https://www.myanonamouse.net/tor/download.php?tid=1&canUseToken=true";

            Assert.That(guard.GetRejection(Remote(url: audiobook)), Is.Null);
            Assert.That(guard.GetRejection(Remote(url: ebook)), Is.Not.Null, "wedges only for audiobooks");

            settings.FreeleechWedges = 0;
            Assert.That(guard.GetRejection(Remote(url: audiobook)), Is.Not.Null);
        }

        [Test]
        public void seed_only_guard_rejects_freeleech_releases_too()
        {
            var guard = Guard(Account("Mouse"));

            Assert.That(guard.GetRejection(Remote(IndexerFlags.Freeleech)), Is.EqualTo(MyAnonaMouseAccountStanding.SeedOnlyMessage));
        }

        [Test]
        public void prowlarr_only_mam_without_a_native_account_keeps_the_old_behavior()
        {
            FakeMamIndexerFactory.Create(out var factory);
            var guard = new MyAnonaMouseStandingGuard(new MyAnonaMouseReleasePolicy(factory));

            Assert.That(guard.GetRejection(new RemoteBook { Release = new TorrentInfo { Title = "x", Indexer = "MyAnonamouse (Prowlarr)" } }), Is.Null);
        }

        [Test]
        public void seed_only_health_check_is_an_error_with_the_store_hint()
        {
            FakeMamIndexerFactory.Create(out var factory).Definitions.Add(new IndexerDefinition { Id = 9, Name = "MyAnonaMouse", EnableAutomaticSearch = true, Settings = Account("Mouse") });
            SeedingFake<ILocalizationService>.Create(out var localization);

            var check = new TalearrMyAnonaMouseSeedOnlyCheck(factory, localization).Check();

            Assert.That(check.Type, Is.EqualTo(HealthCheckResult.Error));
            Assert.That(check.Message, Does.StartWith("MAM account is seed-only: buy upload credit in the Bonus Points Store"));
        }

        // Account refresh.
        private static long Created => DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        [Test]
        public void refresh_reads_text_totals_download_disabled_and_join_date()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };
            FakeMamHttpClient.Create(out var http).Reply(HttpStatusCode.OK, "application/json",
                $"{{\"classname\":\"User\",\"created\":1792195200,\"uploaded\":\"50.5 GiB\",\"downloaded\":\"10 MiB\",\"download_disabled\":\"1\",\"snatch_summary\":{{\"created\":{Created},\"unsat\":{{\"count\":10,\"limit\":20}}}}}}");
            FakeMamHttpClientFactory.Create(http, out var factory);

            MamTestIndexers.Create(settings, factory, new RecordingSessionMonitor()).RefreshAccountStatus().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(settings.UploadedBytes, Is.EqualTo((long)(50.5 * GiB)));
                Assert.That(settings.DownloadedBytes, Is.EqualTo(10L * 1024 * 1024));
                Assert.That(settings.Ratio, Is.EqualTo(5171.2).Within(0.1));
                Assert.That(settings.SeedOnly, Is.True);
                Assert.That(settings.MemberSinceUtc, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1792195200).UtcDateTime));
            });
        }

        [Test]
        public void refresh_without_the_optional_fields_leaves_them_unknown()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };
            FakeMamHttpClient.Create(out var http).Reply(HttpStatusCode.OK, "application/json",
                $"{{\"classname\":\"User\",\"created\":{Created},\"unsat\":{{\"count\":1,\"limit\":20}}}}");
            FakeMamHttpClientFactory.Create(http, out var factory);

            MamTestIndexers.Create(settings, factory, new RecordingSessionMonitor()).RefreshAccountStatus().GetAwaiter().GetResult();

            Assert.Multiple(() =>
            {
                Assert.That(settings.UploadedBytes, Is.Null);
                Assert.That(settings.SeedOnly, Is.Null);
                Assert.That(settings.MemberSinceUtc, Is.Null, "a top-level created without a snatch summary is the snapshot time");
            });
        }

        // Regression (live 0.17.2): running the refresh task by hand right after another refresh
        // finished in 3 ms without calling MAM, so a class change (Mouse back to User after buying
        // upload credit) stayed stale.
        [Test]
        public void manual_refresh_task_always_calls_mam_and_stores_the_new_class()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK", UserClass = "Mouse", BonusPoints = 7400 };
            var http = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out var httpFactory);
            var indexer = MamTestIndexers.Create(settings, httpFactory, new RecordingSessionMonitor());

            var indexers = FakeMamIndexerFactory.Create(out var indexerFactory);
            indexers.Providers.Add(indexer);
            indexers.Definitions.Add((IndexerDefinition)indexer.Definition);

            SeedingFake<IMamUnsatisfiedSlotGuard>.Create(out var slotGuard);
            var service = new MyAnonaMouseAccountStatusService(indexerFactory, slotGuard, LogManager.GetCurrentClassLogger());

            http.Reply(HttpStatusCode.OK, "application/json", $"{{\"classname\":\"Mouse\",\"seedbonus\":7400,\"created\":{Created},\"unsat\":{{\"count\":10,\"limit\":20}}}}");
            http.Reply(HttpStatusCode.OK, "application/json", $"{{\"classname\":\"User\",\"seedbonus\":4900,\"created\":{Created},\"unsat\":{{\"count\":10,\"limit\":20}}}}");

            service.Execute(new RefreshMyAnonaMouseAccountStatusCommand { Trigger = CommandTrigger.Scheduled });
            service.Execute(new RefreshMyAnonaMouseAccountStatusCommand { Trigger = CommandTrigger.Manual });

            Assert.Multiple(() =>
            {
                Assert.That(http.Requests, Has.Count.EqualTo(2));
                Assert.That(settings.UserClass, Is.EqualTo("User"));
                Assert.That(settings.BonusPoints, Is.EqualTo(4900));
                Assert.That(indexers.Updated, Has.Count.EqualTo(2), "every refresh is saved");
            });
        }

        [Test]
        public void health_cycle_refresh_still_waits_an_hour()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };
            var http = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out var httpFactory);
            var indexer = MamTestIndexers.Create(settings, httpFactory, new RecordingSessionMonitor());
            var indexers = FakeMamIndexerFactory.Create(out var indexerFactory);
            indexers.Providers.Add(indexer);
            SeedingFake<IMamUnsatisfiedSlotGuard>.Create(out var slotGuard);
            var service = new MyAnonaMouseAccountStatusService(indexerFactory, slotGuard, LogManager.GetCurrentClassLogger());

            http.Reply(HttpStatusCode.OK, "application/json", $"{{\"classname\":\"User\",\"created\":{Created},\"unsat\":{{\"count\":1,\"limit\":20}}}}");

            service.Execute(new RefreshMyAnonaMouseAccountStatusCommand());
            service.Handle(new HealthCheckCompleteEvent());

            Assert.That(http.Requests, Has.Count.EqualTo(1));
        }

        // Power User progress (Activity > Seeding).
        [Test]
        public void power_user_progress_for_the_owner_account()
        {
            var settings = Account(uploaded: 5 * GiB, downloaded: 2 * GiB);
            settings.Ratio = 2.5;
            settings.MemberSinceUtc = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            var status = new TalearrSeedingMamStatus();

            TalearrMamPowerUserProgress.Apply(status, settings, Now);

            Assert.Multiple(() =>
            {
                Assert.That(status.DaysUntilEligible, Is.EqualTo(28));
                Assert.That(status.UploadedRemainingBytes, Is.EqualTo(20 * GiB));
                Assert.That(status.RatioOk, Is.True);
                Assert.That(status.UploadedBytes, Is.EqualTo(5 * GiB));
                Assert.That(status.MemberSince, Is.EqualTo(settings.MemberSinceUtc));
            });
        }

        [Test]
        public void power_user_progress_unknown_values_stay_null()
        {
            var status = new TalearrSeedingMamStatus();

            TalearrMamPowerUserProgress.Apply(status, new MyAnonaMouseSettings(), Now);

            Assert.Multiple(() =>
            {
                Assert.That(status.DaysUntilEligible, Is.Null);
                Assert.That(status.UploadedRemainingBytes, Is.Null);
                Assert.That(status.RatioOk, Is.Null);
            });
        }

        [Test]
        public void power_user_progress_is_done_after_four_weeks_and_ratio_below_two_is_not_ok()
        {
            var settings = Account(uploaded: 30 * GiB, downloaded: 20 * GiB);
            settings.Ratio = 1.5;
            settings.MemberSinceUtc = Now.AddDays(-40);
            var status = new TalearrSeedingMamStatus();

            TalearrMamPowerUserProgress.Apply(status, settings, Now);

            Assert.That(status.DaysUntilEligible, Is.Zero);
            Assert.That(status.UploadedRemainingBytes, Is.Zero);
            Assert.That(status.RatioOk, Is.False);
        }
    }
}
