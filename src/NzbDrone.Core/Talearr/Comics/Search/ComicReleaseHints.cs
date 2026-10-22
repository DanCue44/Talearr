using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Comics.Parsing;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    // Talearr: facts a source knows about a release that its title may not say. MAM titles are
    // clean ("Witch Hat Atelier, Vol. 8") but leave format, digital or scan and language to
    // separate fields, and MAM files each torrent under a series and number of its own.
    public class ComicReleaseHint
    {
        // The title the comic parser reads instead of the release title (shown unchanged).
        public string ParseTitle { get; set; }
        public string Format { get; set; }
        public string Source { get; set; }

        // ISO 639-1 code when the release is in a language other than English.
        public string Language { get; set; }
        public bool IsScanlation { get; set; }

        // Why the release cannot be a comic at all (for example another MAM category).
        public string Rejection { get; set; }

        public void ApplyTo(ParsedComicRelease parsed)
        {
            if (parsed.Format == ComicFormats.Unknown && !string.IsNullOrWhiteSpace(Format))
            {
                parsed.Format = Format;
            }

            if (parsed.Source == ComicSources.Unknown && !string.IsNullOrWhiteSpace(Source))
            {
                parsed.Source = Source;
            }

            if (parsed.Language == null && !string.IsNullOrWhiteSpace(Language))
            {
                parsed.Language = Language;
            }

            parsed.IsScanlation |= IsScanlation;
        }
    }

    public static class ComicReleaseHints
    {
        private static readonly ConditionalWeakTable<ReleaseInfo, ComicReleaseHint> Hints = new ConditionalWeakTable<ReleaseInfo, ComicReleaseHint>();
        private static readonly Regex Word = new Regex(@"[a-z0-9]+", RegexOptions.Compiled);

        public static void Attach(ReleaseInfo release, ComicReleaseHint hint)
        {
            if (release != null && hint != null)
            {
                Hints.AddOrUpdate(release, hint);
            }
        }

        public static ComicReleaseHint Get(ReleaseInfo release)
        {
            return release != null && Hints.TryGetValue(release, out var hint) ? hint : null;
        }

        public static ComicReleaseHint FromMam(MamComicResult result, int[] comicCategoryIds)
        {
            var release = result.Release;
            var torrent = release as TorrentInfo;
            var hint = new ComicReleaseHint { ParseTitle = release.Title };

            if (result.CategoryId.HasValue && comicCategoryIds.Length > 0 && !comicCategoryIds.Contains(result.CategoryId.Value))
            {
                hint.Rejection = $"Not in MAM's comics category ({torrent?.CategoryName ?? result.CategoryId.ToString()})";
            }

            // MAM's own series and number beat whatever the title wording is ("Vol. 8",
            // "Volume Eight", "#8").
            if (!string.IsNullOrWhiteSpace(result.SeriesName) && decimal.TryParse(result.SeriesNumber, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                hint.ParseTitle = $"{result.SeriesName} v{ComicNaming.FormatNumber(number, "00")}";
            }

            hint.Format = ReadFormat(torrent?.FileType);

            var tags = Words(result.Tags);
            if (tags.Contains("scanlation") || tags.Contains("scanlations") || (tags.Contains("fan") && (tags.Contains("translation") || tags.Contains("translated"))))
            {
                hint.IsScanlation = true;
            }

            if (tags.Contains("c2c"))
            {
                hint.Source = ComicSources.C2C;
            }
            else if (tags.Contains("digital") || tags.Contains("retail") || tags.Contains("webrip"))
            {
                hint.Source = ComicSources.Digital;
            }
            else if (tags.Contains("scan") || tags.Contains("scanned") || tags.Contains("scans"))
            {
                hint.Source = ComicSources.Scan;
            }

            if (MyAnonaMouseLanguageMapper.TryGetLanguage(result.LanguageCode, out _, out var canonical) && !string.Equals(canonical, "eng", StringComparison.OrdinalIgnoreCase))
            {
                hint.Language = ToTwoLetter(canonical);
            }

            return hint;
        }

        // MAM lists every format in the torrent ("cbz", "cbr pdf"); the best one counts.
        private static string ReadFormat(string fileType)
        {
            var words = Words(fileType);
            foreach (var format in new[] { ComicFormats.Cbz, ComicFormats.Cbr, ComicFormats.Cb7, ComicFormats.Epub, ComicFormats.Pdf })
            {
                if (words.Contains(format))
                {
                    return format;
                }
            }

            return null;
        }

        private static string ToTwoLetter(string threeLetter)
        {
            try
            {
                return System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
                    .FirstOrDefault(c => string.Equals(c.ThreeLetterISOLanguageName, threeLetter, StringComparison.OrdinalIgnoreCase))?.TwoLetterISOLanguageName ?? threeLetter;
            }
            catch (Exception)
            {
                return threeLetter;
            }
        }

        private static System.Collections.Generic.HashSet<string> Words(string value)
        {
            return new System.Collections.Generic.HashSet<string>(Word.Matches((value ?? string.Empty).ToLowerInvariant()).Select(m => m.Value));
        }
    }
}
