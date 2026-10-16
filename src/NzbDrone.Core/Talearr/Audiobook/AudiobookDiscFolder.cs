using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Audiobook
{
    // TaleArr: recognises the folder names audiobook rips use for discs and parts
    // ("CD1", "CD 02", "Disc 1", "Disk_03", "Part 01", "Part One", "Part II", "Book 1 CD2",
    // "1984 - Disc 3 of 7") and returns the disc number. Used to order tracks of one book
    // and to keep same-named files from different discs apart on import.
    public static class AudiobookDiscFolder
    {
        private static readonly Dictionary<string, int> WordNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
            ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
            ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20,
            ["first"] = 1, ["second"] = 2, ["third"] = 3, ["fourth"] = 4, ["fifth"] = 5,
            ["sixth"] = 6, ["seventh"] = 7, ["eighth"] = 8, ["ninth"] = 9, ["tenth"] = 10
        };

        // A disc marker at the end of the folder name, optionally "of N". Anything may come before
        // it ("Ender's Game - CD 2"), so the whole name does not have to be a disc label.
        private static readonly Regex DiscMarkerRegex = new Regex(
            @"(?:^|[\s._\-\[(])(?<label>cd|disc|disk|dvd|part|pt|tape|side|volume|vol)[\s._\-#]*(?<number>\d{1,3}|[ivxlc]{1,7}|[a-z]+)(?:\s*(?:of|/|-)\s*(?:\d{1,3}|[a-z]+))?[\s)\]]*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Compact forms with no separator at all: "cd1", "disc02", "d3" is too loose and is not used.
        private static readonly Regex CompactRegex = new Regex(@"^(?:cd|disc|disk)(?<number>\d{1,3})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex RomanRegex = new Regex(@"^(?=[ivxlc]+$)(c{0,3})(xc|xl|l?x{0,3})(ix|iv|v?i{0,3})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // labelOnly: the name must be just the disc label ("CD 2", "Part 01", "Disc Two of Three"),
        // not a title that ends with one ("The Dark Tower Part Two").
        public static bool IsDiscFolder(string folderName, bool labelOnly = false)
        {
            return TryGetDiscNumber(folderName, out _, labelOnly);
        }

        public static bool TryGetDiscNumber(string folderName, out int discNumber, bool labelOnly = false)
        {
            discNumber = 0;

            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            var name = folderName.Trim();

            var compact = CompactRegex.Match(name);
            if (compact.Success)
            {
                return TryParseNumber(compact.Groups["number"].Value, out discNumber);
            }

            var match = DiscMarkerRegex.Match(name);
            if (!match.Success)
            {
                return false;
            }

            if (labelOnly && name.Substring(0, match.Index).Trim(' ', '.', '_', '-', '[', '(').Length > 0)
            {
                return false;
            }

            var label = match.Groups["label"].Value.ToLowerInvariant();
            var number = match.Groups["number"].Value;

            // "Side A" and "Side B" on tape rips.
            if (label == "side" && number.Length == 1 && char.IsLetter(number[0]))
            {
                discNumber = char.ToLowerInvariant(number[0]) - 'a' + 1;
                return discNumber > 0;
            }

            return TryParseNumber(number, out discNumber);
        }

        private static bool TryParseNumber(string text, out int value)
        {
            value = 0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            {
                return value >= 0;
            }

            if (WordNumbers.TryGetValue(text, out value))
            {
                return true;
            }

            if (RomanRegex.IsMatch(text))
            {
                value = ParseRoman(text);
                return value > 0;
            }

            return false;
        }

        private static int ParseRoman(string text)
        {
            var values = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10, ['l'] = 50, ['c'] = 100 };
            var total = 0;
            var lower = text.ToLowerInvariant();

            for (var i = 0; i < lower.Length; i++)
            {
                var current = values[lower[i]];
                var next = i + 1 < lower.Length ? values[lower[i + 1]] : 0;
                total += current < next ? -current : current;
            }

            return total;
        }
    }
}
