using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Naming
{
    // One series a book is in, as the cleaner needs it.
    public sealed class TalearrTitleSeries
    {
        public TalearrTitleSeries(string name, string position)
        {
            Name = name;
            Position = position;
        }

        public string Name { get; }

        // Free text as the provider gives it ("5", "2.5"); null when unnumbered.
        public string Position { get; }
    }

    // Talearr: "Clean Series From Titles" (Settings > Metadata, off by default). Audiobook
    // metadata often repeats the series in the title: "Viridian Gate Online: Cataclysm",
    // "Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5". Audible and
    // Audiobookshelf show "Cataclysm" and "The Lich Priest". This removes:
    //
    // - a trailing series and position that name the book's own series or place:
    //   ": The Viridian Gate Archives, Book 5", ", Book 5", " (Mistborn #1)", " [Mistborn, Book 1]".
    //   A named series must match one of the book's series; a bare "Book 5" must match its place.
    // - a leading "<name>: " when the name matches one of the book's series ("Mistborn: The Final
    //   Empire" in Mistborn), or when another book of the same series starts with the same name
    //   ("Viridian Gate Online:" on books 1 and 5). Series match ignoring case, punctuation and
    //   the words "the", "series", "saga", "archives" and similar.
    //
    // A colon part that is a real subtitle ("Sapiens: A Brief History of Humankind") stays,
    // and a title is never reduced to nothing, a number or "Book 3".
    public static class TalearrSeriesTitleCleaner
    {
        private static readonly HashSet<string> SeriesFillerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and",
            "series", "saga", "sagas", "chronicle", "chronicles", "archive", "archives", "cycle",
            "trilogy", "duology", "quartet", "sequence", "collection", "novel", "novels", "book", "books",
            "universe", "world", "tales", "volume", "volumes"
        };

        // "Book 5", "Bk. 5", "Volume 5", "Vol 5", "#5", "Book #5".
        private const string PlacePattern = @"(?:(?:book|bk\.?|volume|vol\.?)\s*#?|#)\s*(?<num>\d+(?:\.\d+)?)";

        // The inside of a trailing group or the part after a separator: "<series>, Book 5".
        private static readonly Regex SeriesPlaceRegex = new Regex(
            @"^\s*(?<series>[^:()\[\]]*?)[\s,:]*" + PlacePattern + @"\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TrailingGroupRegex = new Regex(
            @"^(?<base>.*\S)\s*[\(\[](?<inner>[^()\[\]]+)[\)\]]\s*$",
            RegexOptions.Compiled);

        // Not a title on its own: "", "5", "Book 5", "#5", "Vol. 2", "Part Two", "Volume IV", "The".
        private static readonly Regex NotATitleRegex = new Regex(
            @"^[\W_]*(?:(?:the|a|an)|(?:(?:book|bk|volume|vol|part|no|number)\.?\s*)?#?\s*[\d.]+|" +
            @"(?:book|bk|volume|vol|part|no|number)(?:\.\s*|\s+)(?:[ivx]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|" +
            @"first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|last|final))?[\W_]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
        private static readonly Regex MultiSpaceRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);

        public static string Clean(string title, IEnumerable<TalearrTitleSeries> series, IEnumerable<string> siblingTitles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var seriesList = (series ?? Enumerable.Empty<TalearrTitleSeries>())
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .ToList();

            if (seriesList.Count == 0)
            {
                return title;
            }

            var result = MultiSpaceRegex.Replace(title.Trim(), " ");
            result = RemoveSeriesSuffix(result, seriesList);
            result = RemoveSeriesPrefix(result, seriesList, siblingTitles);

            return IsTitle(result) ? result : title.Trim();
        }

        // The text before the first ": ", or null when the title has none.
        public static string Prefix(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var index = title.IndexOf(':');
            if (index <= 0 || index >= title.Length - 1 || !char.IsWhiteSpace(title[index + 1]))
            {
                return null;
            }

            var prefix = MultiSpaceRegex.Replace(title.Substring(0, index).Trim(), " ");
            return prefix.Length == 0 ? null : prefix;
        }

        // True when a series name and the text match once case, punctuation and filler words
        // ("The", "Series", "Archives") are ignored.
        public static bool MatchesSeries(string text, string seriesName)
        {
            var left = SignificantWords(text);
            var right = SignificantWords(seriesName);

            return left.Count > 0 && left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase);
        }

        private static string RemoveSeriesSuffix(string title, List<TalearrTitleSeries> series)
        {
            // " (Mistborn #1)", " [The Viridian Gate Archives, Book 5]"
            var group = TrailingGroupRegex.Match(title);
            if (group.Success && NamesOwnPlace(group.Groups["inner"].Value, series) == PlaceMatch.Own)
            {
                var stripped = group.Groups["base"].Value.TrimEnd(' ', ',', ':', '-');
                if (IsTitle(stripped))
                {
                    return stripped;
                }
            }

            // ": The Viridian Gate Archives, Book 5", ", Book 5", " - Mistborn Book 1". The longest
            // tail that qualifies wins, so a series name before the place goes with it.
            for (var i = 0; i < title.Length; i++)
            {
                var c = title[i];
                var isSeparator = c == ':' || c == ',' ||
                                  (c == '-' && i > 0 && char.IsWhiteSpace(title[i - 1]) && i + 1 < title.Length && char.IsWhiteSpace(title[i + 1]));

                if (!isSeparator)
                {
                    continue;
                }

                var place = NamesOwnPlace(title.Substring(i + 1), series);
                if (place == PlaceMatch.OtherSeries)
                {
                    // "Leviathan Wakes: Dune, Book 1" names another series; its ", Book 1" stays too.
                    return title;
                }

                if (place == PlaceMatch.None)
                {
                    continue;
                }

                var stripped = title.Substring(0, i).TrimEnd(' ', ',', ':', '-');
                if (IsTitle(stripped))
                {
                    return stripped;
                }
            }

            return title;
        }

        private static string RemoveSeriesPrefix(string title, List<TalearrTitleSeries> series, IEnumerable<string> siblingTitles)
        {
            var prefix = Prefix(title);
            if (prefix == null)
            {
                return title;
            }

            var rest = title.Substring(title.IndexOf(':') + 1).Trim();
            if (!IsTitle(rest))
            {
                return title;
            }

            if (series.Any(s => MatchesSeries(prefix, s.Name)) || SharedBySibling(title, prefix, siblingTitles))
            {
                return rest;
            }

            return title;
        }

        // Another book of the series (a different title) starts with the same "<prefix>: ".
        private static bool SharedBySibling(string title, string prefix, IEnumerable<string> siblingTitles)
        {
            if (siblingTitles == null)
            {
                return false;
            }

            var own = Normalize(title);
            return siblingTitles
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Where(t => !string.Equals(Normalize(t), own, StringComparison.OrdinalIgnoreCase))
                .Any(t => string.Equals(Prefix(t.Trim()), prefix, StringComparison.OrdinalIgnoreCase));
        }

        private enum PlaceMatch
        {
            None,
            Own,
            OtherSeries
        }

        // "<series>, Book N" or "Book N": Own when it names one of the book's own series and place,
        // OtherSeries when it names a series the book is not in (or another place).
        private static PlaceMatch NamesOwnPlace(string text, List<TalearrTitleSeries> series)
        {
            var match = SeriesPlaceRegex.Match(text ?? string.Empty);
            if (!match.Success)
            {
                return PlaceMatch.None;
            }

            var named = match.Groups["series"].Value.Trim().TrimEnd(',', ':', '-').Trim();
            var number = match.Groups["num"].Value;

            if (named.Length == 0)
            {
                // A bare "Book 5" only goes when it is the book's place in one of its series.
                return series.Any(s => SamePosition(s.Position, number)) ? PlaceMatch.Own : PlaceMatch.None;
            }

            return series.Any(s => MatchesSeries(named, s.Name) &&
                                   (string.IsNullOrWhiteSpace(s.Position) || SamePosition(s.Position, number)))
                ? PlaceMatch.Own
                : PlaceMatch.OtherSeries;
        }

        private static bool SamePosition(string position, string number)
        {
            if (string.IsNullOrWhiteSpace(position) || string.IsNullOrWhiteSpace(number))
            {
                return false;
            }

            if (decimal.TryParse(position.Trim().TrimStart('#'), NumberStyles.Number, CultureInfo.InvariantCulture, out var left) &&
                decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out var right))
            {
                return left == right;
            }

            return string.Equals(position.Trim(), number, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTitle(string text)
        {
            return !string.IsNullOrWhiteSpace(text) && !NotATitleRegex.IsMatch(text);
        }

        private static List<string> SignificantWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<string>();
            }

            return WordRegex.Matches(text)
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !SeriesFillerWords.Contains(w))
                .ToList();
        }

        private static string Normalize(string text)
        {
            return MultiSpaceRegex.Replace((text ?? string.Empty).Trim(), " ");
        }
    }
}
