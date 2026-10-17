using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.QualityLadder;

namespace Chaptarr.Core.Test.Talearr.QualityLadder
{
    [TestFixture]
    public class TalearrAudioLadderFixture
    {
        private const int TenHours = 10 * 3600;

        private static long SizeFor(int kbps, int seconds) => (long)kbps * 1000 / 8 * seconds;

        [TestCase("aac", 64, AudioQualityTier.Good)]
        [TestCase("aac", 48, AudioQualityTier.Low)]
        [TestCase("mp3", 128, AudioQualityTier.Good)]
        [TestCase("mp3", 96, AudioQualityTier.Low)]
        [TestCase("mp3", 24, AudioQualityTier.VeryLow)]
        [TestCase("aac", 31, AudioQualityTier.VeryLow)]
        [TestCase("flac", 400, AudioQualityTier.Good)]
        [TestCase(null, 96, AudioQualityTier.Good)]
        [TestCase(null, 80, AudioQualityTier.Low)]
        [TestCase("mp3", 5, AudioQualityTier.Implausible)]
        public void tiers_follow_codec_and_bitrate(string codec, int kbps, AudioQualityTier tier)
        {
            Assert.That(TalearrAudioLadder.TierFor(codec, kbps), Is.EqualTo(tier));
        }

        [Test]
        public void unknown_bitrate_is_unknown_tier()
        {
            Assert.That(TalearrAudioLadder.TierFor("aac", null), Is.EqualTo(AudioQualityTier.Unknown));
        }

        [TestCase("Author - Book [64kbps] (m4b)", 64)]
        [TestCase("Author - Book MP3 128 kbps", 128)]
        [TestCase("Author - Book [320k]", 320)]
        [TestCase("Author - Book 2019 Unabridged", null)]
        [TestCase("Author - 50 Short Stories", null)]
        public void title_bitrate_tokens_are_read(string title, int? kbps)
        {
            Assert.That(TalearrAudioLadder.ParseTitleKbps(title), Is.EqualTo(kbps));
        }

        [Test]
        public void release_bitrate_comes_from_size_per_minute_of_the_runtime()
        {
            var estimate = TalearrAudioLadder.EstimateRelease(Quality.M4B, "Author - Book", SizeFor(64, TenHours), TenHours);

            Assert.That(estimate.Kbps, Is.EqualTo(64));
            Assert.That(estimate.Codec, Is.EqualTo("aac"));
            Assert.That(estimate.Source, Is.EqualTo("size"));
            Assert.That(estimate.Tier, Is.EqualTo(AudioQualityTier.Good));
        }

        [Test]
        public void a_title_bitrate_wins_over_the_size_estimate()
        {
            var estimate = TalearrAudioLadder.EstimateRelease(Quality.MP3, "Author - Book [64kbps]", SizeFor(80, TenHours), TenHours);

            Assert.That(estimate.Kbps, Is.EqualTo(64));
            Assert.That(estimate.Tier, Is.EqualTo(AudioQualityTier.Low));
        }

        [Test]
        public void a_size_that_cannot_hold_the_runtime_is_implausible_whatever_the_title_says()
        {
            var estimate = TalearrAudioLadder.EstimateRelease(Quality.M4B, "Author - Book [128kbps]", 20L * 1024 * 1024, TenHours);

            Assert.That(estimate.Tier, Is.EqualTo(AudioQualityTier.Implausible));
        }

        [Test]
        public void without_a_runtime_only_a_tiny_release_is_judged()
        {
            Assert.That(TalearrAudioLadder.EstimateRelease(Quality.M4B, "Book", 3L * 1024 * 1024, null).Tier, Is.EqualTo(AudioQualityTier.Implausible));
            Assert.That(TalearrAudioLadder.EstimateRelease(Quality.M4B, "Book", 300L * 1024 * 1024, null).Tier, Is.EqualTo(AudioQualityTier.Unknown));
        }

        [Test]
        public void unknown_audio_takes_its_codec_from_the_title()
        {
            Assert.That(TalearrAudioLadder.CodecFor(Quality.UnknownAudio, "Book (M4B)"), Is.EqualTo("aac"));
            Assert.That(TalearrAudioLadder.CodecFor(Quality.UnknownAudio, "Book mp3"), Is.EqualTo("mp3"));
            Assert.That(TalearrAudioLadder.CodecFor(Quality.UnknownAudio, "Book"), Is.Null);
        }

        [Test]
        public void library_files_use_the_measured_bitrate_weighted_by_duration()
        {
            var files = new List<BookFile>
            {
                new BookFile { Path = "/a/1.mp3", Quality = new QualityModel(Quality.MP3), DurationSeconds = 3000, MediaInfo = new MediaInfoModel { AudioFormat = "MPEG Audio", AudioBitrate = 32 } },
                new BookFile { Path = "/a/2.mp3", Quality = new QualityModel(Quality.MP3), DurationSeconds = 1000, MediaInfo = new MediaInfoModel { AudioFormat = "MPEG Audio", AudioBitrate = 64 } }
            };

            var estimate = TalearrAudioLadder.EstimateFiles(files);

            Assert.That(estimate.Kbps, Is.EqualTo(40));
            Assert.That(estimate.Codec, Is.EqualTo("mp3"));
            Assert.That(estimate.Tier, Is.EqualTo(AudioQualityTier.Low));
        }

        [Test]
        public void library_files_without_media_info_use_size_per_duration()
        {
            var files = new List<BookFile>
            {
                new BookFile { Path = "/a/book.m4b", Quality = new QualityModel(Quality.M4B), DurationSeconds = TenHours, Size = SizeFor(32, TenHours) }
            };

            var estimate = TalearrAudioLadder.EstimateFiles(files);

            Assert.That(estimate.Kbps, Is.EqualTo(32));
            Assert.That(estimate.Codec, Is.EqualTo("aac"));
            Assert.That(estimate.Source, Is.EqualTo("size"));
        }

        [Test]
        public void clear_upgrade_needs_a_higher_tier_and_a_real_gain()
        {
            AudioQualityEstimate E(string codec, int? kbps) => new AudioQualityEstimate { Codec = codec, Kbps = kbps, Tier = TalearrAudioLadder.TierFor(codec, kbps) };

            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", 32), E("mp3", 128), 16), Is.True, "low mp3 to good mp3");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", 24), E("aac", 64), 16), Is.True, "very low to good across codecs");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("aac", 63), E("aac", 64), 16), Is.False, "a tier step of 1 kbps is not clear");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("aac", 64), E("aac", 128), 16), Is.False, "Good files are the ladder's cutoff");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", 32), E("mp3", 64), 16), Is.False, "same tier is not an upgrade");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", null), E("mp3", 128), 16), Is.False, "unknown files are never judged");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", 32), E("mp3", null), 16), Is.False, "unknown releases are never judged");
            Assert.That(TalearrAudioLadder.IsClearUpgrade(E("mp3", 20), E("mp3", 4), 16), Is.False, "an implausible release is never an upgrade");
        }
    }

    [TestFixture]
    public class TalearrEbookPreferenceFixture
    {
        [Test]
        public void default_order_is_epub_azw3_mobi_pdf()
        {
            Assert.That(TalearrEbookPreference.DefaultOrder, Is.EqualTo(new[] { "EPUB", "AZW3", "MOBI", "PDF" }));
            Assert.That(TalearrEbookPreference.Rank(Quality.EPUB, null), Is.LessThan(TalearrEbookPreference.Rank(Quality.PDF, null)));
        }

        [Test]
        public void order_is_normalized_and_unknown_names_dropped()
        {
            Assert.That(TalearrEbookPreference.Normalize(new[] { "pdf", " epub ", "PDF", "docx" }), Is.EqualTo(new[] { "PDF", "EPUB" }));
            Assert.That(TalearrEbookPreference.Normalize(new[] { "docx" }), Is.EqualTo(TalearrEbookPreference.DefaultOrder));
            Assert.That(TalearrEbookPreference.Parse("MOBI,EPUB"), Is.EqualTo(new[] { "MOBI", "EPUB" }));
        }

        [Test]
        public void formats_left_out_rank_after_the_listed_ones()
        {
            var order = new[] { "PDF" };

            Assert.That(TalearrEbookPreference.Rank(Quality.PDF, order), Is.EqualTo(0));
            Assert.That(TalearrEbookPreference.Rank(Quality.EPUB, order), Is.EqualTo(1));
            Assert.That(TalearrEbookPreference.Rank(Quality.M4B, order), Is.EqualTo(int.MaxValue));
        }

        [Test]
        public void preferred_means_strictly_earlier_than_every_file()
        {
            var order = TalearrEbookPreference.DefaultOrder;

            Assert.That(TalearrEbookPreference.IsPreferred(Quality.EPUB, new[] { Quality.PDF }, order), Is.True);
            Assert.That(TalearrEbookPreference.IsPreferred(Quality.EPUB, new[] { Quality.EPUB }, order), Is.False);
            Assert.That(TalearrEbookPreference.IsPreferred(Quality.MOBI, new[] { Quality.PDF, Quality.EPUB }, order), Is.False);
            Assert.That(TalearrEbookPreference.IsPreferred(Quality.EPUB, Array.Empty<Quality>(), order), Is.False);
            Assert.That(TalearrEbookPreference.IsPreferred(Quality.EPUB, new[] { Quality.MP3 }, order), Is.False);
        }
    }
}
