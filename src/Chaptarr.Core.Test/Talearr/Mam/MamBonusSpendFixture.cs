using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Talearr.Recordings;
using Chaptarr.Core.Test.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    public class FakeBonusPurchaseStore : IMyAnonaMouseBonusPurchaseStore
    {
        public List<MyAnonaMouseBonusPurchaseRecord> Rows { get; } = new List<MyAnonaMouseBonusPurchaseRecord>();
        public bool FailWrites { get; set; }
        private long _nextId = 1;

        public bool Record(MyAnonaMouseBonusPurchaseRecord record)
        {
            if (FailWrites)
            {
                return false;
            }

            record.Id = _nextId++;
            Rows.Add(record);
            return true;
        }

        public bool UpdateResult(MyAnonaMouseBonusPurchaseRecord record)
        {
            return Rows.Contains(record);
        }

        public List<MyAnonaMouseBonusPurchaseRecord> GetRecent(int? indexerId, int limit)
        {
            return Rows.Where(r => !indexerId.HasValue || r.IndexerId == indexerId.Value)
                .OrderByDescending(r => r.AttemptedAt)
                .Take(limit)
                .ToList();
        }
    }

    [TestFixture]
    public class MamBonusSpendRulesFixture
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        public static MyAnonaMouseSettings Fresh(DateTime now, string userClass = "Power User", double? points = 100000, int? wedges = 5)
        {
            return new MyAnonaMouseSettings
            {
                MamId = "OK",
                UserClass = userClass,
                IsVip = userClass?.Contains("VIP") == true,
                BonusPoints = points,
                FreeleechWedges = wedges,
                UnsatisfiedCount = 1,
                UnsatisfiedLimit = 50,
                UnsatisfiedSnapshotUtc = now.AddMinutes(-10),
                UnsatisfiedStatusRefreshedUtc = now.AddMinutes(-5)
            };
        }

        private static MyAnonaMouseBonusPurchaseRecord Sent(string type, DateTime at, bool success = true, bool dryRun = false)
        {
            return new MyAnonaMouseBonusPurchaseRecord { SpendType = type, AttemptedAt = at, Success = success, DryRun = dryRun, Message = success ? "ok" : "Not eligible" };
        }

        private static MyAnonaMouseBonusDecision Decide(MyAnonaMouseSettings settings, params MyAnonaMouseBonusPurchaseRecord[] history)
        {
            return MyAnonaMouseBonusSpendRules.Decide(settings, history, Now);
        }

        [Test]
        public void defaults_are_all_off_and_buy_nothing()
        {
            var settings = Fresh(Now, wedges: 0);

            Assert.Multiple(() =>
            {
                Assert.That(settings.BonusKeepVip || settings.BonusKeepWedges || settings.BonusRatioSafetyNet || settings.BonusDryRun, Is.False);
                Assert.That(settings.BonusReservePoints, Is.EqualTo(10000));
                Assert.That(settings.BonusVipRenewDays, Is.EqualTo(14));
                Assert.That(settings.BonusMinimumWedges, Is.EqualTo(2));
                Assert.That(settings.BonusMinimumRatio, Is.EqualTo(1.2));
                Assert.That(settings.BonusUploadGiB, Is.EqualTo(50));
                Assert.That(settings.BonusDailyPurchaseCap, Is.EqualTo(2));
                Assert.That(Decide(settings).Purchase, Is.Null);
                Assert.That(settings.Validate().IsValid, Is.True);
            });
        }

        [Test]
        public void stale_or_unknown_status_buys_nothing()
        {
            var stale = Fresh(Now, wedges: 0);
            stale.BonusKeepWedges = true;
            stale.UnsatisfiedStatusRefreshedUtc = Now.AddHours(-3);

            var unknown = Fresh(Now, points: null, wedges: 0);
            unknown.BonusKeepWedges = true;

            Assert.That(Decide(stale).Purchase, Is.Null);
            Assert.That(Decide(unknown).Purchase, Is.Null);
        }

        [TestCase(60000, true)]
        [TestCase(59999, false)]
        [TestCase(55000, false)]
        public void wedges_respect_the_reserve(double points, bool buys)
        {
            var settings = Fresh(Now, points: points, wedges: 1);
            settings.BonusKeepWedges = true;

            var purchase = Decide(settings).Purchase;

            Assert.That(purchase != null, Is.EqualTo(buys));
            if (buys)
            {
                Assert.That(purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Wedges));
                Assert.That(purchase.EstimatedCost, Is.EqualTo(50000));
            }
        }

        [Test]
        public void wedges_are_not_bought_when_stocked_or_unknown()
        {
            var stocked = Fresh(Now, wedges: 2);
            stocked.BonusKeepWedges = true;
            var unknown = Fresh(Now, wedges: null);
            unknown.BonusKeepWedges = true;

            Assert.That(Decide(stocked).Purchase, Is.Null);
            Assert.That(Decide(unknown).Purchase, Is.Null);
        }

        [Test]
        public void vip_is_bought_for_an_eligible_power_user_without_vip()
        {
            var settings = Fresh(Now, "Power User", points: 30000);
            settings.BonusKeepVip = true;

            var purchase = Decide(settings).Purchase;

            Assert.That(purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Vip));
            Assert.That(purchase.EstimatedCost, Is.EqualTo(16072), "90 days at 5,000 per 4 weeks");
        }

        [Test]
        public void vip_renews_only_below_the_threshold()
        {
            var plenty = Fresh(Now, "VIP");
            plenty.BonusKeepVip = true;
            plenty.VipUntilUtc = Now.AddDays(30);

            var due = Fresh(Now, "VIP");
            due.BonusKeepVip = true;
            due.VipUntilUtc = Now.AddDays(10);

            Assert.That(Decide(plenty).Purchase, Is.Null);
            Assert.That(Decide(due).Purchase.EstimatedCost, Is.EqualTo(14286), "80 missing days");
        }

        [Test]
        public void vip_with_unknown_expiry_is_left_alone()
        {
            var settings = Fresh(Now, "VIP");
            settings.BonusKeepVip = true;

            Assert.That(Decide(settings).Purchase, Is.Null);
        }

        [TestCase("Mouse")]
        [TestCase("User")]
        [TestCase("Elite VIP")]
        [TestCase("Torrent Master")]
        public void classes_that_cannot_buy_vip_are_skipped(string userClass)
        {
            var settings = Fresh(Now, userClass);
            settings.BonusKeepVip = true;

            Assert.That(Decide(settings).Purchase, Is.Null);
        }

        [Test]
        public void vip_cost_buys_at_least_seven_days()
        {
            Assert.That(MyAnonaMouseBonusSpendRules.VipCost(88), Is.EqualTo(1250));
            Assert.That(MyAnonaMouseBonusSpendRules.VipCost(-3), Is.EqualTo(16072));
        }

        [Test]
        public void vip_that_would_break_the_reserve_is_skipped()
        {
            var settings = Fresh(Now, "Power User", points: 26000);
            settings.BonusKeepVip = true;

            Assert.That(Decide(settings).Purchase, Is.Null);
        }

        [Test]
        public void ratio_safety_net_buys_upload_below_the_minimum_ratio()
        {
            var settings = Fresh(Now, points: 40000);
            settings.BonusRatioSafetyNet = true;
            settings.Ratio = 0.8;

            var purchase = Decide(settings).Purchase;

            Assert.That(purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Upload));
            Assert.That(purchase.AmountGiB, Is.EqualTo(50));
            Assert.That(purchase.EstimatedCost, Is.EqualTo(25000));
        }

        [Test]
        public void ratio_safety_net_uses_the_configured_amount()
        {
            var settings = Fresh(Now, points: 50000);
            settings.BonusRatioSafetyNet = true;
            settings.BonusUploadGiB = 80;
            settings.Ratio = 0.5;

            Assert.That(Decide(settings).Purchase.EstimatedCost, Is.EqualTo(40000));
        }

        [Test]
        public void ratio_safety_net_leaves_a_good_or_unknown_ratio_alone()
        {
            var good = Fresh(Now);
            good.BonusRatioSafetyNet = true;
            good.Ratio = 2.5;
            var unknown = Fresh(Now);
            unknown.BonusRatioSafetyNet = true;

            Assert.That(Decide(good).Purchase, Is.Null);
            Assert.That(Decide(unknown).Purchase, Is.Null);
        }

        [Test]
        public void mouse_or_seed_only_triggers_the_safety_net_even_without_a_ratio()
        {
            var mouse = Fresh(Now, "Mouse", points: 40000);
            mouse.BonusRatioSafetyNet = true;

            var seedOnly = Fresh(Now, "User", points: 40000);
            seedOnly.BonusRatioSafetyNet = true;
            seedOnly.Ratio = 3;
            seedOnly.SeedOnly = true;

            Assert.That(Decide(mouse).Purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Upload));
            Assert.That(Decide(seedOnly).Purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Upload));
        }

        [Test]
        public void mouse_with_too_few_points_buys_nothing()
        {
            var mouse = Fresh(Now, "Mouse", points: 7400);
            mouse.BonusRatioSafetyNet = true;

            Assert.That(Decide(mouse).Purchase, Is.Null);
        }

        [Test]
        public void one_purchase_per_run_ratio_first()
        {
            var settings = Fresh(Now, "Power User", points: 500000, wedges: 0);
            settings.BonusKeepVip = true;
            settings.BonusKeepWedges = true;
            settings.BonusRatioSafetyNet = true;
            settings.Ratio = 0.9;

            Assert.That(Decide(settings).Purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Upload));
        }

        [Test]
        public void a_failed_type_backs_off_for_24_hours_and_the_next_rule_runs()
        {
            var settings = Fresh(Now, "Power User", points: 500000, wedges: 0);
            settings.BonusKeepVip = true;
            settings.BonusKeepWedges = true;

            var recentFailure = Decide(settings, Sent("VIP", Now.AddHours(-2), success: false));
            var oldFailure = Decide(settings, Sent("VIP", Now.AddHours(-25), success: false));

            Assert.That(recentFailure.Purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Wedges));
            Assert.That(recentFailure.Skipped.Any(s => s.Contains("backing off")), Is.True);
            Assert.That(oldFailure.Purchase.SpendType, Is.EqualTo(MyAnonaMouseBonusSpendType.Vip));
        }

        [Test]
        public void daily_cap_counts_real_attempts_only()
        {
            var settings = Fresh(Now, points: 500000, wedges: 0);
            settings.BonusKeepWedges = true;

            var capped = Decide(settings, Sent("wedges", Now.AddHours(-20)), Sent("wedges", Now.AddHours(-10)));
            var dryRuns = Decide(settings, Sent("wedges", Now.AddHours(-20), dryRun: true), Sent("wedges", Now.AddHours(-10), dryRun: true), Sent("wedges", Now.AddHours(-2), dryRun: true));
            var expired = Decide(settings, Sent("wedges", Now.AddHours(-30)), Sent("wedges", Now.AddHours(-10)));

            Assert.That(capped.Purchase, Is.Null);
            Assert.That(dryRuns.Purchase, Is.Not.Null);
            Assert.That(expired.Purchase, Is.Not.Null);
        }

        [Test]
        public void waits_for_an_account_refresh_after_a_purchase()
        {
            var settings = Fresh(Now, points: 500000, wedges: 0);
            settings.BonusKeepWedges = true;

            var decision = Decide(settings, Sent("wedges", Now.AddMinutes(-1)));

            Assert.That(decision.Purchase, Is.Null);
            Assert.That(decision.Skipped.Single(), Does.Contain("account refresh"));
        }

        [TestCase(0, false)]
        [TestCase(84, false)]
        [TestCase(83, true)]
        public void vip_threshold_is_validated(int days, bool valid)
        {
            var settings = Fresh(Now);
            settings.BonusVipRenewDays = days;

            Assert.That(settings.Validate().IsValid, Is.EqualTo(valid));
        }

        [Test]
        public void upload_amount_below_50_gib_is_rejected()
        {
            var settings = Fresh(Now);
            settings.BonusUploadGiB = 49;

            Assert.That(settings.Validate().IsValid, Is.False);
        }
    }

    [TestFixture]
    public class MamBonusRequestFixture
    {
        [Test]
        public void vip_request_uses_duration_max()
        {
            var purchase = new MyAnonaMouseBonusPurchase { SpendType = MyAnonaMouseBonusSpendType.Vip };

            Assert.That(MyAnonaMouseBonusRequestBuilder.BuildParameters(purchase), Is.EqualTo("spendtype=VIP&duration=max"));
        }

        [Test]
        public void upload_request_sends_whole_gib()
        {
            var purchase = new MyAnonaMouseBonusPurchase { SpendType = MyAnonaMouseBonusSpendType.Upload, AmountGiB = 50 };

            Assert.That(MyAnonaMouseBonusRequestBuilder.BuildParameters(purchase), Is.EqualTo("spendtype=upload&amount=50"));
        }

        [TestCase(null)]
        [TestCase(49)]
        public void upload_below_the_minimum_is_never_built(int? gib)
        {
            var purchase = new MyAnonaMouseBonusPurchase { SpendType = MyAnonaMouseBonusSpendType.Upload, AmountGiB = gib };

            Assert.Throws<ArgumentOutOfRangeException>(() => MyAnonaMouseBonusRequestBuilder.BuildQuery(purchase));
        }

        [Test]
        public void wedge_request_has_only_the_spend_type_and_the_documented_endpoint()
        {
            var purchase = new MyAnonaMouseBonusPurchase { SpendType = MyAnonaMouseBonusSpendType.Wedges };

            Assert.That(MyAnonaMouseBonusRequestBuilder.BuildUrl("https://www.myanonamouse.net/", purchase),
                Is.EqualTo("https://www.myanonamouse.net/json/bonusBuy.php?spendtype=wedges"));
        }

        [Test]
        public void gift_and_send_wedge_are_unreachable()
        {
            var names = Enum.GetNames(typeof(MyAnonaMouseBonusSpendType));
            var sent = Enum.GetValues(typeof(MyAnonaMouseBonusSpendType))
                .Cast<MyAnonaMouseBonusSpendType>()
                .Select(t => MyAnonaMouseBonusRequestBuilder.SpendTypeValue(t))
                .ToList();

            Assert.Multiple(() =>
            {
                Assert.That(names, Is.EquivalentTo(new[] { "Vip", "Upload", "Wedges" }));
                Assert.That(sent, Is.EquivalentTo(new[] { "VIP", "upload", "wedges" }));
                Assert.That(MyAnonaMouseBonusRequestBuilder.AllowedSpendTypes, Is.EquivalentTo(new[] { "VIP", "upload", "wedges" }));
                Assert.That(MyAnonaMouseBonusRequestBuilder.AllowedSpendTypes.Any(t => t.Contains("gift", StringComparison.OrdinalIgnoreCase) || t.Contains("sendWedge", StringComparison.OrdinalIgnoreCase)), Is.False);
                Assert.Throws<ArgumentOutOfRangeException>(() => MyAnonaMouseBonusRequestBuilder.SpendTypeValue((MyAnonaMouseBonusSpendType)3));
                Assert.Throws<ArgumentOutOfRangeException>(() => MyAnonaMouseBonusRequestBuilder.BuildQuery(new MyAnonaMouseBonusPurchase { SpendType = (MyAnonaMouseBonusSpendType)99 }));
            });
        }

        [Test]
        public void only_the_bonus_store_endpoint_is_used()
        {
            Assert.That(MyAnonaMouseBonusRequestBuilder.EndpointPath, Is.EqualTo("/json/bonusBuy.php"));
        }
    }

    [TestFixture]
    public class MamBonusResponseFixture
    {
        [TestCase("{\"success\":true}")]
        [TestCase("{\"success\":\"true\",\"seedbonus\":1234}")]
        [TestCase("{\"success\":1,\"message\":\"Bought\"}")]
        public void explicit_success_is_a_purchase(string body)
        {
            var result = MyAnonaMouseBonusResponseParser.Parse(HttpStatusCode.OK, body);

            Assert.That(result.Success, Is.True);
            Assert.That(result.RawResponse, Is.EqualTo(body));
        }

        [Test]
        public void remaining_points_are_read_when_sent()
        {
            Assert.That(MyAnonaMouseBonusResponseParser.Parse(HttpStatusCode.OK, "{\"success\":true,\"seedbonus\":\"1,234.5\"}").RemainingPoints, Is.EqualTo(1234.5));
        }

        [TestCase("{\"success\":false,\"error\":\"You are not eligible for VIP\"}", "You are not eligible for VIP")]
        [TestCase("{\"error\":\"Not enough points\"}", "Unrecognised response: Not enough points")]
        [TestCase("{\"foo\":1}", "Unrecognised response")]
        [TestCase("[1,2]", "Unrecognised response (not a JSON object)")]
        [TestCase("<html>Error, you are not signed in</html>", "Unrecognised response (not a JSON object)")]
        [TestCase("", "Empty response")]
        [TestCase("{\"success\":\"maybe\"}", "Unrecognised response")]
        public void anything_else_is_a_failure(string body, string message)
        {
            var result = MyAnonaMouseBonusResponseParser.Parse(HttpStatusCode.OK, body);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.StartWith(message));
        }

        [Test]
        public void http_errors_are_failures()
        {
            var result = MyAnonaMouseBonusResponseParser.Parse(HttpStatusCode.InternalServerError, "{\"success\":true}");

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("500"));
        }

        [Test]
        public void raw_body_is_collapsed_and_truncated()
        {
            var body = "{\"error\":\"" + new string('x', 2000) + "\"}\n\n  ";
            var result = MyAnonaMouseBonusResponseParser.Parse(HttpStatusCode.OK, body);

            Assert.That(result.RawResponse.Length, Is.EqualTo(MyAnonaMouseBonusResponseParser.MaxRawLength + 3));
            Assert.That(MyAnonaMouseBonusResponseParser.Truncate("a \n\t b"), Is.EqualTo("a b"));
        }
    }

    [TestFixture]
    public class MamAccountExtrasFixture
    {
        private FakeMamHttpClient _client;
        private IIndexerHttpClientFactory _factoryProxy;

        [SetUp]
        public void SetUp()
        {
            _client = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out _factoryProxy);
        }

        private static long Created => DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();

        private MyAnonaMouseSettings Refresh(string extra)
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };
            _client.Reply(HttpStatusCode.OK, "application/json", $"{{\"classname\":\"VIP\"{extra},\"created\":{Created},\"unsat\":{{\"count\":1,\"limit\":50}}}}");
            MamTestIndexers.Create(settings, _factoryProxy, new RecordingSessionMonitor()).RefreshAccountStatus().GetAwaiter().GetResult();
            return settings;
        }

        [Test]
        public void reads_vip_expiry_ratio_and_bytes()
        {
            var settings = Refresh(",\"vip_until\":\"2026-12-01 10:00:00\",\"ratio\":\"0.512\",\"uploaded_bytes\":1000,\"downloaded_bytes\":4000");

            Assert.Multiple(() =>
            {
                Assert.That(settings.VipUntilUtc, Is.EqualTo(new DateTime(2026, 12, 1, 10, 0, 0, DateTimeKind.Utc)));
                Assert.That(settings.Ratio, Is.EqualTo(0.512).Within(0.0001));
                Assert.That(settings.UploadedBytes, Is.EqualTo(1000));
                Assert.That(settings.DownloadedBytes, Is.EqualTo(4000));
                Assert.That(settings.SeedOnly, Is.Null);
            });
        }

        [Test]
        public void ratio_falls_back_to_bytes_and_vip_expiry_accepts_unix_seconds()
        {
            var settings = Refresh(",\"vip_until\":1796119200,\"uploaded_bytes\":\"3000\",\"downloaded_bytes\":1000,\"seedonly\":1");

            Assert.That(settings.Ratio, Is.EqualTo(3.0));
            Assert.That(settings.VipUntilUtc, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1796119200).UtcDateTime));
            Assert.That(settings.SeedOnly, Is.True);
        }

        [TestCase(",\"ratio\":\"Inf.\",\"uploaded_bytes\":0,\"downloaded_bytes\":0")]
        [TestCase(",\"vip_until\":\"0000-00-00 00:00:00\",\"ratio\":{\"x\":1}")]
        [TestCase(",\"vip_until\":[1],\"ratio\":null,\"seed_only\":\"perhaps\"")]
        [TestCase("")]
        public void odd_values_stay_unknown_and_never_fail_the_refresh(string extra)
        {
            var settings = Refresh(extra);

            Assert.Multiple(() =>
            {
                Assert.That(settings.VipUntilUtc, Is.Null);
                Assert.That(settings.Ratio, Is.Null);
                Assert.That(settings.SeedOnly, Is.Null);
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(1));
            });
        }
    }

    [TestFixture]
    public class MamBonusSpendServiceFixture
    {
        private FakeMamHttpClient _client;
        private IIndexerHttpClientFactory _factoryProxy;
        private FakeMamIndexerFactory _indexers;
        private IIndexerFactory _indexerFactory;
        private FakeBonusPurchaseStore _store;
        private FakeEventAggregator _events;
        private MyAnonaMouseBonusSpendService _service;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _client = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out _factoryProxy);
            _indexers = FakeMamIndexerFactory.Create(out _indexerFactory);
            _store = new FakeBonusPurchaseStore();
            _events = new FakeEventAggregator();
            _now = DateTime.UtcNow;
            _service = new MyAnonaMouseBonusSpendService(_indexerFactory, _store, _events, LogManager.GetCurrentClassLogger())
            {
                UtcNow = () => _now
            };
        }

        private MyAnonaMouseSettings AddIndexer(Action<MyAnonaMouseSettings> configure)
        {
            var settings = MamBonusSpendRulesFixture.Fresh(_now, points: 70000, wedges: 0);
            configure(settings);
            _indexers.Providers.Add(MamTestIndexers.Create(settings, _factoryProxy, new RecordingSessionMonitor()));
            return settings;
        }

        private void ReplyRefresh(double points, int wedges)
        {
            _client.Reply(HttpStatusCode.OK, "application/json",
                $"{{\"classname\":\"Power User\",\"seedbonus\":{points},\"wedges\":{wedges},\"created\":{DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds()},\"unsat\":{{\"count\":1,\"limit\":50}}}}");
        }

        [Test]
        public void buys_one_wedge_records_it_notifies_and_refreshes()
        {
            var settings = AddIndexer(s => s.BonusKeepWedges = true);
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"success\":true}");
            ReplyRefresh(20000, 1);

            var attempts = _service.Run("test");

            Assert.Multiple(() =>
            {
                Assert.That(_client.Requests, Has.Count.EqualTo(2));
                Assert.That(_client.Requests[0].Url.FullUri, Is.EqualTo("https://www.myanonamouse.net/json/bonusBuy.php?spendtype=wedges"));
                Assert.That(_client.Requests[0].Cookies["mam_id"], Is.EqualTo("OK"));
                Assert.That(_client.Requests[1].Url.FullUri, Does.Contain("jsonLoad.php"));
                Assert.That(attempts.Single().Success, Is.True);
                Assert.That(_store.Rows.Single().Parameters, Is.EqualTo("spendtype=wedges"));
                Assert.That(settings.FreeleechWedges, Is.EqualTo(1), "refreshed right after the purchase");
                Assert.That(_indexers.Updated, Is.Not.Empty);

                var notice = _events.Published.OfType<HealthCheckFailedEvent>().Single().HealthCheck;
                Assert.That(notice.Type, Is.EqualTo(HealthCheckResult.Notice));
                Assert.That(notice.Source.Name, Does.StartWith("Talearr"));
                Assert.That(notice.Message, Does.Contain("freeleech wedge"));
            });
        }

        [Test]
        public void dry_run_sends_nothing()
        {
            AddIndexer(s =>
            {
                s.BonusKeepWedges = true;
                s.BonusDryRun = true;
            });

            var attempts = _service.Run("test");

            Assert.That(_client.Requests, Is.Empty);
            Assert.That(attempts.Single().DryRun, Is.True);
            Assert.That(_store.Rows.Single().DryRun, Is.True);
            Assert.That(_events.Published, Is.Empty);
        }

        [Test]
        public void a_refusal_is_logged_and_not_retried_on_the_next_run()
        {
            AddIndexer(s => s.BonusKeepWedges = true);
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"success\":false,\"error\":\"Not enough points\"}");
            ReplyRefresh(70000, 0);

            var first = _service.Run("test");
            _now = _now.AddMinutes(2);
            var second = _service.Run("test");

            Assert.Multiple(() =>
            {
                Assert.That(first.Single().Success, Is.False);
                Assert.That(first.Single().Message, Is.EqualTo("Not enough points"));
                Assert.That(second, Is.Empty);
                Assert.That(_client.Requests.Count(r => r.Url.FullUri.Contains("bonusBuy")), Is.EqualTo(1));
                Assert.That(_events.Published.OfType<HealthCheckFailedEvent>().Single().HealthCheck.Type, Is.EqualTo(HealthCheckResult.Warning));
            });
        }

        [Test]
        public void nothing_is_bought_when_the_log_cannot_be_written()
        {
            AddIndexer(s => s.BonusKeepWedges = true);
            _store.FailWrites = true;

            Assert.That(_service.Run("test"), Is.Empty);
            Assert.That(_client.Requests, Is.Empty);
        }

        [Test]
        public void indexers_without_rules_are_not_touched()
        {
            AddIndexer(s => { });

            Assert.That(_service.Run("test"), Is.Empty);
            Assert.That(_client.Requests, Is.Empty);
        }

        [Test]
        public void a_stale_status_is_refreshed_before_deciding()
        {
            var settings = AddIndexer(s => s.BonusKeepWedges = true);
            settings.UnsatisfiedStatusRefreshedUtc = _now.AddHours(-3);
            ReplyRefresh(5000, 0);

            var attempts = _service.Run("test");

            Assert.That(attempts, Is.Empty, "after the refresh the balance is below the reserve");
            Assert.That(_client.Requests.Single().Url.FullUri, Does.Contain("jsonLoad.php"));
        }
    }

    [TestFixture]
    public class MamUploadCreditCheckFixture
    {
        [Test]
        public void mouse_account_gets_the_upload_credit_advice()
        {
            var now = DateTime.UtcNow;
            var settings = MamBonusSpendRulesFixture.Fresh(now, "Mouse", points: 7400);

            var message = TalearrMyAnonaMouseUploadCreditCheck.GetMessage("MAM", settings, now);

            Assert.That(message, Does.Contain("Buy upload credit in the Bonus Points Store (API minimum 50 GB = 25,000 points)."));
            Assert.That(message, Does.Contain("7,400"));
        }

        [Test]
        public void healthy_accounts_get_nothing()
        {
            var now = DateTime.UtcNow;

            Assert.That(TalearrMyAnonaMouseUploadCreditCheck.GetMessage("MAM", MamBonusSpendRulesFixture.Fresh(now, "Power User"), now), Is.Null);
        }

        [Test]
        public void seed_only_mentions_the_safety_net()
        {
            var now = DateTime.UtcNow;
            var settings = MamBonusSpendRulesFixture.Fresh(now, "User");
            settings.SeedOnly = true;
            settings.BonusRatioSafetyNet = true;

            Assert.That(TalearrMyAnonaMouseUploadCreditCheck.GetMessage("MAM", settings, now), Does.Contain("seed-only").And.Contain("Ratio Safety Net"));
        }
    }

    [TestFixture]
    public class MamBonusPurchaseStoreFixture
    {
        [Test]
        public void records_updates_and_lists_newest_first()
        {
            using var folder = new TalearrTestFolder();
            var database = folder.CreateDatabase();
            var store = new MyAnonaMouseBonusPurchaseStore(database, LogManager.GetCurrentClassLogger());
            var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            var first = new MyAnonaMouseBonusPurchaseRecord { IndexerId = 9, AttemptedAt = now.AddHours(-2), SpendType = "wedges", Parameters = "spendtype=wedges", EstimatedCost = 50000, PointsBefore = 70000, Message = "Sent" };
            var second = new MyAnonaMouseBonusPurchaseRecord { IndexerId = 9, AttemptedAt = now, SpendType = "VIP", Parameters = "spendtype=VIP&duration=max", DryRun = true };
            var other = new MyAnonaMouseBonusPurchaseRecord { IndexerId = 3, AttemptedAt = now, SpendType = "upload" };

            Assert.That(store.Record(first) && store.Record(second) && store.Record(other), Is.True);

            first.Success = true;
            first.Message = "ok";
            first.Response = "{\"success\":true}";
            Assert.That(store.UpdateResult(first), Is.True);

            var rows = store.GetRecent(9, 10);

            Assert.Multiple(() =>
            {
                Assert.That(database.SchemaVersion, Is.EqualTo(TalearrDatabase.CurrentSchemaVersion));
                Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.GreaterThanOrEqualTo(7));
                Assert.That(rows.Select(r => r.SpendType), Is.EqualTo(new[] { "VIP", "wedges" }));
                Assert.That(rows[0].DryRun, Is.True);
                Assert.That(rows[1].Success, Is.True);
                Assert.That(rows[1].Response, Is.EqualTo("{\"success\":true}"));
                Assert.That(rows[1].PointsBefore, Is.EqualTo(70000));
                Assert.That(rows[1].AttemptedAt, Is.EqualTo(now.AddHours(-2)));
                Assert.That(store.GetRecent(null, 10), Has.Count.EqualTo(3));
            });
        }
    }
}
