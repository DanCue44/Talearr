using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.Comics
{
    public static class ComicKinds
    {
        public const string Manga = "manga";
        public const string Comic = "comic";

        public static string Normalize(string kind)
        {
            return string.Equals(kind, Comic, StringComparison.OrdinalIgnoreCase) ? Comic : Manga;
        }
    }

    // Where a file came from. Digital beats a scan of a printed book, which beats a c2c scan
    // (cover to cover, ads included).
    public static class ComicSources
    {
        public const string Digital = "digital";
        public const string Scan = "scan";
        public const string C2C = "c2c";
        public const string Unknown = "unknown";
    }

    public static class ComicFormats
    {
        public const string Cbz = "cbz";
        public const string Cbr = "cbr";
        public const string Cb7 = "cb7";
        public const string Pdf = "pdf";
        public const string Epub = "epub";
        public const string Unknown = "unknown";

        public static readonly string[] Extensions = { ".cbz", ".cbr", ".cb7", ".pdf", ".epub", ".zip", ".rar", ".7z" };

        public static string FromExtension(string extension)
        {
            switch ((extension ?? string.Empty).TrimStart('.').ToLowerInvariant())
            {
                case "cbz":
                case "zip":
                    return Cbz;
                case "cbr":
                case "rar":
                    return Cbr;
                case "cb7":
                case "7z":
                    return Cb7;
                case "pdf":
                    return Pdf;
                case "epub":
                    return Epub;
                default:
                    return Unknown;
            }
        }
    }

    public static class ComicDownloadStatuses
    {
        public const string Grabbed = "grabbed";
        public const string Completed = "completed";
        public const string Importing = "importing";
        public const string Imported = "imported";
        public const string Failed = "failed";
    }

    public static class ComicHistoryEvents
    {
        public const string Added = "added";
        public const string Grabbed = "grabbed";
        public const string Imported = "imported";
        public const string ImportFailed = "importFailed";
        public const string DownloadFailed = "downloadFailed";
        public const string Deleted = "fileDeleted";
        public const string Renamed = "renamed";
    }

    public class ComicRootFolder
    {
        public int Id { get; set; }
        public string Path { get; set; }

        // manga, comic, or "any".
        public string Kind { get; set; }

        // A library meant for children. Series rated above MaxAgeRating cannot be filed here.
        public bool IsKids { get; set; }
        public string MaxAgeRating { get; set; }
    }

    public class ComicSeries
    {
        public int Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string SortTitle { get; set; }

        // Other names the series is released under (romaji, native, English), newline separated.
        public string AlternateTitles { get; set; }
        public int? Year { get; set; }
        public string Status { get; set; }
        public string Overview { get; set; }
        public string Authors { get; set; }
        public string Publisher { get; set; }
        public string Genres { get; set; }
        public string CoverUrl { get; set; }
        public int? HardcoverSeriesId { get; set; }
        public int? AniListId { get; set; }
        public int? TotalVolumes { get; set; }

        // From metadata; AgeRatingOverride (set by the user) wins when present.
        public string AgeRating { get; set; }
        public string AgeRatingOverride { get; set; }
        public bool IsAdult { get; set; }

        // "rtl" for manga read right to left, "ltr" otherwise.
        public string ReadingDirection { get; set; }
        public string LanguageIso { get; set; }
        public int RootFolderId { get; set; }
        public string Path { get; set; }
        public bool Monitored { get; set; }
        public bool MonitorNewVolumes { get; set; }
        public DateTime Added { get; set; }
        public DateTime? LastRefreshed { get; set; }

        public string EffectiveAgeRating => string.IsNullOrWhiteSpace(AgeRatingOverride) ? (AgeRating ?? ComicAgeRatings.Unknown) : AgeRatingOverride;

        public IEnumerable<string> AllTitles()
        {
            yield return Title;

            foreach (var alt in (AlternateTitles ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return alt;
            }
        }
    }

    public class ComicVolume
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public decimal Number { get; set; }
        public string Title { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string CoverUrl { get; set; }
        public int? PageCount { get; set; }
        public int? HardcoverBookId { get; set; }
        public bool Monitored { get; set; }
    }

    public class ComicFile
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public int VolumeId { get; set; }
        public string Path { get; set; }
        public long Size { get; set; }
        public string Format { get; set; }
        public string Source { get; set; }
        public string ReleaseGroup { get; set; }
        public string SourceTitle { get; set; }
        public int Score { get; set; }
        public DateTime DateAdded { get; set; }
    }

    public class ComicDownload
    {
        public string DownloadId { get; set; }
        public int DownloadClientId { get; set; }
        public string Protocol { get; set; }
        public int SeriesId { get; set; }

        // Comma separated volume ids the release was grabbed for.
        public string VolumeIds { get; set; }
        public string ReleaseTitle { get; set; }
        public string Indexer { get; set; }
        public int Score { get; set; }
        public string Status { get; set; }
        public string OutputPath { get; set; }
        public string Message { get; set; }
        public DateTime GrabbedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public List<int> GetVolumeIds()
        {
            var ids = new List<int>();
            foreach (var part in (VolumeIds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    public class ComicHistoryEntry
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public int? VolumeId { get; set; }
        public string EventType { get; set; }
        public string SourceTitle { get; set; }
        public string Message { get; set; }
        public DateTime Date { get; set; }
    }

    public class ComicBlocklistEntry
    {
        public int Id { get; set; }
        public int SeriesId { get; set; }
        public string SourceTitle { get; set; }
        public string Indexer { get; set; }
        public string Reason { get; set; }
        public DateTime Date { get; set; }
    }
}
