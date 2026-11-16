using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr #10: Audible and Hardcover do not always spell an author's name the same way.
    // Audible lists "Dietmar Wehr" where Hardcover has "Dietmar Arthur Wehr", so an exact name
    // match finds neither the author's Audible books nor the Hardcover author for an Audible
    // result. Names are compared after TalearrSourceGapFill.Normalize.
    public static class TalearrAuthorNameMatch
    {
        // The same first and last name, where one name only adds middle names or initials:
        // "dietmar wehr" and "dietmar arthur wehr", "j r r tolkien" and "john ronald reuel tolkien"
        // are not (different first tokens), "john a smith" and "john b smith" are not.
        public static bool SameAuthor(string normalizedA, string normalizedB)
        {
            if (string.IsNullOrEmpty(normalizedA) || string.IsNullOrEmpty(normalizedB))
            {
                return false;
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.Ordinal))
            {
                return true;
            }

            var a = Tokens(normalizedA);
            var b = Tokens(normalizedB);
            if (a.Length < 2 || b.Length < 2)
            {
                return false;
            }

            // Same length: only initials may differ ("dietmar a wehr", "dietmar arthur wehr").
            if (a.Length == b.Length)
            {
                return string.Equals(a[0], b[0], StringComparison.Ordinal) &&
                       string.Equals(a[^1], b[^1], StringComparison.Ordinal) &&
                       a.Zip(b).All(pair => TokenMatches(pair.First, pair.Second));
            }

            var shorter = a.Length < b.Length ? a : b;
            var longer = a.Length < b.Length ? b : a;

            if (!string.Equals(shorter[0], longer[0], StringComparison.Ordinal) ||
                !string.Equals(shorter[^1], longer[^1], StringComparison.Ordinal))
            {
                return false;
            }

            // The shorter name's middle tokens appear in order among the longer one's.
            var next = 1;
            for (var i = 1; i < shorter.Length - 1; i++)
            {
                while (next < longer.Length - 1 && !TokenMatches(shorter[i], longer[next]))
                {
                    next++;
                }

                if (next >= longer.Length - 1)
                {
                    return false;
                }

                next++;
            }

            return true;
        }

        // The first and last name of a name with middle names ("dietmar arthur wehr" gives
        // "dietmar wehr"); null when there is nothing to drop.
        public static string ShortForm(string normalized)
        {
            var tokens = Tokens(normalized);
            return tokens.Length > 2 ? $"{tokens[0]} {tokens[^1]}" : null;
        }

        // The one entry whose name is the same author as the name, or null when none or more
        // than one distinct id match. An exact name wins.
        public static TValue? FindUnique<TValue>(string normalizedName, IReadOnlyDictionary<string, TValue> byName, Func<TValue, string> id)
            where TValue : struct
        {
            if (string.IsNullOrEmpty(normalizedName) || byName == null)
            {
                return null;
            }

            if (byName.TryGetValue(normalizedName, out var exact))
            {
                return exact;
            }

            var matches = byName
                .Where(pair => SameAuthor(pair.Key, normalizedName))
                .Select(pair => pair.Value)
                .GroupBy(id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return matches.Count == 1 ? matches[0].First() : null;
        }

        private static bool TokenMatches(string token, string other)
        {
            return string.Equals(token, other, StringComparison.Ordinal) ||
                   (token.Length == 1 && other.StartsWith(token, StringComparison.Ordinal)) ||
                   (other.Length == 1 && token.StartsWith(other, StringComparison.Ordinal));
        }

        private static string[] Tokens(string normalized)
        {
            return (normalized ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
