using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    // Ordered worst to best. Unknown sits with Low: a missing measurement is never a penalty and
    // never a reward.
    public enum AudioQualityTier
    {
        // Size per hour too small for real audio (a sample, a stub or a mislabelled release).
        Implausible = 0,

        // Under 32 kbps: speech is audibly damaged.
        VeryLow = 1,
        Unknown = 2,
        Low = 3,
        Good = 4
    }

    // What TaleArr estimated about one audiobook copy: a release (size per minute of the known
    // runtime, or a bitrate in the title) or files in the library (measured bitrate, or size per
    // minute of their duration).
    public sealed class AudioQualityEstimate
    {
        public static readonly AudioQualityEstimate None = new AudioQualityEstimate { Tier = AudioQualityTier.Unknown, Source = "none" };

        // Normalized like the upgrade guard (mp3, aac, flac, opus, ...), null when unknown.
        public string Codec { get; set; }
        public int? Kbps { get; set; }
        public AudioQualityTier Tier { get; set; }

        // title | size | mediainfo | none
        public string Source { get; set; }

        public string Describe()
        {
            if (Kbps == null)
            {
                return Tier == AudioQualityTier.Implausible ? "implausibly small" : "bitrate unknown";
            }

            return string.Format(CultureInfo.InvariantCulture, "{0}{1} kbps{2}", Codec != null ? Codec + " " : string.Empty, Kbps, Source == "size" ? " (from size)" : string.Empty);
        }
    }

    // TaleArr: the audio quality ladder (docs/TALEARR_FEATURE_RESEARCH.md item 12). Pure rules:
    //   - AAC/Opus at 64 kbps or more, MP3 at 128 kbps or more and lossless audio are Good;
    //   - below that down to 32 kbps is Low; under 32 kbps is VeryLow;
    //   - under 8 kbps per hour of runtime, or a whole release under 8 MB, is Implausible.
    // A release's bitrate is a "64kbps" style token in its title, else its size per minute of the
    // book's known runtime. Ranking only reads the tier, so two Good copies stay tied here and the
    // later comparers (protocol, indexer, size) decide as before.
    public static class TalearrAudioLadder
    {
        public const int VeryLowBelowKbps = 32;
        public const int ImplausibleBelowKbps = 8;
        public const long ImplausibleReleaseBytes = 8L * 1024 * 1024;

        // A "clearly better" copy gains at least this much on top of moving up a tier.
        public const double ClearGainRatio = 1.25;

        private static readonly Regex TitleBitrate = new Regex(@"(?<![\d.])(?<kbps>\d{2,3})\s?(?:kbps|kbit/?s|kb/s|kbit|k(?=\b|[\])]))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static AudioQualityTier TierFor(string codec, int? kbps)
        {
            if (kbps == null || kbps <= 0)
            {
                return AudioQualityTier.Unknown;
            }

            if (kbps < ImplausibleBelowKbps)
            {
                return AudioQualityTier.Implausible;
            }

            if (kbps < VeryLowBelowKbps)
            {
                return AudioQualityTier.VeryLow;
            }

            return kbps >= GoodFromKbps(codec) ? AudioQualityTier.Good : AudioQualityTier.Low;
        }

        // Where Good starts for a codec. Unknown codecs sit between AAC and MP3.
        public static int GoodFromKbps(string codec)
        {
            switch (codec)
            {
                case "aac":
                case "opus":
                case "vorbis":
                    return 64;
                case "mp3":
                    return 128;
                case "flac":
                case "alac":
                    return VeryLowBelowKbps;
                default:
                    return 96;
            }
        }

        // Codec a release's quality implies; UnknownAudio falls back to title words.
        public static string CodecFor(Quality quality, string title = null)
        {
            if (quality == Quality.M4B)
            {
                return "aac";
            }

            if (quality == Quality.MP3)
            {
                return "mp3";
            }

            if (quality == Quality.FLAC)
            {
                return "flac";
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var text = title.ToLowerInvariant();
            if (Regex.IsMatch(text, @"\b(m4b|m4a|aac)\b"))
            {
                return "aac";
            }

            if (Regex.IsMatch(text, @"\bopus\b"))
            {
                return "opus";
            }

            if (Regex.IsMatch(text, @"\bmp3\b"))
            {
                return "mp3";
            }

            return Regex.IsMatch(text, @"\bflac\b") ? "flac" : null;
        }

        public static int? ParseTitleKbps(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            foreach (Match match in TitleBitrate.Matches(title))
            {
                if (int.TryParse(match.Groups["kbps"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kbps) && kbps >= 16 && kbps <= 512)
                {
                    return kbps;
                }
            }

            return null;
        }

        public static int? KbpsFromSize(long sizeBytes, double runtimeSeconds)
        {
            if (sizeBytes <= 0 || runtimeSeconds < 60)
            {
                return null;
            }

            return (int)Math.Round(sizeBytes * 8.0 / runtimeSeconds / 1000.0);
        }

        public static AudioQualityEstimate EstimateRelease(Quality quality, string title, long sizeBytes, int? runtimeSeconds)
        {
            var codec = CodecFor(quality, title);
            var titleKbps = ParseTitleKbps(title);
            var sizeKbps = runtimeSeconds > 0 ? KbpsFromSize(sizeBytes, runtimeSeconds.Value) : null;

            // A size that cannot hold the runtime at all beats any title claim.
            if (sizeKbps != null && sizeKbps < ImplausibleBelowKbps)
            {
                return new AudioQualityEstimate { Codec = codec, Kbps = sizeKbps, Tier = AudioQualityTier.Implausible, Source = "size" };
            }

            if (titleKbps != null)
            {
                return new AudioQualityEstimate { Codec = codec, Kbps = titleKbps, Tier = TierFor(codec, titleKbps), Source = "title" };
            }

            if (sizeKbps != null)
            {
                return new AudioQualityEstimate { Codec = codec, Kbps = sizeKbps, Tier = TierFor(codec, sizeKbps), Source = "size" };
            }

            if (sizeBytes > 0 && sizeBytes < ImplausibleReleaseBytes)
            {
                return new AudioQualityEstimate { Codec = codec, Tier = AudioQualityTier.Implausible, Source = "size" };
            }

            return new AudioQualityEstimate { Codec = codec, Tier = AudioQualityTier.Unknown, Source = "none" };
        }

        // The library files of one recording: their measured bitrate (duration weighted) when every
        // file has one, else total size per total duration.
        public static AudioQualityEstimate EstimateFiles(IReadOnlyCollection<BookFile> files)
        {
            var audio = (files ?? Array.Empty<BookFile>()).Where(f => f != null).ToList();
            if (audio.Count == 0)
            {
                return AudioQualityEstimate.None;
            }

            var codecs = audio.Select(f => TalearrUpgradeRules.NormalizeCodec(f.MediaInfo?.AudioFormat) ?? CodecFor(f.Quality?.Quality, f.Path != null ? Path.GetExtension(f.Path).TrimStart('.') : null))
                .Distinct()
                .ToList();
            var codec = codecs.Count == 1 ? codecs[0] : null;

            var seconds = audio.Select(Seconds).ToList();
            if (audio.All(f => f.MediaInfo?.AudioBitrate > 0))
            {
                var weight = seconds.Sum(s => (double)Math.Max(1, s));
                var kbps = (int)Math.Round(audio.Select((f, i) => NormalizeBitrate(f.MediaInfo.AudioBitrate) * (double)Math.Max(1, seconds[i])).Sum() / weight);
                return new AudioQualityEstimate { Codec = codec, Kbps = kbps, Tier = TierFor(codec, kbps), Source = "mediainfo" };
            }

            if (seconds.All(s => s > 0) && audio.All(f => f.Size > 0))
            {
                var kbps = KbpsFromSize(audio.Sum(f => f.Size), seconds.Sum());
                return new AudioQualityEstimate { Codec = codec, Kbps = kbps, Tier = TierFor(codec, kbps), Source = "size" };
            }

            return new AudioQualityEstimate { Codec = codec, Tier = AudioQualityTier.Unknown, Source = "none" };
        }

        // Ladder upgrade: the files are below Good, the candidate is a higher tier and at least a
        // quarter more bitrate and the minimum gain on top. Unknown on either side is never clear.
        public static bool IsClearUpgrade(AudioQualityEstimate existing, AudioQualityEstimate candidate, int minGainKbps)
        {
            if (existing?.Kbps == null || candidate?.Kbps == null)
            {
                return false;
            }

            if (existing.Tier >= AudioQualityTier.Good || candidate.Tier <= existing.Tier || candidate.Tier < AudioQualityTier.Low)
            {
                return false;
            }

            var gain = candidate.Kbps.Value - existing.Kbps.Value;
            return gain >= Math.Max(1, minGainKbps) && candidate.Kbps.Value >= existing.Kbps.Value * ClearGainRatio;
        }

        private static int Seconds(BookFile file)
        {
            if (file.DurationSeconds > 0)
            {
                return file.DurationSeconds.Value;
            }

            return file.MediaInfo?.Duration.TotalSeconds > 0 ? (int)file.MediaInfo.Duration.TotalSeconds : 0;
        }

        // Some extractors report bits per second rather than kbps.
        private static int NormalizeBitrate(int value)
        {
            return value > 10000 ? (int)Math.Round(value / 1000.0) : value;
        }
    }
}
