using System;
using System.Collections.Generic;
using Chaptarr.Core.Test;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    [TestFixture]
    public class MamFreeleechAndHealthFixture
    {
        private FakeMamIndexerFactory _indexers;
        private MyAnonaMouseReleasePolicy _policy;
        private MyAnonaMouseSettings _native;

        [SetUp]
        public void SetUp()
        {
            _indexers = FakeMamIndexerFactory.Create(out var factory);
            _native = new MyAnonaMouseSettings { MamId = "MAIN" };
            _indexers.Definitions.Add(new IndexerDefinition { Id = 9, Name = "MyAnonaMouse", EnableAutomaticSearch = true, Settings = _native });
            _indexers.Definitions.Add(new IndexerDefinition { Id = 3, Name = "MyAnonamouse (Prowlarr)", EnableAutomaticSearch = true, Settings = new NzbDrone.Core.Indexers.Torznab.TorznabSettings() });
            _indexers.Definitions.Add(new IndexerDefinition { Id = 4, Name = "OtherTracker", EnableAutomaticSearch = true, Settings = new NzbDrone.Core.Indexers.Torznab.TorznabSettings() });
            _policy = new MyAnonaMouseReleasePolicy(factory);
        }

        private static DownloadDecision Decision(int indexerId, string indexer, IndexerFlags flags, long size)
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true } }
            };

            return new DownloadDecision(new RemoteBook
            {
                Author = new Author { Id = 1, Name = "Jim Butcher", AudiobookQualityProfileId = 2, AudiobookQualityProfile = profile },
                Release = new ReleaseInfo { Title = "Storm Front", IndexerId = indexerId, Indexer = indexer, IndexerFlags = flags, Size = size },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel { Quality = Quality.M4B } }
            });
        }

        private DownloadDecisionComparer Comparer(IMyAnonaMouseReleasePolicy policy)
        {
            return new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), DelayProfileServiceProxy.Create(), null, LogManager.GetCurrentClassLogger(), mamReleasePolicy: policy);
        }

        [Test]
        public void prefer_freeleech_should_rank_a_smaller_freeleech_release_first()
        {
            var freeleech = Decision(9, "MyAnonaMouse", IndexerFlags.Freeleech, 100);
            var larger = Decision(9, "MyAnonaMouse", 0, 900);

            Assert.That(Comparer(_policy).Compare(freeleech, larger), Is.GreaterThan(0));
            Assert.That(Comparer(_policy).Compare(larger, freeleech), Is.LessThan(0));
        }

        [Test]
        public void without_prefer_freeleech_size_should_decide_as_before()
        {
            _native.PreferFreeleech = false;
            var freeleech = Decision(9, "MyAnonaMouse", IndexerFlags.Freeleech, 100);
            var larger = Decision(9, "MyAnonaMouse", 0, 900);

            Assert.That(Comparer(_policy).Compare(freeleech, larger), Is.LessThan(0));
            Assert.That(Comparer(null).Compare(freeleech, larger), Is.LessThan(0));
        }

        [Test]
        public void prowlarr_mam_results_should_follow_the_native_indexer_settings()
        {
            var vipFree = Decision(3, "MyAnonamouse (Prowlarr)", IndexerFlags.VipFreeleech, 100);
            var larger = Decision(3, "MyAnonamouse (Prowlarr)", 0, 900);

            Assert.That(Comparer(_policy).Compare(vipFree, larger), Is.GreaterThan(0));
        }

        [Test]
        public void other_trackers_freeleech_should_not_be_boosted()
        {
            var freeleech = Decision(4, "OtherTracker", IndexerFlags.Freeleech, 100);
            var larger = Decision(4, "OtherTracker", 0, 900);

            Assert.That(Comparer(_policy).Compare(freeleech, larger), Is.LessThan(0));
        }

        [Test]
        public void partial_freeleech_should_not_count_as_freeleech()
        {
            Assert.That(MyAnonaMouseReleasePolicy.IsFreeleech(new ReleaseInfo { IndexerFlags = IndexerFlags.Freeleech25 }), Is.False);
            Assert.That(MyAnonaMouseReleasePolicy.IsFreeleech(new ReleaseInfo { IndexerFlags = IndexerFlags.Freeleech }), Is.True);
        }

        [Test]
        public void freeleech_only_should_reject_paid_mam_releases_only()
        {
            _native.FreeleechOnly = true;
            var spec = new MyAnonaMouseFreeleechOnlySpecification(_policy, LogManager.GetCurrentClassLogger());

            Assert.That(spec.IsSatisfiedBy(Decision(9, "MyAnonaMouse", 0, 1).RemoteBook, null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(Decision(3, "MyAnonamouse (Prowlarr)", 0, 1).RemoteBook, null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(Decision(9, "MyAnonaMouse", IndexerFlags.Freeleech, 1).RemoteBook, null).Accepted, Is.True);
            Assert.That(spec.IsSatisfiedBy(Decision(4, "OtherTracker", 0, 1).RemoteBook, null).Accepted, Is.True);
        }

        [Test]
        public void freeleech_only_off_should_accept_everything()
        {
            var spec = new MyAnonaMouseFreeleechOnlySpecification(_policy, LogManager.GetCurrentClassLogger());

            Assert.That(spec.IsSatisfiedBy(Decision(9, "MyAnonaMouse", 0, 1).RemoteBook, null).Accepted, Is.True);
        }

        [Test]
        public void session_message_should_show_while_the_rejected_mam_id_is_still_configured()
        {
            var health = new MyAnonaMouseSessionHealth { RejectedMamId = "OLD" };

            Assert.That(TalearrMyAnonaMouseSessionCheck.GetSessionMessage("MAM", new MyAnonaMouseSettings { MamId = "OLD" }, health),
                Does.StartWith("MAM session expired: paste a new mam_id"));
            Assert.That(TalearrMyAnonaMouseSessionCheck.GetSessionMessage("MAM", new MyAnonaMouseSettings { MamId = "PASTED" }, health), Is.Null);
        }

        [TestCase(MyAnonaMouseSeedboxOutcome.NetworkMismatch, "same IP")]
        [TestCase(MyAnonaMouseSeedboxOutcome.NetworkMismatch, "Seedbox Proxy")]
        [TestCase(MyAnonaMouseSeedboxOutcome.NotAllowed, "Allow session to set dynamic seedbox")]
        [TestCase(MyAnonaMouseSeedboxOutcome.InvalidSession, "Paste a new mam_id")]
        public void seedbox_problems_should_explain_the_fix(MyAnonaMouseSeedboxOutcome outcome, string expected)
        {
            var settings = new MyAnonaMouseSettings { MamId = "MAIN", DynamicSeedboxEnabled = true };
            var health = new MyAnonaMouseSessionHealth
            {
                SeedboxMamId = "MAIN",
                Seedbox = new MyAnonaMouseSeedboxResult { Outcome = outcome, Message = "x" }
            };

            var message = TalearrMyAnonaMouseSessionCheck.GetSeedboxMessage("MAM", settings, health);

            if (expected == "same IP")
            {
                Assert.That(message, Does.Contain("torrent client's VPN IP"));
            }
            else
            {
                Assert.That(message, Does.Contain(expected));
            }
        }

        [Test]
        public void seedbox_message_should_hide_when_disabled_or_fine()
        {
            var health = new MyAnonaMouseSessionHealth
            {
                Seedbox = new MyAnonaMouseSeedboxResult { Outcome = MyAnonaMouseSeedboxOutcome.NetworkMismatch }
            };

            Assert.That(TalearrMyAnonaMouseSessionCheck.GetSeedboxMessage("MAM", new MyAnonaMouseSettings { DynamicSeedboxEnabled = false }, health), Is.Null);

            health.Seedbox = new MyAnonaMouseSeedboxResult { Outcome = MyAnonaMouseSeedboxOutcome.TooRecent };
            Assert.That(TalearrMyAnonaMouseSessionCheck.GetSeedboxMessage("MAM", new MyAnonaMouseSettings { DynamicSeedboxEnabled = true }, health), Is.Null);
        }

        private static MyAnonaMouseSettings Unsatisfied(int count, int limit, bool protect = true, int reserve = 5, int buffer = 0)
        {
            var now = DateTime.UtcNow;
            return new MyAnonaMouseSettings
            {
                ProtectUnsatisfiedSlots = protect,
                UnsatisfiedSlotReserve = reserve,
                ManualGrabBuffer = buffer,
                UnsatisfiedCount = count,
                UnsatisfiedLimit = limit,
                UnsatisfiedSnapshotUtc = now,
                UnsatisfiedStatusRefreshedUtc = now
            };
        }

        [Test]
        public void unsatisfied_well_below_the_limit_should_be_quiet()
        {
            Assert.That(TalearrMyAnonaMouseUnsatisfiedCheck.GetMessage("MAM", Unsatisfied(10, 50), DateTime.UtcNow), Is.Null);
        }

        [Test]
        public void unsatisfied_near_the_limit_should_warn()
        {
            Assert.That(TalearrMyAnonaMouseUnsatisfiedCheck.GetMessage("MAM", Unsatisfied(40, 50), DateTime.UtcNow), Does.Contain("approaching the limit"));
        }

        [Test]
        public void unsatisfied_inside_the_reserve_should_say_grabs_are_paused()
        {
            Assert.That(TalearrMyAnonaMouseUnsatisfiedCheck.GetMessage("MAM", Unsatisfied(45, 50), DateTime.UtcNow), Does.Contain("paused"));
        }

        [Test]
        public void unsatisfied_near_the_limit_without_protection_should_warn_about_suspension()
        {
            Assert.That(TalearrMyAnonaMouseUnsatisfiedCheck.GetMessage("MAM", Unsatisfied(46, 50, protect: false), DateTime.UtcNow), Does.Contain("24-hour"));
        }

        [Test]
        public void stale_unsatisfied_status_should_be_quiet()
        {
            var settings = Unsatisfied(49, 50);
            settings.UnsatisfiedStatusRefreshedUtc = DateTime.UtcNow.AddHours(-5);
            settings.UnsatisfiedSnapshotUtc = DateTime.UtcNow.AddHours(-5);

            Assert.That(TalearrMyAnonaMouseUnsatisfiedCheck.GetMessage("MAM", settings, DateTime.UtcNow), Is.Null);
        }
    }
}
