using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.Talearr.Comics
{
    // Talearr: folder and file names for comics. One folder per series and nothing at the library
    // root, the layout Kavita and Komga both read:
    //   Manga/Witch Hat Atelier/Witch Hat Atelier v01 (2019).cbz
    //   Comics/Saga (2012)/Saga v01 (2012).cbz
    // Tokens: {Series Title} {Series Year} {Volume} {Volume:00} {Volume:000} {Year} {Volume Title}
    public static class ComicNaming
    {
        private static readonly Regex Token = new Regex(@"\{(?<name>[A-Za-z ]+)(?::(?<format>0+))?\}", RegexOptions.Compiled);

        public static string SeriesFolder(ComicSeries series, ComicSettings settings)
        {
            var format = series.Kind == ComicKinds.Comic ? settings.ComicFolderFormat : settings.MangaFolderFormat;
            var name = Apply(format, series, null);
            return FileNameBuilder.CleanFolderName(string.IsNullOrWhiteSpace(name) ? series.Title : name);
        }

        public static string FileName(ComicSeries series, ComicVolume volume, ComicSettings settings)
        {
            var name = Apply(settings.FileFormat, series, volume);
            return FileNameBuilder.CleanFileName(string.IsNullOrWhiteSpace(name) ? $"{series.Title} v{FormatNumber(volume.Number, "00")}" : name);
        }

        public static string FormatNumber(decimal number, string padding)
        {
            var whole = Math.Truncate(number);
            var text = ((long)whole).ToString(padding ?? "0", CultureInfo.InvariantCulture);
            var fraction = number - whole;
            return fraction == 0 ? text : text + fraction.ToString(".#", CultureInfo.InvariantCulture);
        }

        private static string Apply(string format, ComicSeries series, ComicVolume volume)
        {
            var result = Token.Replace(format ?? string.Empty, match =>
            {
                var padding = match.Groups["format"].Success ? match.Groups["format"].Value : null;
                switch (match.Groups["name"].Value.Trim().ToLowerInvariant())
                {
                    case "series title":
                        return series.Title;
                    case "series year":
                        return series.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    case "volume":
                        return volume == null ? string.Empty : FormatNumber(volume.Number, padding);
                    case "year":
                        return (volume?.ReleaseDate?.Year ?? series.Year)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                    case "volume title":
                        return volume?.Title ?? string.Empty;
                    default:
                        return match.Value;
                }
            });

            // A missing year leaves "()" behind.
            result = Regex.Replace(result, @"\(\s*\)|\[\s*\]", string.Empty);
            result = Regex.Replace(result, @"\s+", " ");
            return result.Trim(' ', '-', '.');
        }
    }
}
