using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace NzbDrone.Core.Talearr.Comics.Import
{
    // Talearr: ComicInfo.xml (Anansi Project schema, which Kavita and Komga read) for one volume.
    // Volume files carry <Volume> and no <Number>: readers treat <Number> as a chapter or issue.
    // AgeRating is what a reader's per-user age limit checks, so it is always written.
    public static class ComicInfoBuilder
    {
        public static string Build(ComicSeries series, ComicVolume volume, int pageCount)
        {
            var info = new XElement("ComicInfo",
                new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));

            void Add(string name, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    info.Add(new XElement(name, value.Trim()));
                }
            }

            Add("Title", volume.Title);
            Add("Series", series.Title);
            Add("Volume", ComicNaming.FormatNumber(volume.Number, "0"));
            Add("Count", series.TotalVolumes > 0 ? series.TotalVolumes.Value.ToString(CultureInfo.InvariantCulture) : null);
            Add("Summary", series.Overview);

            var date = volume.ReleaseDate;
            if (date.HasValue)
            {
                Add("Year", date.Value.Year.ToString(CultureInfo.InvariantCulture));
                Add("Month", date.Value.Month.ToString(CultureInfo.InvariantCulture));
                Add("Day", date.Value.Day.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                Add("Year", series.Year?.ToString(CultureInfo.InvariantCulture));
            }

            Add("Writer", series.Authors);
            Add("Publisher", series.Publisher);
            Add("Genre", series.Genres);
            Add("Web", series.AniListId.HasValue ? $"https://anilist.co/manga/{series.AniListId}" : null);
            Add("PageCount", pageCount > 0 ? pageCount.ToString(CultureInfo.InvariantCulture) : null);
            Add("LanguageISO", series.LanguageIso ?? "en");
            Add("Manga", series.Kind == ComicKinds.Manga ? (series.ReadingDirection == "ltr" ? "Yes" : "YesAndRightToLeft") : "No");
            Add("AgeRating", series.EffectiveAgeRating);
            Add("Notes", "Tagged by Talearr");

            var document = new XDocument(new XDeclaration("1.0", "utf-8", null), info);
            return document.Declaration + "\n" + document.Root;
        }

        public static string ReadAgeRating(string xml)
        {
            var root = XDocument.Parse(xml).Root;
            return root?.Elements().FirstOrDefault(e => e.Name.LocalName == "AgeRating")?.Value;
        }
    }
}
