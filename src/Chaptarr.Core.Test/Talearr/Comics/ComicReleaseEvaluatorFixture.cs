using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Search;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicReleaseEvaluatorFixture
    {
        private const long Mb = 1024 * 1024;

        private ComicEvaluationContext _context;

        [SetUp]
        public void SetUp()
        {
            var series = new ComicSeries
            {
                Id = 1,
                Kind = ComicKinds.Manga,
                Title = "Jujutsu Kaisen",
                AlternateTitles = "Juujutsu Kaisen",
                TotalVolumes = 26
            };

            var volumes = Enumerable.Range(1, 26).Select(n => new ComicVolume { Id = 100 + n, SeriesId = 1, Number = n, Monitored = true }).ToList();

            _context = new ComicEvaluationContext
            {
                Series = series,
                Volumes = volumes,
                WantedVolumeIds = new HashSet<int>(volumes.Select(v => v.Id)),
                Settings = new ComicSettings()
            };
        }

        private static ReleaseInfo Release(string title, long size, DownloadProtocol protocol = DownloadProtocol.Usenet)
        {
            return new ReleaseInfo { Title = title, Size = size, DownloadProtocol = protocol, PublishDate = DateTime.UtcNow };
        }

        [Test]
        public void should_accept_a_wanted_digital_volume()
        {
            var decision = ComicReleaseEvaluator.Evaluate(Release("VIZ.Media.Jujutsu.Kaisen.Vol.06.Black.Flash.2020.Hybrid.Comic.eBook-BitBook-xpost", 369 * Mb), _context);

            Assert.That(decision.Accepted, Is.True, string.Join("; ", decision.Rejections));
            Assert.That(decision.VolumeIds, Is.EqualTo(new[] { 106 }));
        }

        [Test]
        public void should_reject_numbered_chapters_by_count_and_size()
        {
            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu.Kaisen.211.[2023].[Digital].[anadius].cbz-xpost", 4 * Mb), _context);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Rejections.First(), Does.Contain("chapter 211"));
        }

        [Test]
        public void should_reject_a_small_bare_number_even_inside_the_volume_count()
        {
            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu.Kaisen.12.[2023].[Digital].[anadius].cbz-xpost", 5 * Mb), _context);

            Assert.That(decision.Accepted, Is.False);
        }

        [Test]
        public void should_take_a_large_bare_number_inside_the_volume_count_as_a_volume()
        {
            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen 12 (2021) (Digital) (1r0n)", 250 * Mb), _context);

            Assert.That(decision.Accepted, Is.True, string.Join("; ", decision.Rejections));
            Assert.That(decision.VolumeIds, Is.EqualTo(new[] { 112 }));
        }

        [TestCase("Jujutsu.Kaisen.Chapter.202.cbz-xpost", "Single chapter")]
        [TestCase("Jujutsu.Kaisen.v05.2021.FRENCH.HYBRiD.COMiC.iNTERNAL.CBZ.eBook-TONER-xpost", "Not in English")]
        [TestCase("Jujutsu Kaisen v05 (2021) (Asura Scans)", "scanlation")]
        [TestCase("Jujutsu Kaisen Omnibus v02 (2024) (Digital) (1r0n)", "Different edition")]
        [TestCase("Jujutsu.Kaisen.-.The.Official.Anime.Guide.-.Season.1.[2023].[Digital].[LuCaZ].cbz-xpost", "Different series")]
        [TestCase("Chainsaw Man v05 (2021) (Digital) (1r0n)", "Different series")]
        public void should_reject(string title, string reason)
        {
            var decision = ComicReleaseEvaluator.Evaluate(Release(title, 300 * Mb), _context);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(string.Join("; ", decision.Rejections), Does.Contain(reason));
        }

        [Test]
        public void should_reject_volumes_that_are_not_wanted()
        {
            _context.WantedVolumeIds = new HashSet<int> { 101 };

            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v05 (2021) (Digital) (1r0n)", 300 * Mb), _context);

            Assert.That(string.Join("; ", decision.Rejections), Does.Contain("not wanted"));
        }

        [Test]
        public void should_rank_digital_over_scan_over_c2c()
        {
            var digital = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v05 (2021) (Digital) (1r0n).cbz", 300 * Mb), _context);
            var scan = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v05 (2021) (Scan) (Someone).cbz", 300 * Mb), _context);
            var c2c = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v05 (2021) (c2c) (Someone).cbz", 300 * Mb), _context);

            Assert.That(digital.Score, Is.GreaterThan(scan.Score));
            Assert.That(scan.Score, Is.GreaterThan(c2c.Score));
        }

        [Test]
        public void should_switch_off_kinds_of_release()
        {
            _context.Settings.AllowC2C = false;

            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v05 (2021) (c2c) (Someone).cbz", 300 * Mb), _context);

            Assert.That(string.Join("; ", decision.Rejections), Does.Contain("c2c"));
        }

        [Test]
        public void should_prefer_one_pack_over_many_singles_when_both_cover_the_wanted_volumes()
        {
            _context.WantedVolumeIds = new HashSet<int> { 101, 102, 103 };

            var decisions = new[]
            {
                ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v01 (2019) (Digital) (1r0n)", 300 * Mb), _context),
                ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v02 (2019) (Digital) (1r0n)", 300 * Mb), _context),
                ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v01-v03 (2019) (Digital) (1r0n)", 900 * Mb), _context)
            };

            var picked = ComicReleaseEvaluator.PickBest(decisions);

            Assert.That(picked.Count, Is.EqualTo(1));
            Assert.That(picked[0].Release.Title, Does.Contain("v01-v03"));
        }

        [Test]
        public void should_refuse_packs_when_switched_off()
        {
            _context.Settings.AllowPacks = false;

            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen v01-v03 (2019) (Digital) (1r0n)", 900 * Mb), _context);

            Assert.That(string.Join("; ", decision.Rejections), Does.Contain("packs are switched off"));
        }

        [Test]
        public void should_treat_bare_numbers_as_issues_for_western_comics()
        {
            _context.Series.Kind = ComicKinds.Comic;
            _context.Series.Title = "Batman";
            _context.Series.AlternateTitles = null;

            var decision = ComicReleaseEvaluator.Evaluate(Release("Batman.686.(2009).(2.covers).(digital-Empire).cbr-xpost", 55 * Mb), _context);

            Assert.That(string.Join("; ", decision.Rejections), Does.Contain("Single issue"));
        }

        [Test]
        public void should_take_a_titled_one_shot_as_its_only_volume()
        {
            _context.Volumes = _context.Volumes.Take(1).ToList();
            _context.Series.TotalVolumes = 1;

            var decision = ComicReleaseEvaluator.Evaluate(Release("Jujutsu Kaisen (2021) (Digital) (1r0n)", 200 * Mb), _context);

            Assert.That(decision.Accepted, Is.True, string.Join("; ", decision.Rejections));
        }
    }
}
