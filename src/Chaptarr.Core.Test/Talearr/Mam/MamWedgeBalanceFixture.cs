using System;
using System.Linq;
using System.Net;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    [TestFixture]
    public class MamWedgeBalanceFixture
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

        private MyAnonaMouseSettings Refresh(string json)
        {
            var settings = new MyAnonaMouseSettings { MamId = "OK" };
            _client.Reply(HttpStatusCode.OK, "application/json", json);
            MamTestIndexers.Create(settings, _factoryProxy, new RecordingSessionMonitor()).RefreshAccountStatus().GetAwaiter().GetResult();
            return settings;
        }

        [Test]
        public void account_refresh_reads_wedges_and_bonus_points_next_to_the_class()
        {
            var settings = Refresh($"{{\"classname\":\"Power User\",\"seedbonus\":51234.7,\"wedges\":3,\"snatch_summary\":{{\"created\":{Created},\"unsat\":{{\"count\":4,\"limit\":50}}}}}}");

            Assert.Multiple(() =>
            {
                Assert.That(settings.FreeleechWedges, Is.EqualTo(3));
                Assert.That(settings.BonusPoints, Is.EqualTo(51234.7).Within(0.001));
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(4));
                Assert.That(_client.Requests.Single().Method, Is.EqualTo(System.Net.Http.HttpMethod.Get), "read only, nothing is bought");
            });
        }

        [Test]
        public void numbers_sent_as_strings_are_read()
        {
            var settings = Refresh($"{{\"classname\":\"User\",\"seedbonus\":\"1,250.5\",\"wedges\":\"0\",\"created\":{Created},\"unsat\":{{\"count\":0,\"limit\":20}}}}");

            Assert.That(settings.FreeleechWedges, Is.Zero);
            Assert.That(settings.BonusPoints, Is.EqualTo(1250.5).Within(0.001));
        }

        [TestCase("")]
        [TestCase(",\"wedges\":null,\"seedbonus\":\"n/a\"")]
        [TestCase(",\"wedges\":{\"x\":1},\"seedbonus\":-5")]
        public void missing_or_odd_fields_leave_the_balance_unknown_and_never_fail_the_refresh(string extra)
        {
            var settings = Refresh($"{{\"classname\":\"User\"{extra},\"created\":{Created},\"unsat\":{{\"count\":1,\"limit\":20}}}}");

            Assert.Multiple(() =>
            {
                Assert.That(settings.FreeleechWedges, Is.Null);
                Assert.That(settings.BonusPoints, Is.Null);
                Assert.That(settings.UnsatisfiedCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void test_message_lists_the_balance_only_when_known()
        {
            Assert.That(MyAnonaMouseAccountStatus.DescribeWallet(2, 51234.7), Is.EqualTo("; freeleech wedges: 2, bonus points: 51,234"));
            Assert.That(MyAnonaMouseAccountStatus.DescribeWallet(null, null), Is.Empty);
        }

        private static MyAnonaMouseSettings Fresh(int? wedges, MyAnonaMouseFreeleechWedgeAction action, double? points = 9000)
        {
            var now = DateTime.UtcNow;
            return new MyAnonaMouseSettings
            {
                MamId = "OK",
                UseFreeleechWedge = (int)action,
                FreeleechWedges = wedges,
                BonusPoints = points,
                UnsatisfiedCount = 1,
                UnsatisfiedLimit = 50,
                UnsatisfiedSnapshotUtc = now,
                UnsatisfiedStatusRefreshedUtc = now
            };
        }

        [Test]
        public void health_notice_when_prefer_wedge_is_on_and_no_wedges_are_left()
        {
            var message = TalearrMyAnonaMouseWedgeCheck.GetMessage("MAM", Fresh(0, MyAnonaMouseFreeleechWedgeAction.Preferred), DateTime.UtcNow);

            Assert.That(message, Does.Contain("no freeleech wedges left").And.Contain("9,000 bonus points"));
        }

        [Test]
        public void no_health_notice_with_wedges_left_unknown_balance_stale_status_or_wedges_off()
        {
            var now = DateTime.UtcNow;
            var stale = Fresh(0, MyAnonaMouseFreeleechWedgeAction.Preferred);
            stale.UnsatisfiedStatusRefreshedUtc = now.AddDays(-3);
            stale.UnsatisfiedSnapshotUtc = now.AddDays(-3);

            Assert.Multiple(() =>
            {
                Assert.That(TalearrMyAnonaMouseWedgeCheck.GetMessage("MAM", Fresh(2, MyAnonaMouseFreeleechWedgeAction.Preferred), now), Is.Null);
                Assert.That(TalearrMyAnonaMouseWedgeCheck.GetMessage("MAM", Fresh(null, MyAnonaMouseFreeleechWedgeAction.Preferred), now), Is.Null);
                Assert.That(TalearrMyAnonaMouseWedgeCheck.GetMessage("MAM", Fresh(0, MyAnonaMouseFreeleechWedgeAction.Never), now), Is.Null);
                Assert.That(TalearrMyAnonaMouseWedgeCheck.GetMessage("MAM", stale, now), Is.Null);
            });
        }
    }
}
