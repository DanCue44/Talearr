using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Audiobook
{
    // TaleArr: extra file and folder naming tokens for audiobooks, and the helpers behind the
    // zero-padded series position. Registered by FileNameBuilder; values come from the existing
    // book, edition and file fields, nothing is stored.
    //
    //   {Book SeriesPosition:00}  series position padded to the number of zeros ("03", "02.5")
    //   {Book Year}               first published year, else the edition year, else empty
    //   {Abridged}                "Abridged" for an abridged recording, else empty
    //   {Abridgement}             "Abridged" or "Unabridged" when known, else empty
    //
    // Audiobookshelf reads "Author/Series/01 - 1994 - Title {Narrator}/" as series position 1,
    // published year 1994 and narrator; see AudiobookshelfFormat.
    public static class TalearrNamingTokens
    {
        // Audiobookshelf's preferred layout (Author/Series/Vol - Year - Title {Narrator}/files),
        // relative to the author folder.
        public const string AudiobookshelfFormat = "{Book Series}/{Book SeriesPosition:00 - }{Book Year - }{Book Title}{ {Narrator}}/{Book Title}{ (PartNumber:smart)}";

        private static readonly Regex PositionRegex = new Regex(@"^(?<int>\d+)(?<rest>(?:[.,]\d+)?)$", RegexOptions.Compiled);

        internal static void Add(Dictionary<string, Func<TokenMatch, string>> tokens, Edition edition, ITalearrAbridgedResolver abridgedResolver = null)
        {
            var year = GetYear(edition);
            tokens["{Book Year}"] = m => year.HasValue ? year.Value.ToString(CultureInfo.InvariantCulture) : m.DefaultValue(string.Empty);

            var abridgement = GetAbridgement(edition, abridgedResolver);
            tokens["{Abridged}"] = m => abridgement == AbridgedMarker.Abridged ? "Abridged" : m.DefaultValue(string.Empty);
            tokens["{Abridgement}"] = m => abridgement switch
            {
                AbridgedMarker.Abridged => "Abridged",
                AbridgedMarker.Unabridged => "Unabridged",
                _ => m.DefaultValue(string.Empty)
            };
        }

        // "3" with "00" -> "03", "2.5" with "00" -> "02.5", "12" with "0" -> "12".
        // Positions that are not a plain number ("1-3", "Prequel") are returned unchanged.
        public static string FormatSeriesPosition(string position, string customFormat)
        {
            if (string.IsNullOrWhiteSpace(position))
            {
                return position;
            }

            var trimmed = position.Trim();

            if (string.IsNullOrWhiteSpace(customFormat) || !customFormat.All(c => c == '0'))
            {
                return trimmed;
            }

            var match = PositionRegex.Match(trimmed);
            if (!match.Success)
            {
                return trimmed;
            }

            var integer = match.Groups["int"].Value.TrimStart('0');
            if (integer.Length == 0)
            {
                integer = "0";
            }

            return integer.PadLeft(customFormat.Length, '0') + match.Groups["rest"].Value;
        }

        public static int? GetYear(Edition edition)
        {
            var book = edition?.Book;

            if (book?.ReleaseDate.HasValue == true && book.ReleaseDate.Value.Year > 1)
            {
                return book.ReleaseDate.Value.Year;
            }

            if (edition?.ReleaseDate.HasValue == true && edition.ReleaseDate.Value.Year > 1)
            {
                return edition.ReleaseDate.Value.Year;
            }

            return null;
        }

        // The same answer the grab policy uses (TalearrAbridgedResolver: Audible facts first, then
        // edition text and runtime), so a folder named "Abridged" matches what was searched for.
        // Without a resolver, or when it has no answer, the metadata rules below decide.
        public static AbridgedMarker GetAbridgement(Edition edition, ITalearrAbridgedResolver abridgedResolver)
        {
            if (edition != null && abridgedResolver != null)
            {
                try
                {
                    var resolved = abridgedResolver.ResolveEdition(edition, SiblingsOf(edition)).Marker;
                    if (resolved != AbridgedMarker.Unknown)
                    {
                        return resolved;
                    }
                }
                catch (Exception)
                {
                    // Fall back to metadata only.
                }
            }

            return GetAbridgement(edition);
        }

        private static List<Edition> SiblingsOf(Edition edition)
        {
            try
            {
                return edition.Book?.Editions?.Where(e => e != null).ToList();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static AbridgedMarker GetAbridgement(Edition edition)
        {
            if (edition == null)
            {
                return AbridgedMarker.Unknown;
            }

            if (AbridgedReleaseDetector.IsAbridgedByMetadata(edition))
            {
                return AbridgedMarker.Abridged;
            }

            List<Edition> siblings = null;
            try
            {
                siblings = edition.Book?.Editions?.Where(e => e != null).ToList();
            }
            catch (Exception)
            {
                // Editions are lazy loaded and may be unavailable while renaming.
                siblings = null;
            }

            if (siblings != null && AbridgedReleaseDetector.IsAbridgedByRuntime(edition, siblings))
            {
                return AbridgedMarker.Abridged;
            }

            var marked = new[]
                {
                    edition.Title,
                    edition.Subtitle,
                    edition.Disambiguation,
                    edition.EditionInfo,
                    edition.EditionFormat,
                    edition.Format
                }
                .Select(AbridgedReleaseDetector.Detect)
                .Any(marker => marker == AbridgedMarker.Unabridged);

            return marked ? AbridgedMarker.Unabridged : AbridgedMarker.Unknown;
        }

        // Multi-file audiobooks keep their original file names when renaming is off (or the
        // format has no part number). Files from different disc folders often share names
        // ("Part 1/Chapter 1-2.mp3" and "Part 3/Chapter 1-2.mp3"), and once they land in one
        // book folder the second would overwrite or skip the first. Those files get their part
        // number in front ("01 - Chapter 1-2"), which also keeps them in playing order.
        public static string GetOriginalFileName(BookFile bookFile)
        {
            var original = Path.GetFileNameWithoutExtension(bookFile?.Path ?? string.Empty);

            if (bookFile == null || bookFile.PartCount <= 1 || bookFile.Part <= 0 || !IsAudio(bookFile))
            {
                return original;
            }

            var parent = Path.GetFileName(Path.GetDirectoryName(bookFile.Path ?? string.Empty) ?? string.Empty);
            if (!AudiobookDiscFolder.IsDiscFolder(parent, labelOnly: true))
            {
                return original;
            }

            var width = Math.Max(2, bookFile.PartCount.ToString(CultureInfo.InvariantCulture).Length);
            return bookFile.Part.ToString(new string('0', width), CultureInfo.InvariantCulture) + " - " + original;
        }

        private static bool IsAudio(BookFile bookFile)
        {
            if (!string.IsNullOrWhiteSpace(bookFile.MediaType))
            {
                return string.Equals(bookFile.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase);
            }

            return MediaFileExtensions.AudioExtensions.Contains(Path.GetExtension(bookFile.Path ?? string.Empty));
        }
    }
}
