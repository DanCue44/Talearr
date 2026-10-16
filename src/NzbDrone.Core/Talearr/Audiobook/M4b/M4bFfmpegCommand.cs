using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // TaleArr: builds the ffmpeg inputs for a merge: the concat list, the FFMETADATA1 file with
    // tags and chapters, and the argument list. Pure functions so they can be unit tested.
    public static class M4bFfmpegCommand
    {
        public const int MaxDescriptionLength = 4000;

        // ffmpeg concat demuxer list. Paths are single quoted; a quote inside a path is written
        // as '\'' (close, escaped quote, reopen).
        public static string BuildConcatList(IEnumerable<string> paths)
        {
            var builder = new StringBuilder();
            builder.Append("ffconcat version 1.0\n");

            foreach (var path in paths)
            {
                builder.Append("file '").Append((path ?? string.Empty).Replace("'", "'\\''")).Append("'\n");
            }

            return builder.ToString();
        }

        public static string BuildFfMetadata(M4bMetadata metadata, IReadOnlyList<M4bChapter> chapters)
        {
            var builder = new StringBuilder();
            builder.Append(";FFMETADATA1\n");

            foreach (var pair in GetTags(metadata))
            {
                builder.Append(pair.Key).Append('=').Append(Escape(pair.Value)).Append('\n');
            }

            foreach (var chapter in chapters ?? Array.Empty<M4bChapter>())
            {
                var start = (long)Math.Round(chapter.Start * 1000, MidpointRounding.AwayFromZero);
                var end = (long)Math.Round(chapter.End * 1000, MidpointRounding.AwayFromZero);
                if (end <= start)
                {
                    continue;
                }

                builder.Append("[CHAPTER]\n");
                builder.Append("TIMEBASE=1/1000\n");
                builder.Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
                builder.Append("title=").Append(Escape(chapter.Title)).Append('\n');
            }

            return builder.ToString();
        }

        // Tags the MP4 muxer writes as standard atoms. Narrator goes in composer (©wrt), which
        // is where Audiobookshelf and most audiobook players read it from.
        public static IEnumerable<KeyValuePair<string, string>> GetTags(M4bMetadata metadata)
        {
            if (metadata == null)
            {
                yield break;
            }

            var description = Truncate(metadata.Description, MaxDescriptionLength);
            var grouping = metadata.Series;
            if (!string.IsNullOrWhiteSpace(grouping) && !string.IsNullOrWhiteSpace(metadata.SeriesPosition))
            {
                grouping = grouping.Trim() + " #" + metadata.SeriesPosition.Trim();
            }

            var tags = new List<KeyValuePair<string, string>>
            {
                new ("title", metadata.Title),
                new ("album", metadata.Title),
                new ("artist", metadata.Author),
                new ("album_artist", metadata.Author),
                new ("composer", metadata.Narrator),
                new ("date", metadata.Year),
                new ("genre", metadata.Genre),
                new ("comment", description),
                new ("description", description),
                new ("copyright", metadata.Publisher),
                new ("grouping", grouping),
                new ("media_type", "2")
            };

            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t.Value)))
            {
                yield return new KeyValuePair<string, string>(tag.Key, tag.Value.Trim());
            }
        }

        // FFMETADATA1 escaping: '=', ';', '#', '\' and newlines are backslash escaped.
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            foreach (var c in value.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                if (c == '=' || c == ';' || c == '#' || c == '\\' || c == '\n')
                {
                    builder.Append('\\');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        // True when every source is AAC with the same sample rate and channel count, so the
        // audio can be copied into the M4B without re-encoding.
        public static bool CanStreamCopy(IReadOnlyList<M4bSourceTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return false;
            }

            var first = tracks[0];
            return tracks.All(t => string.Equals(t.Codec, "aac", StringComparison.OrdinalIgnoreCase) &&
                                   t.SampleRate == first.SampleRate &&
                                   t.Channels == first.Channels &&
                                   t.SampleRate > 0);
        }

        public static int GetTargetBitrateKbps(IReadOnlyList<M4bSourceTrack> tracks, M4bEncodeSettings settings)
        {
            settings ??= new M4bEncodeSettings();
            var target = settings.BitrateKbps > 0 ? settings.BitrateKbps : 64;

            if (settings.NoUpscale)
            {
                var sourceKbps = (tracks ?? Array.Empty<M4bSourceTrack>())
                    .Where(t => t.BitRate > 0)
                    .Select(t => (int)Math.Round(t.BitRate / 1000.0))
                    .DefaultIfEmpty(0)
                    .Max();

                if (sourceKbps > 0 && sourceKbps < target)
                {
                    target = Math.Max(24, sourceKbps);
                }
            }

            return target;
        }

        public static List<string> BuildArguments(
            string concatListPath,
            string metadataPath,
            string coverPath,
            string outputPath,
            bool streamCopy,
            int bitrateKbps,
            bool mono)
        {
            var args = new List<string>
            {
                "-hide_banner",
                "-nostdin",
                "-y",
                "-loglevel", "error",
                "-progress", "pipe:1",
                "-nostats",
                "-f", "concat",
                "-safe", "0",
                "-i", concatListPath,
                "-i", metadataPath
            };

            var hasCover = !string.IsNullOrWhiteSpace(coverPath);
            if (hasCover)
            {
                args.AddRange(new[] { "-i", coverPath });
            }

            args.AddRange(new[]
            {
                "-map", "0:a:0",
                "-map_metadata", "1",
                "-map_chapters", "1"
            });

            if (hasCover)
            {
                args.AddRange(new[] { "-map", "2:v:0", "-c:v", "copy", "-disposition:v:0", "attached_pic" });
            }

            if (streamCopy)
            {
                args.AddRange(new[] { "-c:a", "copy" });
            }
            else
            {
                args.AddRange(new[] { "-c:a", "aac", "-b:a", bitrateKbps.ToString(CultureInfo.InvariantCulture) + "k" });
                if (mono)
                {
                    args.AddRange(new[] { "-ac", "1" });
                }
            }

            args.AddRange(new[]
            {
                "-movflags", "+faststart",
                "-f", "mp4",
                outputPath
            });

            return args;
        }

        // The merged file must be within 1% of the summed source durations.
        public static bool IsDurationWithinTolerance(double expectedSeconds, double actualSeconds, double tolerance = 0.01)
        {
            if (expectedSeconds <= 0 || actualSeconds <= 0)
            {
                return false;
            }

            return Math.Abs(actualSeconds - expectedSeconds) <= expectedSeconds * tolerance;
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= length)
            {
                return value;
            }

            return value.Substring(0, length).TrimEnd() + "...";
        }
    }
}
