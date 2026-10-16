using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // TaleArr: reads "ffprobe -print_format json -show_format -show_streams -show_chapters".
    public static class M4bProbeParser
    {
        public static M4bSourceTrack Parse(string path, string json)
        {
            var track = new M4bSourceTrack { Path = path };

            if (string.IsNullOrWhiteSpace(json))
            {
                return track;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("format", out var format))
            {
                track.DurationSeconds = ReadDouble(format, "duration");
                track.BitRate = (long)ReadDouble(format, "bit_rate");
                ReadTags(format, track.Tags);
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = ReadString(stream, "codec_type");

                    if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase) && track.Codec == null)
                    {
                        track.Codec = ReadString(stream, "codec_name");
                        track.SampleRate = (int)ReadDouble(stream, "sample_rate");
                        track.Channels = (int)ReadDouble(stream, "channels");

                        var streamBitRate = (long)ReadDouble(stream, "bit_rate");
                        if (streamBitRate > 0)
                        {
                            track.BitRate = streamBitRate;
                        }

                        if (track.DurationSeconds <= 0)
                        {
                            track.DurationSeconds = ReadDouble(stream, "duration");
                        }
                    }
                    else if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        track.HasEmbeddedCover = true;
                    }
                }
            }

            if (root.TryGetProperty("chapters", out var chapters) && chapters.ValueKind == JsonValueKind.Array)
            {
                foreach (var chapter in chapters.EnumerateArray())
                {
                    var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ReadTags(chapter, tags);
                    tags.TryGetValue("title", out var title);

                    track.Chapters.Add(new M4bChapter(ReadDouble(chapter, "start_time"), ReadDouble(chapter, "end_time"), title));
                }
            }

            track.Tags.TryGetValue("title", out var fileTitle);
            track.Title = fileTitle;

            return track;
        }

        private static void ReadTags(JsonElement element, Dictionary<string, string> tags)
        {
            if (!element.TryGetProperty("tags", out var tagElement) || tagElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in tagElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    tags[property.Name] = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    tags[property.Name] = property.Value.ToString();
                }
            }
        }

        private static string ReadString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        private static double ReadDouble(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                return 0;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                _ => null
            };

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
        }
    }
}
