using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    // TaleArr: the ebook format preference (EPUB > AZW3 > MOBI > PDF by default). It only breaks
    // ties the quality profile leaves (formats grouped together, or the same format), so a profile
    // that orders the formats itself keeps the last word and preferences can never fight it.
    public static class TalearrEbookPreference
    {
        public static readonly IReadOnlyList<string> DefaultOrder = new[] { "EPUB", "AZW3", "MOBI", "PDF" };

        private static readonly Quality[] EbookQualities = { Quality.EPUB, Quality.AZW3, Quality.MOBI, Quality.PDF };

        public static bool IsEbook(Quality quality)
        {
            return quality != null && EbookQualities.Any(q => q == quality);
        }

        // Known format names in the given order, duplicates and unknown names dropped. An empty
        // or unusable list gives the default order.
        public static List<string> Normalize(IEnumerable<string> formats)
        {
            var known = new HashSet<string>(DefaultOrder, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var raw in formats ?? Enumerable.Empty<string>())
            {
                var name = raw?.Trim().ToUpperInvariant();
                if (!string.IsNullOrEmpty(name) && known.Contains(name) && !result.Contains(name))
                {
                    result.Add(name);
                }
            }

            return result.Count == 0 ? DefaultOrder.ToList() : result;
        }

        public static List<string> Parse(string value)
        {
            return Normalize((value ?? string.Empty).Split(new[] { ',', ';', '>', ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        // 0 is the most preferred; formats left out of the order rank after every listed one.
        public static int Rank(Quality quality, IReadOnlyList<string> order)
        {
            if (!IsEbook(quality))
            {
                return int.MaxValue;
            }

            var list = order ?? DefaultOrder;
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], quality.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return list.Count;
        }

        // True when the candidate's format is strictly earlier in the order than every file's.
        public static bool IsPreferred(Quality candidate, IEnumerable<Quality> existing, IReadOnlyList<string> order)
        {
            var files = (existing ?? Enumerable.Empty<Quality>()).ToList();
            if (!IsEbook(candidate) || files.Count == 0 || files.Any(q => !IsEbook(q)))
            {
                return false;
            }

            var rank = Rank(candidate, order);
            return files.All(q => rank < Rank(q, order));
        }
    }
}
