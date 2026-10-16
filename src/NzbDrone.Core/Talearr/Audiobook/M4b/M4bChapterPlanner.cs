using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // TaleArr: chapters for a merged M4B.
    //
    // A source file that already has chapters keeps them (shifted to its place in the book).
    // Every other file becomes one chapter. Its title is the file's title tag when the files
    // have distinct titles, otherwise the file name without the track number and without the
    // text all file names share ("101 - Walter Isaacson - Steve Jobs - 2011 - Apple is Born"
    // becomes "Apple is Born"). Names that are still not unique get their disc folder in front
    // ("Part 3 - Chapter 1-2"); if nothing distinguishes the files they are "Chapter N".
    public static class M4bChapterPlanner
    {
        private static readonly Regex LeadingTrackRegex = new Regex(@"^\s*(?:(?:track|trk)\s*)?\d{1,4}(?:\s*(?:of|/)\s*\d{1,4})?(?:\s*[-._)\]:]+\s*|\s+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex OnlyNumberRegex = new Regex(@"^\s*\d+\s*$", RegexOptions.Compiled);

        public static List<M4bChapter> Plan(IReadOnlyList<M4bSourceTrack> tracks)
        {
            var chapters = new List<M4bChapter>();
            if (tracks == null || tracks.Count == 0)
            {
                return chapters;
            }

            var fileTitles = BuildFileTitles(tracks);
            var offset = 0.0;
            var counter = 0;

            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                var duration = Math.Max(0, track.DurationSeconds);
                var embedded = (track.Chapters ?? new List<M4bChapter>())
                    .Where(c => c != null && c.End > c.Start && c.Start < duration + 0.5)
                    .OrderBy(c => c.Start)
                    .ToList();

                if (embedded.Count > 1)
                {
                    foreach (var chapter in embedded)
                    {
                        counter++;
                        var start = offset + Math.Max(0, chapter.Start);
                        var end = offset + Math.Min(duration, chapter.End);
                        if (end <= start)
                        {
                            continue;
                        }

                        var title = string.IsNullOrWhiteSpace(chapter.Title) || OnlyNumberRegex.IsMatch(chapter.Title)
                            ? DefaultTitle(counter)
                            : chapter.Title.Trim();

                        chapters.Add(new M4bChapter(start, end, title));
                    }
                }
                else
                {
                    counter++;
                    chapters.Add(new M4bChapter(offset, offset + duration, fileTitles[i] ?? DefaultTitle(counter)));
                }

                offset += duration;
            }

            // Close tiny gaps from rounding so players do not show a gap chapter.
            for (var i = 0; i < chapters.Count - 1; i++)
            {
                chapters[i].End = chapters[i + 1].Start;
            }

            return chapters;
        }

        private static string DefaultTitle(int number)
        {
            return string.Format(CultureInfo.InvariantCulture, "Chapter {0}", number);
        }

        private static List<string> BuildFileTitles(IReadOnlyList<M4bSourceTrack> tracks)
        {
            var tagTitles = tracks.Select(t => Clean(t.Title)).ToList();
            if (tagTitles.All(t => !string.IsNullOrEmpty(t)) &&
                tagTitles.Distinct(StringComparer.OrdinalIgnoreCase).Count() == tagTitles.Count)
            {
                return tagTitles;
            }

            var names = tracks
                .Select(t => LeadingTrackRegex.Replace(Path.GetFileNameWithoutExtension(t.Path ?? string.Empty), string.Empty).Trim())
                .ToList();

            names = StripCommonAffixes(names);

            if (names.Any(string.IsNullOrWhiteSpace) || names.All(n => OnlyNumberRegex.IsMatch(n)))
            {
                return tracks.Select(_ => (string)null).ToList();
            }

            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            {
                var withDisc = tracks.Select((t, i) =>
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(t.Path ?? string.Empty) ?? string.Empty);
                    return AudiobookDiscFolder.IsDiscFolder(folder) ? folder.Trim() + " - " + names[i] : names[i];
                }).ToList();

                if (withDisc.Distinct(StringComparer.OrdinalIgnoreCase).Count() != withDisc.Count)
                {
                    return tracks.Select(_ => (string)null).ToList();
                }

                names = withDisc;
            }

            return names;
        }

        // Removes a prefix and suffix shared by every name, cut at a word separator.
        internal static List<string> StripCommonAffixes(List<string> names)
        {
            if (names.Count < 2)
            {
                return names;
            }

            var prefix = CommonPrefix(names);
            var cut = LastSeparatorEnd(prefix);
            if (cut > 0 && names.All(n => n.Length > cut))
            {
                names = names.Select(n => n.Substring(cut).Trim()).ToList();
            }

            var reversed = names.Select(n => new string(n.Reverse().ToArray())).ToList();
            var suffix = CommonPrefix(reversed);
            var suffixCut = LastSeparatorEnd(suffix);
            if (suffixCut > 0 && names.All(n => n.Length > suffixCut))
            {
                names = names.Select(n => n.Substring(0, n.Length - suffixCut).Trim()).ToList();
            }

            return names.Select(n => n.Trim(' ', '-', '_', '.')).ToList();
        }

        private static string CommonPrefix(List<string> values)
        {
            var first = values[0];
            var length = first.Length;

            foreach (var value in values.Skip(1))
            {
                length = Math.Min(length, value.Length);
                for (var i = 0; i < length; i++)
                {
                    if (char.ToLowerInvariant(first[i]) != char.ToLowerInvariant(value[i]))
                    {
                        length = i;
                        break;
                    }
                }
            }

            return first.Substring(0, length);
        }

        // Length up to and including the last " - " / "_" / ". " style separator in the text, so
        // a shared prefix is only removed at a word boundary.
        private static int LastSeparatorEnd(string text)
        {
            var best = -1;
            foreach (var separator in new[] { " - ", " _ ", "_-_", " . ", "_", " -", "- " })
            {
                var index = text.LastIndexOf(separator, StringComparison.Ordinal);
                if (index >= 0)
                {
                    best = Math.Max(best, index + separator.Length);
                }
            }

            return best;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var text = value.Trim();
            return OnlyNumberRegex.IsMatch(text) ? null : text;
        }
    }
}
