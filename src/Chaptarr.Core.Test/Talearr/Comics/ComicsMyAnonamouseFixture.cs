using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Search;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicsMyAnonamouseFixture
    {
        private static readonly int[] ComicCategories = { MyAnonaMouse.ComicsCategoryId };

        private ComicEvaluationContext _context;

        [SetUp]
        public void SetUp()
        {
            var series = new ComicSeries { Id = 1, Kind = ComicKinds.Manga, Title = "Witch Hat Atelier", TotalVolumes = 16, LanguageIso = "en" };
            var volumes = Enumerable.Range(1, 16).Select(n => new ComicVolume { Id = n, SeriesId = 1, Number = n, Monitored = true }).ToList();

            _context = new ComicEvaluationContext
            {
                Series = series,
                Volumes = volumes,
                WantedVolumeIds = new HashSet<int>(volumes.Select(v => v.Id)),
                Settings = new ComicSettings()
            };
        }

        private static List<MamComicResult> LoadResults()
        {
            var json = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Talearr", "Comics", "Fixtures", "mam-search-witch-hat-atelier.json"));
            var request = new HttpRequest("https://www.myanonamouse.net/tor/js/loadSearchJSONbasic.php");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, json, HttpStatusCode.OK);
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" };

            var results = MyAnonaMouse.ParseComicResults(new IndexerResponse(new IndexerRequest(request), response), settings);
            foreach (var result in results)
            {
                result.Release.Indexer = "MyAnonaMouse";
                ComicReleaseHints.Attach(result.Release, ComicReleaseHints.FromMam(result, ComicCategories));
            }

            return results;
        }

        private ComicReleaseDecision Evaluate(string title)
        {
            var release = LoadResults().Single(r => r.Release.Title == title).Release;
            return ComicReleaseEvaluator.Evaluate(release, _context);
        }

        [Test]
        public void search_payload_should_use_comic_site_categories_and_english()
        {
            var payload = MyAnonaMouse.BuildComicSearchPayload("Witch Hat Atelier", new[] { 61 }, "en", freeleechOnly: false, minimumSeeders: 1);
            var tor = (Dictionary<string, object>)payload["tor"];

            Assert.That(tor["cat"], Is.EqualTo(new[] { "61" }));
            Assert.That(tor.ContainsKey("main_cat"), Is.False);
            Assert.That(tor["browse_lang"], Is.EqualTo(new[] { "1" }));
            Assert.That(tor["searchType"], Is.EqualTo("active"));
            Assert.That(tor["text"], Is.EqualTo("Witch Hat Atelier"));
        }

        [Test]
        public void search_payload_should_ask_for_freeleech_only_when_the_indexer_is_freeleech_only()
        {
            var payload = MyAnonaMouse.BuildComicSearchPayload("Witch Hat Atelier", Array.Empty<int>(), "en", freeleechOnly: true, minimumSeeders: 0);
            var tor = (Dictionary<string, object>)payload["tor"];

            Assert.That(tor["searchType"], Is.EqualTo(MyAnonaMouse.FreeleechSearchType));
            Assert.That(tor["cat"], Is.EqualTo(new[] { "61" }));
        }

        [Test]
        public void should_read_series_info_and_tags()
        {
            var results = LoadResults();
            var first = results.Single(r => r.Release.Guid == "MAM-902111");

            Assert.That(results, Has.Count.EqualTo(6));
            Assert.That(first.SeriesName, Is.EqualTo("Witch Hat Atelier"));
            Assert.That(first.SeriesNumber, Is.EqualTo("8"));
            Assert.That(first.CategoryId, Is.EqualTo(61));
            Assert.That(first.Release.IndexerFlags.HasFlag(IndexerFlags.Freeleech), Is.True);
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("{\"1\": []}")]
        public void should_ignore_missing_or_malformed_series_info(string seriesInfo)
        {
            Assert.That(MyAnonaMouse.ReadSeriesInfo(seriesInfo), Is.EqualTo(((string)null, (string)null)));
        }

        [Test]
        public void should_accept_a_digital_cbz_volume()
        {
            var decision = Evaluate("Witch Hat Atelier, Vol. 8");

            Assert.That(decision.Accepted, Is.True, string.Join("; ", decision.Rejections));
            Assert.That(decision.VolumeIds, Is.EqualTo(new[] { 8 }));
            Assert.That(decision.Parsed.Format, Is.EqualTo(ComicFormats.Cbz));
            Assert.That(decision.Parsed.Source, Is.EqualTo(ComicSources.Digital));
        }

        [Test]
        public void should_take_a_scan_and_its_best_format_from_mam_fields()
        {
            var decision = Evaluate("Witch Hat Atelier Volume 9");

            Assert.That(decision.Accepted, Is.True, string.Join("; ", decision.Rejections));
            Assert.That(decision.VolumeIds, Is.EqualTo(new[] { 9 }));
            Assert.That(decision.Parsed.Source, Is.EqualTo(ComicSources.Scan));
            Assert.That(decision.Parsed.Format, Is.EqualTo(ComicFormats.Cbr));
            Assert.That(decision.Score, Is.LessThan(Evaluate("Witch Hat Atelier, Vol. 8").Score));
        }

        [Test]
        public void should_reject_another_language_from_lang_code()
        {
            var decision = Evaluate("Witch Hat Atelier 10");

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Rejections, Has.Some.Contains("Not in English"));
        }

        [Test]
        public void should_reject_a_chapter_and_a_scanlation()
        {
            var decision = Evaluate("Witch Hat Atelier ch 75");

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Rejections, Has.Some.Contains("scanlation").Or.Some.Contains("chapter"));
        }

        [Test]
        public void should_reject_a_spin_off_series()
        {
            var decision = Evaluate("Witch Hat Atelier Kitchen, Vol. 4");

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Rejections.First(), Does.Contain("Different series"));
        }

        [Test]
        public void should_reject_a_result_outside_the_comics_category()
        {
            var decision = Evaluate("Witch Hat Atelier, Vol. 11");

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Rejections.Single(), Does.Contain("Not in MAM's comics category"));
        }

        [Test]
        public void mam_rules_should_reject_a_non_freeleech_release_when_freeleech_only_is_on()
        {
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net", MamId = "id", FreeleechOnly = true };
            var rules = new ComicMamRules(new FakePolicy(settings), new FakeStandingGuard(null), new FakeSlotGuard(MamUnsatisfiedSlotAvailability.Accept()), null, LogManager.GetCurrentClassLogger());

            var results = LoadResults();
            var paid = results.Single(r => r.Release.Guid == "MAM-902112").Release;
            var free = results.Single(r => r.Release.Guid == "MAM-902111").Release;

            Assert.That(rules.GetRejections(paid), Has.Some.Contains("Freeleech Only"));
            Assert.That(rules.GetRejections(free), Is.Empty);
        }

        [Test]
        public void mam_rules_should_pass_on_standing_and_slot_rejections()
        {
            var settings = new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net", MamId = "id" };
            var free = LoadResults().Single(r => r.Release.Guid == "MAM-902111").Release;

            var standing = new ComicMamRules(new FakePolicy(settings), new FakeStandingGuard("Seed-only: no downloads"), new FakeSlotGuard(MamUnsatisfiedSlotAvailability.Accept()), null, LogManager.GetCurrentClassLogger());
            Assert.That(standing.GetRejections(free), Has.Some.Contains("Seed-only"));
            Assert.That(standing.Reserve(new RemoteBook { Release = free, IsComic = true }), Does.Contain("Seed-only"));

            var slots = new ComicMamRules(new FakePolicy(settings), new FakeStandingGuard(null), new FakeSlotGuard(MamUnsatisfiedSlotAvailability.Reject("No free torrent slots")), null, LogManager.GetCurrentClassLogger());
            Assert.That(slots.GetRejections(free), Has.Some.Contains("No free torrent slots"));
            Assert.That(slots.Reserve(new RemoteBook { Release = free, IsComic = true }), Does.Contain("No free torrent slots"));
        }

        [Test]
        public void mam_rules_should_ignore_other_indexers()
        {
            var rules = new ComicMamRules(new FakePolicy(new MyAnonaMouseSettings { FreeleechOnly = true, MamId = "id" }), new FakeStandingGuard("Seed-only"), new FakeSlotGuard(MamUnsatisfiedSlotAvailability.Reject("full")), null, LogManager.GetCurrentClassLogger());
            var release = new ReleaseInfo { Title = "Witch Hat Atelier v08 (2021) (Digital) (LuCaZ)", Guid = "nzb-1", Indexer = "NZBgeek" };

            Assert.That(rules.GetRejections(release), Is.Empty);
            Assert.That(rules.Reserve(new RemoteBook { Release = release, IsComic = true }), Is.Null);
        }

        private class FakePolicy : IMyAnonaMouseReleasePolicy
        {
            private readonly MyAnonaMouseSettings _settings;

            public FakePolicy(MyAnonaMouseSettings settings)
            {
                _settings = settings;
            }

            public MyAnonaMouseSettings ResolveSettings(ReleaseInfo release)
            {
                return _settings;
            }
        }

        private class FakeStandingGuard : IMyAnonaMouseStandingGuard
        {
            private readonly string _rejection;

            public FakeStandingGuard(string rejection)
            {
                _rejection = rejection;
            }

            public string GetRejection(RemoteBook remoteBook)
            {
                return _rejection;
            }
        }

        private class FakeSlotGuard : IMamUnsatisfiedSlotGuard
        {
            private readonly MamUnsatisfiedSlotAvailability _availability;

            public FakeSlotGuard(MamUnsatisfiedSlotAvailability availability)
            {
                _availability = availability;
            }

            public MamUnsatisfiedSlotAvailability Check(RemoteBook remoteBook)
            {
                return _availability;
            }

            public MamUnsatisfiedSlotAvailability TryReserve(RemoteBook remoteBook)
            {
                return _availability;
            }

            public void Reconcile(MyAnonaMouse indexer, MyAnonaMouseAccountStatus status)
            {
            }
        }
    }
}
