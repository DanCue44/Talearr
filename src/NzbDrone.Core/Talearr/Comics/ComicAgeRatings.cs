using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Talearr.Comics
{
    // Talearr: the ComicInfo.xml AgeRating values (Anansi schema) that Kavita and Komga read, in
    // order from youngest to oldest. Readers limit each user to a maximum rating, so the value
    // written into each file is what keeps a child's account to suitable books.
    public static class ComicAgeRatings
    {
        public const string Unknown = "Unknown";
        public const string EarlyChildhood = "Early Childhood";
        public const string Everyone = "Everyone";
        public const string Everyone10 = "Everyone 10+";
        public const string Teen = "Teen";
        public const string Mature17 = "Mature 17+";
        public const string AdultsOnly = "Adults Only 18+";

        public static readonly IReadOnlyList<string> Ordered = new[]
        {
            EarlyChildhood,
            Everyone,
            Everyone10,
            Teen,
            Mature17,
            AdultsOnly
        };

        public static bool IsKnown(string rating)
        {
            return Ordered.Contains(rating, StringComparer.OrdinalIgnoreCase);
        }

        public static string Normalize(string rating)
        {
            if (string.IsNullOrWhiteSpace(rating))
            {
                return Unknown;
            }

            var match = Ordered.FirstOrDefault(r => string.Equals(r, rating.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? Unknown;
        }

        // Higher is older. Unknown ranks above everything, so an unrated series never passes a
        // children's limit by accident.
        public static int Rank(string rating)
        {
            var index = Ordered.ToList().FindIndex(r => string.Equals(r, rating, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? Ordered.Count : index;
        }

        public static bool IsAllowedFor(string rating, string maxRating)
        {
            if (string.IsNullOrWhiteSpace(maxRating) || !IsKnown(maxRating))
            {
                return true;
            }

            return Rank(rating) <= Rank(maxRating);
        }

        // Best guess from AniList facts. AniList has no age rating field: the adult flag, the
        // demographic tag (Kids, Shounen, Seinen...) and content tags are what it offers. Users
        // can override the result per series.
        public static string FromAniList(bool isAdult, IEnumerable<string> genres, IEnumerable<(string Name, int Rank, bool IsAdult)> tags)
        {
            var genreSet = new HashSet<string>(genres ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var tagList = (tags ?? Enumerable.Empty<(string Name, int Rank, bool IsAdult)>()).ToList();
            bool HasTag(string name, int minRank = 0) => tagList.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Rank >= minRank);

            if (isAdult || genreSet.Contains("Hentai") || tagList.Any(t => t.IsAdult && t.Rank >= 50))
            {
                return AdultsOnly;
            }

            if (genreSet.Contains("Ecchi") || HasTag("Nudity", 50) || HasTag("Gore", 60) || HasTag("Sexual Content", 40))
            {
                return Mature17;
            }

            if (HasTag("Seinen") || HasTag("Josei"))
            {
                return Mature17;
            }

            if (HasTag("Kids"))
            {
                return Everyone;
            }

            if (HasTag("Shounen") || HasTag("Shoujo"))
            {
                return Teen;
            }

            return Unknown;
        }
    }
}
