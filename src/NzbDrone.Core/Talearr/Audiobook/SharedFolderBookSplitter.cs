using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Audiobook
{
    // TaleArr: the matcher treats all audio files of one extension in one folder as a single
    // book. When a folder holds two different books ("Book A 01.mp3 ... Book B 01.mp3") that
    // would merge them. This looks at the album tags of such a folder and returns a book key per
    // file when the tags clearly name more than one book, so each book is matched on its own.
    //
    // Conservative on purpose: every file needs an album tag; disc, part and format words are
    // ignored when comparing albums ("Dune (Disc 1)" and "Dune [Disc 2]" are one book); and a
    // folder whose albums are one per file but whose track numbers run 1..N across the folder
    // (rips that put the chapter title in the album tag) is left alone. Splitting the discs of
    // one book by mistake is harmless: both halves match the same book and import together.
    public static class SharedFolderBookSplitter
    {
        private static readonly string[] AlbumKeys = { "ALBUM", "album", "ID3v2:TALB", "MP4:©alb", "XIPH:ALBUM", "ASF:WM/AlbumTitle", "APE:Album" };

        private static readonly Regex BracketRegex = new Regex(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex PackagingRegex = new Regex(
            @"\b(?:cd|disc|disk|part|pt|tape|side|track|trk)\s*[#.]?\s*(?:\d{1,3}|[ivx]{1,5}|one|two|three|four|five|six|seven|eight|nine|ten|[ab])(?:\s*(?:of|/)\s*\d{1,3})?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NoiseWordRegex = new Regex(@"\b(?:unabridged|abridged|audiobook|audio\s*book|retail|mp3|m4a|m4b|aac|\d{2,3}\s*kbps)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NonWordRegex = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        public static IReadOnlyDictionary<string, string> GetBookKeys<T>(IEnumerable<T> files, Func<T, string> getPath, Func<T, IDictionary<string, List<string>>> getTags)
        {
            var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var list = files?.Where(f => f != null).ToList() ?? new List<T>();

            if (list.Count < 2)
            {
                return empty;
            }

            var entries = list.Select(f =>
            {
                var tags = getTags(f);
                return new
                {
                    Path = getPath(f),
                    Album = NormalizeAlbum(ReadFirst(tags, AlbumKeys)),
                    Track = AudiobookTrackOrder.ReadTrackNumber(tags)
                };
            }).ToList();

            if (entries.Any(e => string.IsNullOrWhiteSpace(e.Path) || string.IsNullOrEmpty(e.Album)))
            {
                return empty;
            }

            var groups = entries.GroupBy(e => e.Album, StringComparer.Ordinal).ToList();
            if (groups.Count < 2)
            {
                return empty;
            }

            // One album per file with a single 1..N track run: chapter titles in the album tag.
            if (groups.Count == entries.Count && entries.All(e => e.Track.HasValue))
            {
                var tracks = entries.Select(e => e.Track.Value).OrderBy(t => t).ToList();
                if (tracks.SequenceEqual(Enumerable.Range(tracks[0], tracks.Count)))
                {
                    return empty;
                }
            }

            // Many distinct albums with only one file each and no other evidence is more likely
            // inconsistent tagging than a folder of single-file books; leave those alone too.
            if (groups.Count > 2 && groups.All(g => g.Count() == 1) && entries.Count > 4)
            {
                return empty;
            }

            var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                foreach (var entry in group)
                {
                    keys[entry.Path] = group.Key;
                }
            }

            return keys;
        }

        internal static string NormalizeAlbum(string album)
        {
            if (string.IsNullOrWhiteSpace(album))
            {
                return null;
            }

            var text = BracketRegex.Replace(album, " ");
            text = PackagingRegex.Replace(text, " ");
            text = NoiseWordRegex.Replace(text, " ");
            text = NonWordRegex.Replace(text, " ").Trim().ToLowerInvariant();

            return text.Length == 0 ? null : text;
        }

        private static string ReadFirst(IDictionary<string, List<string>> tags, string[] keys)
        {
            if (tags == null)
            {
                return null;
            }

            foreach (var key in keys)
            {
                if (tags.TryGetValue(key, out var values))
                {
                    var value = values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                    if (value != null)
                    {
                        return value;
                    }
                }
            }

            return null;
        }
    }
}
