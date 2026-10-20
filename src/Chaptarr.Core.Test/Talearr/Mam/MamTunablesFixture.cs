using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Chaptarr.Core.Test.Talearr.Seeding;
using Newtonsoft.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.MamFiller;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    // Talearr: MAM timing and aggressiveness settings. Old settings JSON keeps the old behavior,
    // validation only stops values that would break, and the account status refresh, the
    // freshness windows and the task ticks follow the settings.
    [TestFixture]
    public class MamTunablesFixture
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private static long Created => DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        private static void AssertLegacyDefaults(MyAnonaMouseSettings settings)
        {
            Assert.Multiple(() =>
            {
                Assert.That(settings.AccountStatusRefreshInterval, Is.EqualTo(60));
                Assert.That(settings.ResolveAccountStatusRefreshInterval(), Is.EqualTo(TimeSpan.FromHours(1)));
                Assert.That(settings.ResolveStatusMaxAge(TimeSpan.FromHours(2)), Is.EqualTo(TimeSpan.FromHours(2)));
                Assert.That(settings.DynamicSeedboxInterval, Is.EqualTo(30));
                Assert.That(settings.FillerIntervalHours, Is.EqualTo(6));
                Assert.That(settings.FillerMaxGrabsPerRun, Is.EqualTo(5));
                Assert.That(settings.FillerSlotMargin, Is.EqualTo(2));
                Assert.That(settings.FillerMaxSizeMb, Is.EqualTo(2048));
                Assert.That(settings.FillerMinLeechers, Is.EqualTo(1));
                Assert.That(settings.FillerMaxSeeders, Is.EqualTo(30));
                Assert.That(settings.FillerSearchPages, Is.EqualTo(3));
                Assert.That(settings.FillerPreferredSearchPages, Is.EqualTo(2));
                Assert.That(settings.FillerErrorPauseHours, Is.EqualTo(12));
                Assert.That(TalearrMamFillerRules.ErrorPause(settings), Is.EqualTo(TimeSpan.FromHours(12)));
                Assert.That(settings.BonusDailyPurchaseCap, Is.EqualTo(2));
                Assert.That(settings.BonusRetryAfterFailureHours, Is.EqualTo(24));
                Assert.That(MyAnonaMouseBonusSpendRules.RetryAfterFailure(settings), Is.EqualTo(TimeSpan.FromHours(24)));
                Assert.That(settings.UnsatisfiedSlotReserve, Is.EqualTo(5));
                Assert.That(settings.ManualGrabBuffer, Is.Zero);
                Assert.That(settings.Validate().IsValid, Is.True);
            });
        }

        [Test]
        public void settings_json_without_the_new_fields_keeps_the_old_values_newtonsoft()
        {
            AssertLegacyDefaults(JsonConvert.DeserializeObject<MyAnonaMouseSettings>("{\"mamId\":\"secret\",\"fillerEnabled\":true}"));
        }

        [Test]
        public void settings_json_without_the_new_fields_keeps_the_old_values_stjson()
        {
            AssertLegacyDefaults(STJson.Deserialize<MyAnonaMouseSettings>("{\"mamId\":\"secret\",\"fillerEnabled\":true}"));
        }

        [Test]
        public void new_fields_round_trip()
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK", AccountStatusRefreshInterval = 15, FillerSearchPages = 7, FillerErrorPauseHours = 2, BonusRetryAfterFailureHours = 3 };

            var copy = STJson.Deserialize<MyAnonaMouseSettings>(STJson.ToJson(settings));

            Assert.Multiple(() =>
            {
                Assert.That(copy.AccountStatusRefreshInterval, Is.EqualTo(15));
                Assert.That(copy.FillerSearchPages, Is.EqualTo(7));
                Assert.That(copy.FillerErrorPauseHours, Is.EqualTo(2));
                Assert.That(copy.BonusRetryAfterFailureHours, Is.EqualTo(3));
            });
        }

        private static bool Valid(Action<MyAnonaMouseSettings> change)
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK", FillerEnabled = true, DynamicSeedboxEnabled = true };
            change(settings);
            return settings.Validate().IsValid;
        }

        // Floors stop what breaks (zero or negative intervals, no grabs), ceilings stop typos.
        // Aggressive but working values are the user's choice.
        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(1440, true)]
        [TestCase(1441, false)]
        public void account_status_refresh_bounds(int minutes, bool valid)
        {
            Assert.That(Valid(s => s.AccountStatusRefreshInterval = minutes), Is.EqualTo(valid));
        }

        [TestCase(-5, false)]
        [TestCase(0, false)]
        [TestCase(1, true)]
        [TestCase(4, true)]
        [TestCase(1440, true)]
        [TestCase(1441, false)]
        public void dynamic_seedbox_interval_bounds(int minutes, bool valid)
        {
            Assert.That(Valid(s => s.DynamicSeedboxInterval = minutes), Is.EqualTo(valid));
        }

        [Test]
        public void validation_allows_aggressive_values_and_stops_broken_ones()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Valid(s => s.FillerMaxGrabsPerRun = 0), Is.False);
                Assert.That(Valid(s => s.FillerMaxGrabsPerRun = 1), Is.True);
                Assert.That(Valid(s => s.FillerMaxGrabsPerRun = 50), Is.True, "was capped at 20");
                Assert.That(Valid(s => s.FillerMaxGrabsPerRun = 1001), Is.False);
                Assert.That(Valid(s => s.FillerIntervalHours = 0), Is.False);
                Assert.That(Valid(s => s.FillerIntervalHours = 1), Is.True);
                Assert.That(Valid(s => s.FillerIntervalHours = 721), Is.False);
                Assert.That(Valid(s => s.FillerMaxSizeMb = 0), Is.False);
                Assert.That(Valid(s => s.FillerMaxSizeMb = 100000), Is.True);
                Assert.That(Valid(s => s.FillerMaxSizeMb = int.MaxValue), Is.False);
                Assert.That(Valid(s => s.FillerMinLeechers = 0), Is.True);
                Assert.That(Valid(s => s.FillerMinLeechers = -1), Is.False);
                Assert.That(Valid(s => s.FillerMaxSeeders = 0), Is.True);
                Assert.That(Valid(s => s.FillerMaxSeeders = -1), Is.False);
                Assert.That(Valid(s => s.FillerSlotMargin = 0), Is.True);
                Assert.That(Valid(s => s.FillerSlotMargin = -1), Is.False);
                Assert.That(Valid(s => s.FillerSearchPages = 0), Is.False);
                Assert.That(Valid(s => s.FillerSearchPages = 50), Is.True);
                Assert.That(Valid(s => s.FillerPreferredSearchPages = 0), Is.True, "0 turns the extra search off");
                Assert.That(Valid(s => s.FillerErrorPauseHours = 0), Is.False);
                Assert.That(Valid(s => s.FillerErrorPauseHours = 1), Is.True);
                Assert.That(Valid(s => s.BonusDailyPurchaseCap = 0), Is.False);
                Assert.That(Valid(s => s.BonusDailyPurchaseCap = 24), Is.True, "was capped at 10");
                Assert.That(Valid(s => s.BonusDailyPurchaseCap = 101), Is.False);
                Assert.That(Valid(s => s.BonusRetryAfterFailureHours = 0), Is.False);
                Assert.That(Valid(s => s.BonusRetryAfterFailureHours = 1), Is.True);
                Assert.That(Valid(s => s.UnsatisfiedSlotReserve = 0), Is.True);
                Assert.That(Valid(s => s.UnsatisfiedSlotReserve = -1), Is.False);
                Assert.That(Valid(s => s.ManualGrabBuffer = 10001), Is.False);
            });
        }

        [Test]
        public void out_of_range_stored_values_resolve_to_working_ones()
        {
            var settings = new MyAnonaMouseSettings { AccountStatusRefreshInterval = 0, FillerErrorPauseHours = -3, BonusRetryAfterFailureHours = 0 };

            Assert.Multiple(() =>
            {
                Assert.That(settings.ResolveAccountStatusRefreshInterval(), Is.EqualTo(TimeSpan.FromHours(1)));
                Assert.That(TalearrMamFillerRules.ErrorPause(settings), Is.EqualTo(TimeSpan.FromHours(12)));
                Assert.That(MyAnonaMouseBonusSpendRules.RetryAfterFailure(settings), Is.EqualTo(TimeSpan.FromHours(24)));
            });

            settings.AccountStatusRefreshInterval = int.MaxValue;
            Assert.That(settings.ResolveAccountStatusRefreshInterval(), Is.EqualTo(TimeSpan.FromDays(1)));
        }

        // Account status refresh.
        private sealed class StatusRig
        {
            public MyAnonaMouseSettings Settings { get; set; }
            public FakeMamHttpClient Http { get; set; }
            public MyAnonaMouseAccountStatusService Service { get; set; }
            public DateTime Clock { get; set; } = Now;

            public void Reply()
            {
                Http.Reply(HttpStatusCode.OK, "application/json", $"{{\"classname\":\"User\",\"seedbonus\":100,\"created\":{Created},\"unsat\":{{\"count\":1,\"limit\":20}}}}");
            }

            public void Scheduled()
            {
                Service.Execute(new RefreshMyAnonaMouseAccountStatusCommand { Trigger = CommandTrigger.Scheduled });
            }
        }

        private static StatusRig Rig(int refreshMinutes)
        {
            var rig = new StatusRig { Settings = new MyAnonaMouseSettings { MamId = "OK", AccountStatusRefreshInterval = refreshMinutes } };
            rig.Http = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out var httpFactory);
            var indexer = MamTestIndexers.Create(rig.Settings, httpFactory, new RecordingSessionMonitor());
            var indexers = FakeMamIndexerFactory.Create(out var indexerFactory);
            indexers.Providers.Add(indexer);
            SeedingFake<IMamUnsatisfiedSlotGuard>.Create(out var slotGuard);
            rig.Service = new MyAnonaMouseAccountStatusService(indexerFactory, slotGuard, LogManager.GetCurrentClassLogger());
            rig.Service.UtcNow = () => rig.Clock;
            return rig;
        }

        [Test]
        public void scheduled_refresh_follows_a_shorter_interval()
        {
            var rig = Rig(15);

            rig.Reply();
            rig.Scheduled();

            rig.Clock = Now.AddMinutes(10);
            rig.Scheduled();
            rig.Service.Handle(new HealthCheckCompleteEvent());
            Assert.That(rig.Http.Requests, Has.Count.EqualTo(1), "10 minutes is not yet due");

            rig.Clock = Now.AddMinutes(15);
            rig.Reply();
            rig.Scheduled();
            Assert.That(rig.Http.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void scheduled_refresh_follows_a_longer_interval()
        {
            var rig = Rig(240);

            rig.Reply();
            rig.Scheduled();

            rig.Clock = Now.AddHours(1);
            rig.Scheduled();
            rig.Clock = Now.AddHours(3);
            rig.Service.Handle(new HealthCheckCompleteEvent());
            Assert.That(rig.Http.Requests, Has.Count.EqualTo(1), "the old fixed hour no longer applies");

            rig.Clock = Now.AddHours(4);
            rig.Reply();
            rig.Scheduled();
            Assert.That(rig.Http.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void manual_refresh_ignores_the_interval()
        {
            var rig = Rig(240);

            rig.Reply();
            rig.Scheduled();
            rig.Reply();
            rig.Service.Execute(new RefreshMyAnonaMouseAccountStatusCommand { Trigger = CommandTrigger.Manual });

            Assert.That(rig.Http.Requests, Has.Count.EqualTo(2));
        }

        // Freshness windows stretch with a longer refresh so the guards do not pause between
        // refreshes, and keep their old size otherwise.
        [Test]
        public void status_freshness_follows_the_refresh_interval()
        {
            var settings = new MyAnonaMouseSettings
            {
                UserClass = "User",
                UnsatisfiedCount = 1,
                UnsatisfiedLimit = 20,
                UnsatisfiedSnapshotUtc = Now.AddHours(-3.5),
                UnsatisfiedStatusRefreshedUtc = Now.AddHours(-3.5)
            };

            Assert.Multiple(() =>
            {
                Assert.That(MamUnsatisfiedSlotGuard.HasFreshStatus(settings, Now), Is.False);
                Assert.That(MyAnonaMouseAccountStanding.HasFreshStatus(settings, Now), Is.False);
                Assert.That(MyAnonaMouseAccountStanding.StaleHours(settings), Is.EqualTo("3"));
            });

            settings.AccountStatusRefreshInterval = 240;

            Assert.Multiple(() =>
            {
                Assert.That(MamUnsatisfiedSlotGuard.HasFreshStatus(settings, Now), Is.True);
                Assert.That(MyAnonaMouseAccountStanding.HasFreshStatus(settings, Now), Is.True);
                Assert.That(MyAnonaMouseAccountStanding.StaleHours(settings), Is.EqualTo("6"));
            });

            settings.AccountStatusRefreshInterval = 5;
            Assert.That(settings.ResolveStatusMaxAge(TimeSpan.FromHours(2)), Is.EqualTo(TimeSpan.FromHours(2)), "a shorter refresh keeps the window");
        }

        // Task ticks.
        private static IndexerDefinition Mam(MyAnonaMouseSettings settings, bool enabled = true)
        {
            return new IndexerDefinition { Implementation = "MyAnonaMouse", Settings = settings, EnableRss = enabled };
        }

        [Test]
        public void account_status_task_ticks_at_the_shortest_refresh()
        {
            Assert.Multiple(() =>
            {
                Assert.That(MyAnonaMouseSchedule.AccountStatusTaskMinutes(new List<IndexerDefinition>()), Is.EqualTo(60));
                Assert.That(MyAnonaMouseSchedule.AccountStatusTaskMinutes(new[] { Mam(new MyAnonaMouseSettings()) }), Is.EqualTo(60));
                Assert.That(MyAnonaMouseSchedule.AccountStatusTaskMinutes(new[]
                {
                    Mam(new MyAnonaMouseSettings { AccountStatusRefreshInterval = 240 }),
                    Mam(new MyAnonaMouseSettings { AccountStatusRefreshInterval = 15 }),
                    Mam(new MyAnonaMouseSettings { AccountStatusRefreshInterval = 2 }, enabled: false)
                }), Is.EqualTo(15));
                Assert.That(MyAnonaMouseSchedule.AccountStatusTaskMinutes(new[] { Mam(new MyAnonaMouseSettings { AccountStatusRefreshInterval = 240 }) }), Is.EqualTo(240));
            });
        }

        [Test]
        public void seedbox_task_keeps_five_minutes_unless_an_interval_is_shorter()
        {
            Assert.Multiple(() =>
            {
                Assert.That(MyAnonaMouseSchedule.DynamicSeedboxTaskMinutes(null), Is.EqualTo(5));
                Assert.That(MyAnonaMouseSchedule.DynamicSeedboxTaskMinutes(new[] { Mam(new MyAnonaMouseSettings { DynamicSeedboxEnabled = true, DynamicSeedboxInterval = 60 }) }), Is.EqualTo(5));
                Assert.That(MyAnonaMouseSchedule.DynamicSeedboxTaskMinutes(new[] { Mam(new MyAnonaMouseSettings { DynamicSeedboxEnabled = true, DynamicSeedboxInterval = 2 }) }), Is.EqualTo(2));
                Assert.That(MyAnonaMouseSchedule.DynamicSeedboxTaskMinutes(new[] { Mam(new MyAnonaMouseSettings { DynamicSeedboxEnabled = false, DynamicSeedboxInterval = 2 }) }), Is.EqualTo(5));
            });
        }
    }
}
