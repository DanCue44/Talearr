using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.QualityLadder;

namespace NzbDrone.Core.Talearr.Recordings
{
    // What Talearr measured about one side of a replacement: the files already in the library for a
    // recording, or the files a download would put in their place. Null means unknown.
    public sealed class RecordingAudioFacts
    {
        public int FileCount { get; set; }
        public int? DurationSeconds { get; set; }

        // Duration weighted average over the files.
        public int? BitrateKbps { get; set; }
        public int? Channels { get; set; }

        // Normalized: mp3, aac, opus, flac, vorbis, ... (see TalearrUpgradeRules.NormalizeCodec).
        public string Codec { get; set; }
        public bool SingleM4b { get; set; }
        public List<string> Narrators { get; set; } = new List<string>();
        public AbridgedMarker Abridged { get; set; } = AbridgedMarker.Unknown;
    }

    // How the replacement compares on upstream's own terms (quality profile, revision, custom format score).
    public sealed class UpgradeProfileComparison
    {
        public int Quality { get; set; }
        public int Revision { get; set; }
        public int CustomFormatScore { get; set; }

        public bool AnyHigher => Quality > 0 || Revision > 0 || CustomFormatScore > 0;
    }

    public enum UpgradeVerdictKind
    {
        // Replace the old files (upstream's normal upgrade).
        Allow,

        // Keep the old files; the import is rejected with the reason.
        Reject,

        // Keep the old files and import the download as its own (unabridged) recording.
        KeepBoth
    }

    public sealed class UpgradeVerdict
    {
        public UpgradeVerdictKind Kind { get; private set; }

        // Short rule name for logs and tests: same-recording, never-worse, better, unknown.
        public string Rule { get; private set; }
        public string Reason { get; private set; }

        public static UpgradeVerdict Allow(string rule, string reason) => new UpgradeVerdict { Kind = UpgradeVerdictKind.Allow, Rule = rule, Reason = reason };
        public static UpgradeVerdict Reject(string rule, string reason) => new UpgradeVerdict { Kind = UpgradeVerdictKind.Reject, Rule = rule, Reason = "Talearr: " + reason };
        public static UpgradeVerdict KeepBoth(string reason) => new UpgradeVerdict { Kind = UpgradeVerdictKind.KeepBoth, Rule = "keep-both", Reason = "Talearr: " + reason };
    }

    // Talearr: the recording-aware upgrade rules (docs/TALEARR_RECORDINGS_DESIGN.md, "Upgrade path").
    // Pure decisions over measured facts; TalearrUpgradeGuard does the measuring. In order:
    //   1. same recording only: same abridged status, not longer by a whole abridgement, narrator
    //      not contradicting, and not shorter than the runtime tolerance;
    //   2. never worse on measured facts: fewer channels or a lower bitrate rejects;
    //   3. better: higher in the quality profile, a higher revision or custom format score, or,
    //      at the same quality, a bitrate gain of at least the minimum within the same codec (a gain
    //      across codecs does not count), or one M4B replacing many files at the same bitrate.
    // Rules only reject on evidence: unknown facts never block an upgrade.
    public static class TalearrUpgradeRules
    {
        public const string RuleSameRecording = "same-recording";
        public const string RuleNeverWorse = "never-worse";
        public const string RuleBetter = "better";
        public const string RuleUnknown = "unknown";

        // A replacement whose average bitrate is lower by more than this is worse (VBR averages
        // wander by a few percent between encodes of the same file).
        public const double BitrateTolerance = 0.05;
        public const int BitrateToleranceFloorKbps = 4;

        // The old files are an abridgement of the new ones when they are under this share of the
        // new runtime (the same 60% the grab policy uses).
        public const double AbridgedRuntimeRatio = 0.6;

        // ladderUpgrades (talearr.ladder.audioupgrades): a gain across codecs counts when it is a clear
        // step up the audio quality ladder (TalearrAudioLadder.IsClearUpgrade).
        public static UpgradeVerdict Decide(RecordingAudioFacts existing, RecordingAudioFacts incoming, UpgradeProfileComparison profile, TalearrRecordingsSettings settings, bool ladderUpgrades = false)
        {
            existing ??= new RecordingAudioFacts();
            incoming ??= new RecordingAudioFacts();
            profile ??= new UpgradeProfileComparison();
            settings ??= TalearrRecordingsSettings.Default;

            var sameRecording = SameRecording(existing, incoming, settings);
            if (sameRecording != null)
            {
                return sameRecording;
            }

            var worse = NeverWorse(existing, incoming);
            if (worse != null)
            {
                return worse;
            }

            return Better(existing, incoming, profile, settings, ladderUpgrades);
        }

        public static string NormalizeCodec(string format)
        {
            if (string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            var value = format.Trim().ToLowerInvariant();
            if (value == "unknown")
            {
                return null;
            }

            if (value.Contains("mp3") || value.Contains("mpeg") || value.Contains("layer 3") || value.Contains("layer iii"))
            {
                return "mp3";
            }

            if (value.Contains("aac") || value.Contains("mp4a") || value.Contains("m4a") || value.Contains("m4b"))
            {
                return "aac";
            }

            if (value.Contains("opus"))
            {
                return "opus";
            }

            if (value.Contains("flac"))
            {
                return "flac";
            }

            if (value.Contains("vorbis") || value.Contains("ogg"))
            {
                return "vorbis";
            }

            if (value.Contains("alac"))
            {
                return "alac";
            }

            return value;
        }

        private static UpgradeVerdict SameRecording(RecordingAudioFacts existing, RecordingAudioFacts incoming, TalearrRecordingsSettings settings)
        {
            // Abridged status, when both sides say.
            if (existing.Abridged != AbridgedMarker.Unknown && incoming.Abridged != AbridgedMarker.Unknown && existing.Abridged != incoming.Abridged)
            {
                return existing.Abridged == AbridgedMarker.Abridged
                    ? AbridgedToUnabridged(settings, "the files are abridged and the download is unabridged")
                    : UpgradeVerdict.Reject(RuleSameRecording, "different recording, an abridged download would replace the unabridged files; kept the existing files");
            }

            var oldSeconds = existing.DurationSeconds ?? 0;
            var newSeconds = incoming.DurationSeconds ?? 0;

            // Much longer: the files in the library are an abridgement of the download.
            if (oldSeconds > 0 && newSeconds > 0 && existing.Abridged != AbridgedMarker.Unabridged && oldSeconds < newSeconds * AbridgedRuntimeRatio)
            {
                return AbridgedToUnabridged(settings, $"the download runs {Minutes(newSeconds)} against {Minutes(oldSeconds)} for the files, so the files are abridged");
            }

            var narrators = TalearrAudibleMatcher.NarratorsMatch(existing.Narrators, incoming.Narrators);
            if (narrators == false)
            {
                return UpgradeVerdict.Reject(RuleSameRecording, $"different recording, narrated by {Join(incoming.Narrators)} instead of {Join(existing.Narrators)}; kept the existing files");
            }

            var tolerance = Math.Clamp(settings.RuntimeTolerancePct, 0, TalearrRecordingsSettingsService.MaxRuntimeTolerancePct) / 100.0;
            if (oldSeconds > 0 && newSeconds > 0 && newSeconds < oldSeconds * (1 - tolerance))
            {
                return UpgradeVerdict.Reject(RuleSameRecording, $"the download is {Minutes(oldSeconds - newSeconds)} shorter than the files ({Minutes(newSeconds)} against {Minutes(oldSeconds)}), so it is a different recording or incomplete; kept the existing files");
            }

            return null;
        }

        private static UpgradeVerdict AbridgedToUnabridged(TalearrRecordingsSettings settings, string why)
        {
            return settings.KeepBoth
                ? UpgradeVerdict.KeepBoth($"{why}; keeping both recordings, the download is imported as its own unabridged recording")
                : UpgradeVerdict.Reject(RuleSameRecording, $"different recording, {why}; kept the existing files (turn on Keep both recordings to import it next to them)");
        }

        private static UpgradeVerdict NeverWorse(RecordingAudioFacts existing, RecordingAudioFacts incoming)
        {
            if (existing.Channels > 0 && incoming.Channels > 0 && incoming.Channels < existing.Channels)
            {
                return UpgradeVerdict.Reject(RuleNeverWorse, $"kept the existing files, the download has {incoming.Channels} audio channel(s) instead of {existing.Channels}");
            }

            if (existing.BitrateKbps > 0 && incoming.BitrateKbps > 0)
            {
                var allowed = Math.Max(BitrateToleranceFloorKbps, existing.BitrateKbps.Value * BitrateTolerance);
                if (incoming.BitrateKbps.Value < existing.BitrateKbps.Value - allowed)
                {
                    return UpgradeVerdict.Reject(RuleNeverWorse, $"kept the existing files, the download is {existing.BitrateKbps - incoming.BitrateKbps} kbps lower ({incoming.BitrateKbps} kbps {incoming.Codec ?? string.Empty} against {existing.BitrateKbps} kbps {existing.Codec ?? string.Empty})".Replace("  ", " ").Replace(" )", ")"));
                }
            }

            return null;
        }

        private static UpgradeVerdict Better(RecordingAudioFacts existing, RecordingAudioFacts incoming, UpgradeProfileComparison profile, TalearrRecordingsSettings settings, bool ladderUpgrades)
        {
            if (profile.AnyHigher)
            {
                return UpgradeVerdict.Allow(RuleBetter, "higher in the quality profile, revision or custom format score");
            }

            if (profile.Quality < 0 || profile.Revision < 0)
            {
                // Upstream's own checks decide lower quality and revisions; Talearr adds nothing here.
                return UpgradeVerdict.Allow(RuleUnknown, "left to the quality profile");
            }

            if (!(existing.BitrateKbps > 0) || !(incoming.BitrateKbps > 0))
            {
                return UpgradeVerdict.Allow(RuleUnknown, "bitrate unknown, nothing to judge");
            }

            var gain = incoming.BitrateKbps.Value - existing.BitrateKbps.Value;
            var sameCodec = existing.Codec != null && incoming.Codec != null && existing.Codec == incoming.Codec;
            var minimum = Math.Clamp(settings.MinBitrateGainKbps, 0, TalearrRecordingsSettingsService.MaxBitrateGainKbps);

            if (incoming.SingleM4b && existing.FileCount > 1)
            {
                return UpgradeVerdict.Allow(RuleBetter, "one chaptered M4B replaces several files at the same bitrate");
            }

            // Talearr: quality ladder, e.g. 32 kbps mp3 files replaced by 64 kbps AAC of the same recording.
            if (ladderUpgrades && existing.Codec != null && incoming.Codec != null && !sameCodec && gain > 0 &&
                TalearrAudioLadder.IsClearUpgrade(Estimate(existing), Estimate(incoming), minimum))
            {
                return UpgradeVerdict.Allow(RuleBetter, $"a clear step up the audio quality ladder ({existing.Codec} {existing.BitrateKbps} kbps to {incoming.Codec} {incoming.BitrateKbps} kbps)");
            }

            if (existing.Codec != null && incoming.Codec != null && !sameCodec && gain > 0)
            {
                return UpgradeVerdict.Reject(RuleBetter, $"not an upgrade, a bitrate gain only counts within the same codec ({existing.Codec} {existing.BitrateKbps} kbps against {incoming.Codec} {incoming.BitrateKbps} kbps); kept the existing files");
            }

            if (sameCodec && gain >= minimum && gain > 0)
            {
                return UpgradeVerdict.Allow(RuleBetter, $"{gain} kbps more in the same codec");
            }

            if (gain > 0 && !sameCodec)
            {
                // One side's codec is unknown: the gain cannot be judged.
                return UpgradeVerdict.Allow(RuleUnknown, "codec unknown, bitrate gain not judged");
            }

            return UpgradeVerdict.Reject(RuleBetter, gain > 0
                ? $"not an upgrade, only {gain} kbps more (at least {minimum} kbps counts); kept the existing files"
                : "not an upgrade, same quality and no higher bitrate; kept the existing files");
        }

        private static AudioQualityEstimate Estimate(RecordingAudioFacts facts)
        {
            return new AudioQualityEstimate { Codec = facts.Codec, Kbps = facts.BitrateKbps, Tier = TalearrAudioLadder.TierFor(facts.Codec, facts.BitrateKbps), Source = "mediainfo" };
        }

        private static string Minutes(int seconds)
        {
            var minutes = (int)Math.Round(seconds / 60.0);
            return minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes} min";
        }

        private static string Join(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            return list.Count == 0 ? "an unknown narrator" : string.Join(", ", list);
        }
    }
}
