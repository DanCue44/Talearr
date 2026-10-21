using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Exceptions;
using NzbDrone.Core.Talearr.Comics.Metadata;
using NzbDrone.Core.Talearr.Comics.Parsing;

namespace NzbDrone.Core.Talearr.Comics
{
    public class ComicsException : NzbDroneException
    {
        public ComicsException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    // A kids library refused a series because of its age rating. The app offers the rating picker
    // right where this shows, so fixing it is one click.
    public class ComicAgeRatingRequiredException : ComicsException
    {
        public ComicAgeRatingRequiredException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class ComicLookupResult
    {
        public int? AniListId { get; set; }
        public int? HardcoverSeriesId { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public List<string> AlternateTitles { get; set; } = new List<string>();
        public int? Year { get; set; }
        public string Status { get; set; }
        public int? Volumes { get; set; }
        public string Authors { get; set; }
        public string Overview { get; set; }
        public string CoverUrl { get; set; }
        public string AgeRating { get; set; }
        public bool IsAdult { get; set; }
        public string Format { get; set; }

        // Set when the series is already in the library.
        public int? SeriesId { get; set; }
    }

    public class ComicLookupResponse
    {
        public List<ComicLookupResult> Results { get; set; } = new List<ComicLookupResult>();

        // Plain notes for the user, such as a metadata source being down.
        public List<string> Notices { get; set; } = new List<string>();
    }

    public class ComicAddRequest
    {
        public int? AniListId { get; set; }
        public int? HardcoverSeriesId { get; set; }
        public string Kind { get; set; }
        public int RootFolderId { get; set; }

        // all, future (only volumes released after adding) or none.
        public string Monitor { get; set; } = "all";
        public string AgeRatingOverride { get; set; }
    }

    public class ComicSeriesUpdate
    {
        public bool? Monitored { get; set; }
        public bool? MonitorNewVolumes { get; set; }
        public string AgeRatingOverride { get; set; }
        public bool ClearAgeRatingOverride { get; set; }
        public string Kind { get; set; }
    }

    public interface IComicSeriesService
    {
        ComicLookupResponse Lookup(string term, string kind);
        ComicSeries Add(ComicAddRequest request);
        ComicSeries Refresh(int seriesId);
        void RefreshAll();
        ComicSeries Update(int seriesId, ComicSeriesUpdate update);
        void Delete(int seriesId, bool deleteFiles);
        List<ComicVolume> AddVolumes(int seriesId, decimal from, decimal to);
        void SetVolumesMonitored(IEnumerable<int> volumeIds, bool monitored);
    }

    // Talearr: comic and manga series. Facts come from AniList (manga) and Hardcover (manga
    // volumes and comic collections). Either source may be down (AniList switched its API off in
    // October 2026), so each is optional: a series needs one of them, and Hardcover alone is
    // enough for the volume list.
    public class ComicSeriesService : IComicSeriesService
    {
        private readonly ITalearrComicsStore _store;
        private readonly IComicSettingsService _settingsService;
        private readonly IAniListClient _aniList;
        private readonly IComicHardcoverClient _hardcover;
        private readonly IMangaDexClient _mangaDex;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public ComicSeriesService(ITalearrComicsStore store,
                                  IComicSettingsService settingsService,
                                  IAniListClient aniList,
                                  IComicHardcoverClient hardcover,
                                  IMangaDexClient mangaDex,
                                  IDiskProvider diskProvider,
                                  Logger logger)
        {
            _store = store;
            _settingsService = settingsService;
            _aniList = aniList;
            _hardcover = hardcover;
            _mangaDex = mangaDex;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public ComicLookupResponse Lookup(string term, string kind)
        {
            var response = new ComicLookupResponse();
            if (string.IsNullOrWhiteSpace(term))
            {
                return response;
            }

            kind = ComicKinds.Normalize(kind);
            var library = _store.GetAllSeries();

            var aniList = new List<AniListManga>();
            if (kind == ComicKinds.Manga)
            {
                try
                {
                    aniList = _aniList.Search(term.Trim());
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: AniList search failed");
                    response.Notices.Add($"AniList could not be searched: {ex.Message.TrimEnd('.')}. Results come from Hardcover only.");
                }
            }

            var hardcover = new List<HardcoverComicSeries>();
            if (_hardcover.IsConfigured)
            {
                try
                {
                    hardcover = _hardcover.SearchSeries(term.Trim());
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: Hardcover search failed");
                    response.Notices.Add($"Hardcover could not be searched: {ex.Message}");
                }
            }
            else
            {
                response.Notices.Add("Add a Hardcover API token in Settings > Metadata to get volume lists.");
            }

            var usedHardcover = new HashSet<int>();
            foreach (var manga in aniList)
            {
                var titles = MangaTitles(manga).ToList();
                var match = hardcover.FirstOrDefault(h => !usedHardcover.Contains(h.Id) && titles.Any(t => SameTitle(t, h.Name)));
                if (match != null)
                {
                    usedHardcover.Add(match.Id);
                }

                response.Results.Add(new ComicLookupResult
                {
                    AniListId = manga.Id,
                    HardcoverSeriesId = match?.Id,
                    Kind = ComicKinds.Manga,
                    Title = manga.DisplayTitle,
                    AlternateTitles = titles.Where(t => t != manga.DisplayTitle).ToList(),
                    Year = manga.StartYear,
                    Status = FormatStatus(manga.Status),
                    Volumes = manga.Volumes ?? (match?.PrimaryBooksCount > 0 ? match.PrimaryBooksCount : null),
                    Authors = string.Join(", ", manga.Authors),
                    Overview = manga.Description,
                    CoverUrl = manga.CoverUrl ?? match?.CoverUrl,
                    AgeRating = ComicAgeRatings.FromAniList(manga.IsAdult, manga.Genres, manga.Tags),
                    IsAdult = manga.IsAdult,
                    Format = manga.Format,
                    SeriesId = library.FirstOrDefault(s => s.AniListId == manga.Id || (match != null && s.HardcoverSeriesId == match.Id))?.Id
                });
            }

            var mangaDexLookups = 0;
            foreach (var series in hardcover.Where(h => !usedHardcover.Contains(h.Id) && h.BooksCount > 0))
            {
                // No AniList facts: MangaDex can still say how the series is rated (first few only).
                var rating = ComicAgeRatings.Unknown;
                if (kind == ComicKinds.Manga && mangaDexLookups < 5)
                {
                    mangaDexLookups++;
                    rating = MangaDexClient.ToAgeRating(FindMangaDex(new[] { series.Name }, out var mangaDexError));
                    if (mangaDexError != null && !response.Notices.Any(n => n.StartsWith("MangaDex", StringComparison.Ordinal)))
                    {
                        response.Notices.Add($"MangaDex could not be searched for age ratings: {mangaDexError}");
                    }
                }

                response.Results.Add(new ComicLookupResult
                {
                    HardcoverSeriesId = series.Id,
                    Kind = kind,
                    Title = series.Name,
                    Volumes = series.PrimaryBooksCount > 0 ? series.PrimaryBooksCount : series.BooksCount,
                    Authors = series.AuthorName,
                    CoverUrl = series.CoverUrl,
                    AgeRating = rating,
                    SeriesId = library.FirstOrDefault(s => s.HardcoverSeriesId == series.Id)?.Id
                });
            }

            return response;
        }

        public ComicSeries Add(ComicAddRequest request)
        {
            if (!request.AniListId.HasValue && !request.HardcoverSeriesId.HasValue)
            {
                throw new ComicsException("Pick a series from the search results.");
            }

            var root = _store.GetRootFolder(request.RootFolderId) ?? throw new ComicsException("Choose a comics root folder (Comics > Settings).");

            if (request.AniListId.HasValue && _store.FindSeriesByAniList(request.AniListId.Value) is { } existingAniList)
            {
                throw new ComicsException("{0} is already in the library.", existingAniList.Title);
            }

            if (request.HardcoverSeriesId.HasValue && _store.FindSeriesByHardcover(request.HardcoverSeriesId.Value) is { } existingHardcover)
            {
                throw new ComicsException("{0} is already in the library.", existingHardcover.Title);
            }

            var series = new ComicSeries
            {
                Kind = ComicKinds.Normalize(request.Kind),
                AniListId = request.AniListId,
                HardcoverSeriesId = request.HardcoverSeriesId,
                RootFolderId = root.Id,
                Monitored = request.Monitor != "none",
                MonitorNewVolumes = request.Monitor != "none",
                AgeRatingOverride = ComicAgeRatings.IsKnown(request.AgeRatingOverride) ? ComicAgeRatings.Normalize(request.AgeRatingOverride) : null,
                LanguageIso = "en",
                Added = DateTime.UtcNow
            };

            var volumes = LoadFacts(series, failIfNothing: true);

            if (!ComicAgeRatings.IsAllowedFor(series.EffectiveAgeRating, root.IsKids ? root.MaxAgeRating : null))
            {
                throw new ComicAgeRatingRequiredException(series.EffectiveAgeRating == ComicAgeRatings.Unknown
                        ? "{0} has no age rating, and this kids library ({2}) only takes rated series. Set its age rating to add it."
                        : "{0} is rated {1}, above this kids library's limit ({2}). Set its age rating if the data is wrong.",
                    series.Title, series.EffectiveAgeRating, root.MaxAgeRating);
            }

            if (!string.Equals(root.Kind, "any", StringComparison.OrdinalIgnoreCase) && !string.Equals(root.Kind, series.Kind, StringComparison.OrdinalIgnoreCase))
            {
                throw new ComicsException("{0} is a folder for {1}; choose a {2} folder.", root.Path, root.Kind, series.Kind);
            }

            var settings = _settingsService.Get();
            series.Path = Path.Combine(root.Path, ComicNaming.SeriesFolder(series, settings));
            series = _store.AddSeries(series);

            _store.MergeVolumes(series.Id, volumes, newVolumesMonitored: request.Monitor == "all");

            _store.AddHistory(new ComicHistoryEntry { SeriesId = series.Id, EventType = ComicHistoryEvents.Added, Message = $"Added with {volumes.Count} volumes" });
            _logger.Info("Comics: added {0} ({1}, {2} volumes)", series.Title, series.Kind, volumes.Count);

            return series;
        }

        public ComicSeries Refresh(int seriesId)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");
            var title = series.Title;
            var volumes = LoadFacts(series, failIfNothing: false);

            // Keep the name the series was added under, so a metadata edit never moves folders.
            series.Title = title;
            series.LastRefreshed = DateTime.UtcNow;
            _store.UpdateSeries(series);

            var added = _store.MergeVolumes(series.Id, volumes, newVolumesMonitored: series.Monitored && series.MonitorNewVolumes);
            if (added.Any())
            {
                _store.AddHistory(new ComicHistoryEntry
                {
                    SeriesId = series.Id,
                    EventType = ComicHistoryEvents.Added,
                    Message = "New volumes: " + string.Join(", ", added.Select(v => ComicNaming.FormatNumber(v.Number, "0")))
                });
            }

            return series;
        }

        public void RefreshAll()
        {
            foreach (var series in _store.GetAllSeries())
            {
                try
                {
                    Refresh(series.Id);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Comics: could not refresh {0}", series.Title);
                }
            }
        }

        public ComicSeries Update(int seriesId, ComicSeriesUpdate update)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");

            if (update.Monitored.HasValue)
            {
                series.Monitored = update.Monitored.Value;
            }

            if (update.MonitorNewVolumes.HasValue)
            {
                series.MonitorNewVolumes = update.MonitorNewVolumes.Value;
            }

            if (update.ClearAgeRatingOverride)
            {
                series.AgeRatingOverride = null;
            }
            else if (!string.IsNullOrWhiteSpace(update.AgeRatingOverride))
            {
                if (!ComicAgeRatings.IsKnown(update.AgeRatingOverride))
                {
                    throw new ComicsException("Unknown age rating '{0}'", update.AgeRatingOverride);
                }

                series.AgeRatingOverride = ComicAgeRatings.Normalize(update.AgeRatingOverride);
            }

            if (!string.IsNullOrWhiteSpace(update.Kind))
            {
                series.Kind = ComicKinds.Normalize(update.Kind);
                series.ReadingDirection = series.Kind == ComicKinds.Manga ? series.ReadingDirection ?? "rtl" : "ltr";
            }

            var root = _store.GetRootFolder(series.RootFolderId);
            if (root != null && root.IsKids && !ComicAgeRatings.IsAllowedFor(series.EffectiveAgeRating, root.MaxAgeRating))
            {
                throw new ComicAgeRatingRequiredException("{0} would be rated {1}, above this kids library's limit ({2}).", series.Title, series.EffectiveAgeRating, root.MaxAgeRating);
            }

            _store.UpdateSeries(series);
            return series;
        }

        public void Delete(int seriesId, bool deleteFiles)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");

            if (deleteFiles)
            {
                foreach (var file in _store.GetFiles(seriesId))
                {
                    try
                    {
                        if (_diskProvider.FileExists(file.Path))
                        {
                            _diskProvider.DeleteFile(file.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Comics: could not delete {0}", file.Path);
                    }
                }

                try
                {
                    if (_diskProvider.FolderExists(series.Path) && _diskProvider.FolderEmpty(series.Path))
                    {
                        _diskProvider.DeleteFolder(series.Path, false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: could not remove {0}", series.Path);
                }
            }

            _store.DeleteSeries(seriesId);
            _logger.Info("Comics: removed {0}{1}", series.Title, deleteFiles ? " and its files" : string.Empty);
        }

        public List<ComicVolume> AddVolumes(int seriesId, decimal from, decimal to)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");
            if (from < 0 || to < from || to - from > 500)
            {
                throw new ComicsException("Give a range of volume numbers, like 1 to 12.");
            }

            var volumes = new List<ComicVolume>();
            for (var number = from; number <= to; number++)
            {
                volumes.Add(new ComicVolume { Number = number });
            }

            return _store.MergeVolumes(series.Id, volumes, newVolumesMonitored: series.Monitored);
        }

        public void SetVolumesMonitored(IEnumerable<int> volumeIds, bool monitored)
        {
            _store.SetVolumesMonitored(volumeIds, monitored);
        }

        // Fills the series facts and returns the volume list, from whichever sources answer.
        private List<ComicVolume> LoadFacts(ComicSeries series, bool failIfNothing)
        {
            var problems = new List<string>();
            AniListManga manga = null;
            HardcoverComicSeries hardcover = null;

            if (series.AniListId.HasValue)
            {
                try
                {
                    manga = _aniList.Get(series.AniListId.Value);
                }
                catch (Exception ex)
                {
                    problems.Add("AniList: " + ex.Message);
                }
            }

            if (!series.HardcoverSeriesId.HasValue && manga != null && _hardcover.IsConfigured)
            {
                series.HardcoverSeriesId = FindHardcoverSeries(MangaTitles(manga));
            }

            if (series.HardcoverSeriesId.HasValue)
            {
                try
                {
                    hardcover = _hardcover.GetSeries(series.HardcoverSeriesId.Value);
                }
                catch (Exception ex)
                {
                    problems.Add("Hardcover: " + ex.Message);
                }
            }

            if (manga == null && hardcover == null)
            {
                var message = problems.Any() ? string.Join("; ", problems) : "the series was not found";
                if (failIfNothing)
                {
                    throw new ComicsException("Could not read the series ({0}).", message);
                }

                _logger.Warn("Comics: could not refresh {0} ({1})", series.Title, message);
                return new List<ComicVolume>();
            }

            var titles = new List<string>();
            if (manga != null)
            {
                titles.AddRange(MangaTitles(manga));
            }

            if (hardcover != null && !string.IsNullOrWhiteSpace(hardcover.Name))
            {
                titles.Add(hardcover.Name);
            }

            series.Title = string.IsNullOrWhiteSpace(series.Title) ? manga?.DisplayTitle ?? hardcover?.Name : series.Title;
            series.SortTitle = ComicReleaseParser.NormalizeTitle(series.Title);
            series.AlternateTitles = string.Join("\n", titles
                .Where(t => !string.IsNullOrWhiteSpace(t) && t != series.Title && t.All(c => c < 0x2E80))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            series.Overview = manga?.Description ?? hardcover?.Description ?? series.Overview;
            series.Authors = manga != null && manga.Authors.Any() ? string.Join(", ", manga.Authors) : hardcover?.AuthorName ?? series.Authors;
            series.Genres = manga != null && manga.Genres.Any() ? string.Join(", ", manga.Genres) : series.Genres;
            series.CoverUrl = manga?.CoverUrl ?? hardcover?.CoverUrl ?? series.CoverUrl;
            series.Status = manga != null ? FormatStatus(manga.Status) : series.Status;
            series.Year = manga?.StartYear ?? hardcover?.Volumes.Select(v => v.ReleaseDate?.Year).FirstOrDefault(y => y.HasValue) ?? series.Year;

            if (manga != null)
            {
                series.IsAdult = manga.IsAdult;
                series.AgeRating = ComicAgeRatings.FromAniList(manga.IsAdult, manga.Genres, manga.Tags);

                // Japanese manga reads right to left; Korean and Chinese comics read left to right.
                series.ReadingDirection = series.Kind == ComicKinds.Manga && (manga.CountryOfOrigin ?? "JP") == "JP" ? "rtl" : "ltr";
            }
            else
            {
                series.AgeRating ??= ComicAgeRatings.Unknown;
                series.ReadingDirection ??= series.Kind == ComicKinds.Manga ? "rtl" : "ltr";
            }

            // AniList down or silent on the rating: MangaDex fills in the facts it has.
            if (series.Kind == ComicKinds.Manga && (manga == null || series.AgeRating == ComicAgeRatings.Unknown))
            {
                var mangaDex = FindMangaDex(titles.Prepend(series.Title), out var mangaDexError);
                if (mangaDexError != null)
                {
                    _logger.Warn("Comics: MangaDex could not rate {0}: {1}", series.Title, mangaDexError);
                }

                if (mangaDex != null)
                {
                    series.AgeRating = MangaDexClient.ToAgeRating(mangaDex);

                    if (manga == null)
                    {
                        series.AlternateTitles = string.Join("\n", (series.AlternateTitles ?? string.Empty)
                            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                            .Concat(mangaDex.Titles)
                            .Where(t => !string.Equals(t, series.Title, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase));
                        series.Overview ??= mangaDex.Description;
                        series.Status = FormatMangaDexStatus(mangaDex.Status) ?? series.Status;
                        series.Year ??= mangaDex.Year;
                        series.ReadingDirection = mangaDex.OriginalLanguage == "ja" ? "rtl" : "ltr";
                    }
                }
            }

            var volumes = new List<ComicVolume>();
            if (hardcover != null && hardcover.Volumes.Any())
            {
                volumes.AddRange(hardcover.Volumes.Select(v => new ComicVolume
                {
                    Number = v.Number,
                    Title = v.Title,
                    ReleaseDate = v.ReleaseDate,
                    CoverUrl = v.CoverUrl,
                    PageCount = v.Pages,
                    HardcoverBookId = v.BookId
                }));
            }
            else if (manga?.Volumes > 0)
            {
                volumes.AddRange(Enumerable.Range(1, manga.Volumes.Value).Select(n => new ComicVolume { Number = n }));
            }

            var maxVolume = volumes.Any() ? (int)volumes.Max(v => v.Number) : 0;
            series.TotalVolumes = Math.Max(manga?.Volumes ?? 0, maxVolume) is var total && total > 0 ? total : series.TotalVolumes;

            return volumes;
        }

        private int? FindHardcoverSeries(IEnumerable<string> titles)
        {
            foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t) && t.All(c => c < 0x2E80)).Take(3))
            {
                try
                {
                    var hit = _hardcover.SearchSeries(title)
                        .Where(h => SameTitle(h.Name, title))
                        .OrderByDescending(h => h.PrimaryBooksCount)
                        .FirstOrDefault();

                    if (hit != null)
                    {
                        return hit.Id;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: Hardcover series search for {0} failed", title);
                    return null;
                }
            }

            return null;
        }

        // The MangaDex entry whose English or romaji title is one of the series' names.
        private MangaDexManga FindMangaDex(IEnumerable<string> titles, out string error)
        {
            error = null;
            var wanted = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var title in wanted.Where(t => t.All(c => c < 0x2E80)).Take(2))
            {
                try
                {
                    var hit = _mangaDex.Search(title).FirstOrDefault(m => m.Titles.Any(t => wanted.Any(w => SameTitle(t, w))));
                    if (hit != null)
                    {
                        return hit;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: MangaDex search for {0} failed", title);
                    error = ex.Message;
                    return null;
                }
            }

            return null;
        }

        private static string FormatMangaDexStatus(string status)
        {
            switch (status)
            {
                case "completed":
                    return "Finished";
                case "ongoing":
                    return "Ongoing";
                case "hiatus":
                    return "On hiatus";
                case "cancelled":
                    return "Cancelled";
                default:
                    return null;
            }
        }

        private static IEnumerable<string> MangaTitles(AniListManga manga)
        {
            return new[] { manga.EnglishTitle, manga.RomajiTitle }
                .Concat(manga.Synonyms)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool SameTitle(string a, string b)
        {
            var left = ComicReleaseParser.NormalizeTitle(a);
            return left.Length > 0 && left == ComicReleaseParser.NormalizeTitle(b);
        }

        private static string FormatStatus(string status)
        {
            switch (status)
            {
                case "FINISHED":
                    return "Finished";
                case "RELEASING":
                    return "Ongoing";
                case "HIATUS":
                    return "On hiatus";
                case "CANCELLED":
                    return "Cancelled";
                case "NOT_YET_RELEASED":
                    return "Not yet released";
                default:
                    return status;
            }
        }
    }
}
