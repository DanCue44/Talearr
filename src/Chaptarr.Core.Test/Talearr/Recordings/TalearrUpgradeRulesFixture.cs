using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // One test (or more) per upgrade rule in TalearrUpgradeRules.
    [TestFixture]
    public class TalearrUpgradeRulesFixture
    {
        private static readonly UpgradeProfileComparison SameQuality = new UpgradeProfileComparison();

        private static RecordingAudioFacts Audio(int minutes, int? kbps = 64, string codec = "aac", int? channels = 2, int files = 1, bool m4b = true, AbridgedMarker abridged = AbridgedMarker.Unknown, params string[] narrators)
        {
            return new RecordingAudioFacts
            {
                DurationSeconds = minutes * 60,
                BitrateKbps = kbps,
                Codec = codec,
                Channels = channels,
                FileCount = files,
                SingleM4b = m4b && files == 1,
                Abridged = abridged,
                Narrators = new List<string>(narrators)
            };
        }

        private static UpgradeVerdict Decide(RecordingAudioFacts existing, RecordingAudioFacts incoming, UpgradeProfileComparison profile = null, TalearrRecordingsSettings settings = null)
        {
            return TalearrUpgradeRules.Decide(existing, incoming, profile ?? SameQuality, settings ?? TalearrRecordingsSettings.Default);
        }

        // Rule 1: same recording only.
        [Test]
        public void unabridged_download_for_abridged_files_is_kept_as_its_own_recording()
        {
            var verdict = Decide(Audio(360, abridged: AbridgedMarker.Abridged), Audio(1030, abridged: AbridgedMarker.Unabridged));

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.KeepBoth));
            Assert.That(verdict.Reason, Does.StartWith("Talearr: the files are abridged and the download is unabridged; keeping both recordings"));
        }

        [Test]
        public void with_keep_both_off_an_unabridged_download_never_replaces_abridged_files()
        {
            var verdict = Decide(Audio(360, abridged: AbridgedMarker.Abridged), Audio(1030, abridged: AbridgedMarker.Unabridged), settings: new TalearrRecordingsSettings { KeepBoth = false });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Rule, Is.EqualTo(TalearrUpgradeRules.RuleSameRecording));
            Assert.That(verdict.Reason, Does.Contain("turn on Keep both recordings"));
        }

        [Test]
        public void abridged_download_never_replaces_unabridged_files()
        {
            var verdict = Decide(Audio(1030, abridged: AbridgedMarker.Unabridged), Audio(360, abridged: AbridgedMarker.Abridged), new UpgradeProfileComparison { Quality = 1 });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.Contain("an abridged download would replace the unabridged files"));
        }

        [Test]
        public void files_far_shorter_than_the_download_are_an_abridgement()
        {
            var verdict = Decide(Audio(360), Audio(1030));

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.KeepBoth));
            Assert.That(verdict.Reason, Does.Contain("the download runs 17h 10m against 6h 00m"));
        }

        [Test]
        public void a_contradicting_narrator_is_a_different_recording()
        {
            var verdict = Decide(Audio(1030, narrators: "Scott Brick"), Audio(1030, kbps: 128, narrators: "D.W. Moffett"), new UpgradeProfileComparison { Quality = 1 });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.Contain("narrated by D.W. Moffett instead of Scott Brick"));
        }

        [Test]
        public void a_replacement_shorter_than_the_runtime_tolerance_is_rejected()
        {
            var verdict = Decide(Audio(1030), Audio(950, kbps: 128), new UpgradeProfileComparison { Quality = 1 });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.Contain("is 1h 20m shorter than the files"));
        }

        [Test]
        public void a_replacement_within_the_runtime_tolerance_is_the_same_recording()
        {
            Assert.That(Decide(Audio(1030), Audio(1010, kbps: 128), new UpgradeProfileComparison { Quality = 1 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));

            var strict = new TalearrRecordingsSettings { RuntimeTolerancePct = 1 };
            Assert.That(Decide(Audio(1030), Audio(1010, kbps: 128), new UpgradeProfileComparison { Quality = 1 }, strict).Kind, Is.EqualTo(UpgradeVerdictKind.Reject), "the tolerance is a setting");
        }

        // Rule 2: never worse on measured facts.
        [Test]
        public void fewer_channels_is_worse_even_at_a_higher_quality()
        {
            var verdict = Decide(Audio(1030, channels: 2), Audio(1030, channels: 1), new UpgradeProfileComparison { Quality = 1 });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Rule, Is.EqualTo(TalearrUpgradeRules.RuleNeverWorse));
            Assert.That(verdict.Reason, Does.Contain("1 audio channel(s) instead of 2"));
        }

        [Test]
        public void a_lower_bitrate_is_worse_even_at_a_higher_quality()
        {
            var verdict = Decide(Audio(1030, kbps: 128, codec: "mp3", files: 40, m4b: false), Audio(1030, kbps: 64), new UpgradeProfileComparison { Quality = 1 });

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Rule, Is.EqualTo(TalearrUpgradeRules.RuleNeverWorse));
            Assert.That(verdict.Reason, Is.EqualTo("Talearr: kept the existing files, the download is 64 kbps lower (64 kbps aac against 128 kbps mp3)"));
        }

        [Test]
        public void bitrate_wobble_within_the_tolerance_is_not_worse()
        {
            Assert.That(Decide(Audio(1030, kbps: 64), Audio(1030, kbps: 62), new UpgradeProfileComparison { Revision = 1 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
        }

        // Rule 3: what counts as better.
        [Test]
        public void higher_quality_revision_or_custom_format_score_is_better()
        {
            Assert.That(Decide(Audio(1030), Audio(1030), new UpgradeProfileComparison { Quality = 1 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
            Assert.That(Decide(Audio(1030), Audio(1030), new UpgradeProfileComparison { Revision = 1 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
            Assert.That(Decide(Audio(1030), Audio(1030), new UpgradeProfileComparison { CustomFormatScore = 1 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
        }

        [Test]
        public void a_bitrate_gain_in_the_same_codec_counts_from_the_minimum()
        {
            Assert.That(Decide(Audio(1030, kbps: 64), Audio(1030, kbps: 128)).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));

            var small = Decide(Audio(1030, kbps: 64), Audio(1030, kbps: 72));
            Assert.That(small.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(small.Reason, Does.Contain("only 8 kbps more (at least 16 kbps counts)"));

            Assert.That(Decide(Audio(1030, kbps: 64), Audio(1030, kbps: 72), settings: new TalearrRecordingsSettings { MinBitrateGainKbps = 8 }).Kind, Is.EqualTo(UpgradeVerdictKind.Allow), "the minimum is a setting");
        }

        [Test]
        public void a_bitrate_gain_across_codecs_does_not_count()
        {
            var verdict = Decide(Audio(1030, kbps: 64, codec: "aac"), Audio(1030, kbps: 128, codec: "mp3", m4b: false));

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.Contain("a bitrate gain only counts within the same codec (aac 64 kbps against mp3 128 kbps)"));
        }

        [Test]
        public void one_m4b_replacing_many_files_at_the_same_bitrate_is_better()
        {
            Assert.That(Decide(Audio(1030, kbps: 64, codec: "mp3", files: 40, m4b: false), Audio(1030, kbps: 64)).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
        }

        [Test]
        public void same_quality_and_bitrate_is_not_an_upgrade()
        {
            var verdict = Decide(Audio(1030, kbps: 64), Audio(1030, kbps: 64));

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.Contain("same quality and no higher bitrate"));
        }

        [Test]
        public void unknown_facts_never_block_an_upgrade()
        {
            var unknown = new RecordingAudioFacts();
            Assert.That(Decide(unknown, unknown).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
            Assert.That(Decide(Audio(1030, kbps: null, codec: null, channels: null), Audio(1030, kbps: 128)).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
            Assert.That(Decide(Audio(1030, kbps: 64, codec: null), Audio(1030, kbps: 128, codec: "mp3")).Kind, Is.EqualTo(UpgradeVerdictKind.Allow), "a gain with an unknown codec cannot be judged");
        }

        [TestCase("MPEG Audio Layer 3", "mp3")]
        [TestCase("mp3", "mp3")]
        [TestCase("AAC LC", "aac")]
        [TestCase("mp4a.40.2", "aac")]
        [TestCase("Opus", "opus")]
        [TestCase("FLAC", "flac")]
        [TestCase("Unknown", null)]
        [TestCase("", null)]
        public void codecs_are_normalized(string format, string expected)
        {
            Assert.That(TalearrUpgradeRules.NormalizeCodec(format), Is.EqualTo(expected));
        }
    }
}
