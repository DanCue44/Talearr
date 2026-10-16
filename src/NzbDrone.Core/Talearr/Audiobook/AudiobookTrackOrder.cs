using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common;

namespace NzbDrone.Core.Talearr.Audiobook
{
    // TaleArr: puts the audio files of one audiobook in playing order.
    //
    // 1. When every file carries a track number tag and the (disc, track) pairs are unique, the
    //    tags decide. The disc comes from the disc tag, or from a disc folder (CD1, Disc 2,
    //    Part 03) when the tag is missing, so per-disc track numbering still works.
    // 2. Otherwise the path decides: disc folders by their number (so "Disc Two" sorts before
    //    "Disc Three" and "Part X" after "Part IX"), then a natural sort of the rest of the path
    //    ("2" before "10").
    //
    // Only folders below the files' common folder count, so a book folder that happens to be
    // called "... Part Two" does not affect the order.
    public static class AudiobookTrackOrder
    {
        private static readonly Regex LeadingNumberRegex = new Regex(@"^\s*(?<n>\d{1,5})", RegexOptions.Compiled);

        private static readonly string[] TrackKeys = { "TRACKNUMBER", "track", "ID3v2:TRCK", "XIPH:TRACKNUMBER", "APE:Track", "ASF:WM/TrackNumber" };
        private static readonly string[] DiscKeys = { "DISCNUMBER", "disc", "ID3v2:TPOS", "XIPH:DISCNUMBER", "APE:Disc", "ASF:WM/PartOfSet" };

        public static List<T> Order<T>(IEnumerable<T> items, Func<T, string> getPath, Func<T, IDictionary<string, List<string>>> getTags = null)
        {
            var list = items?.Where(item => item != null).ToList() ?? new List<T>();
            if (list.Count < 2)
            {
                return list;
            }

            var root = GetCommonFolder(list.Select(item => getPath(item) ?? string.Empty).ToList());

            var entries = list.Select((item, index) =>
            {
                var path = getPath(item) ?? string.Empty;
                var relative = GetRelativePath(root, path);
                var tags = getTags?.Invoke(item);

                return new Entry<T>
                {
                    Item = item,
                    Index = index,
                    RelativePath = relative,
                    FolderDisc = GetFolderDisc(relative),
                    TagDisc = ReadNumber(tags, DiscKeys),
                    TagTrack = ReadNumber(tags, TrackKeys)
                };
            }).ToList();

            if (CanOrderByTags(entries))
            {
                return entries
                    .OrderBy(e => e.TagDisc ?? e.FolderDisc ?? 0)
                    .ThenBy(e => e.TagTrack.Value)
                    .ThenBy(e => e.RelativePath, NaturalSortComparer.Instance)
                    .Select(e => e.Item)
                    .ToList();
            }

            return entries
                .OrderBy(e => e.RelativePath, new DiscAwarePathComparer())
                .ThenBy(e => e.Index)
                .Select(e => e.Item)
                .ToList();
        }

        public static int? ReadTrackNumber(IDictionary<string, List<string>> tags)
        {
            return ReadNumber(tags, TrackKeys);
        }

        public static int? ReadDiscNumber(IDictionary<string, List<string>> tags)
        {
            return ReadNumber(tags, DiscKeys);
        }

        private static bool CanOrderByTags<T>(List<Entry<T>> entries)
        {
            if (entries.Any(e => !e.TagTrack.HasValue || e.TagTrack.Value <= 0))
            {
                return false;
            }

            var keys = entries.Select(e => (Disc: e.TagDisc ?? e.FolderDisc ?? 0, Track: e.TagTrack.Value)).ToList();
            return keys.Distinct().Count() == keys.Count;
        }

        private static int? GetFolderDisc(string relativePath)
        {
            var folders = relativePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // The last segment is the file name.
            for (var i = folders.Length - 2; i >= 0; i--)
            {
                if (AudiobookDiscFolder.TryGetDiscNumber(folders[i], out var disc))
                {
                    return disc;
                }
            }

            return null;
        }

        private static int? ReadNumber(IDictionary<string, List<string>> tags, string[] keys)
        {
            if (tags == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (!tags.TryGetValue(key, out var values) || values == null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    var match = LeadingNumberRegex.Match(value ?? string.Empty);
                    if (match.Success && int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                    {
                        return number;
                    }
                }
            }

            return null;
        }

        public static string GetCommonFolder(IReadOnlyList<string> paths)
        {
            var folders = paths
                .Select(p => (Path.GetDirectoryName(p) ?? string.Empty).Replace('\\', '/').TrimEnd('/'))
                .ToList();

            if (folders.Count == 0)
            {
                return string.Empty;
            }

            var common = folders[0].Split('/');
            var length = common.Length;

            foreach (var folder in folders.Skip(1))
            {
                var parts = folder.Split('/');
                var i = 0;
                while (i < length && i < parts.Length && string.Equals(parts[i], common[i], StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }

                length = i;
            }

            return string.Join("/", common.Take(length));
        }

        private static string GetRelativePath(string root, string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            if (root.Length > 0 && normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(root.Length + 1);
            }

            return normalized;
        }

        private sealed class Entry<T>
        {
            public T Item { get; set; }
            public int Index { get; set; }
            public string RelativePath { get; set; }
            public int? FolderDisc { get; set; }
            public int? TagDisc { get; set; }
            public int? TagTrack { get; set; }
        }

        // Compares relative paths segment by segment. Two disc folders compare by disc number;
        // files directly in the common folder sort before disc folders; everything else uses
        // the natural sort.
        private sealed class DiscAwarePathComparer : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                var a = (x ?? string.Empty).Split('/');
                var b = (y ?? string.Empty).Split('/');
                var count = Math.Min(a.Length, b.Length);

                for (var i = 0; i < count; i++)
                {
                    var aIsFile = i == a.Length - 1;
                    var bIsFile = i == b.Length - 1;

                    if (aIsFile != bIsFile)
                    {
                        // A loose file next to disc folders (an intro, a credits track) goes first.
                        return aIsFile ? -1 : 1;
                    }

                    if (!aIsFile &&
                        AudiobookDiscFolder.TryGetDiscNumber(a[i], out var discA) &&
                        AudiobookDiscFolder.TryGetDiscNumber(b[i], out var discB) &&
                        discA != discB)
                    {
                        return discA.CompareTo(discB);
                    }

                    var result = NaturalSortComparer.Instance.Compare(a[i], b[i]);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return a.Length.CompareTo(b.Length);
            }
        }
    }
}
