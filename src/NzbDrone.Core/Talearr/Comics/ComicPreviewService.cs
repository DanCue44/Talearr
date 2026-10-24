using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Talearr.Comics.Metadata;

namespace NzbDrone.Core.Talearr.Comics
{
    // AniList or Hardcover has no series with the id asked for.
    public class ComicPreviewNotFoundException : ComicsException
    {
        public ComicPreviewNotFoundException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    // No source could be asked and nothing is known locally, so there is nothing to show yet.
    public class ComicPreviewUnavailableException : ComicsException
    {
        public ComicPreviewUnavailableException(string message, params object[] args)
            : base(message, args)
        {
        }
    }

    public class ComicPreviewVolume
    {
        public decimal Number { get; set; }
        public string Title { get; set; }

        // yyyy-MM-dd, or null when the date is not known.
        public string ReleaseDate { get; set; }
        public string CoverUrl { get; set; }
    }

    // One series as a request app's detail page shows it, before it is added.
    public class ComicPreview
    {
        public int? AniListId { get; set; }
        public int? HardcoverSeriesId { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public List<string> AlternateTitles { get; set; } = new List<string>();
        public string Overview { get; set; }
        public string Authors { get; set; }
        public int? Year { get; set; }
        public string Status { get; set; }
        public List<string> Genres { get; set; } = new List<string>();
        public string Format { get; set; }
        public string AgeRating { get; set; }
        public bool IsAdult { get; set; }
        public string CoverUrl { get; set; }
        public string BannerUrl { get; set; }
        public int? VolumeCount { get; set; }
        public List<ComicPreviewVolume> Volumes { get; set; } = new List<ComicPreviewVolume>();

        // Set when the series is already in the library.
        public int? SeriesId { get; set; }
        public List<string> Notices { get; set; } = new List<string>();

        internal ComicPreview Copy()
        {
            var copy = (ComicPreview)MemberwiseClone();
            copy.AlternateTitles = new List<string>(AlternateTitles);
            copy.Genres = new List<string>(Genres);
            copy.Volumes = new List<ComicPreviewVolume>(Volumes);
            copy.Notices = new List<string>(Notices);
            return copy;
        }
    }

    public interface IComicPreviewService
    {
        ComicPreview Preview(int? aniListId, int? hardcoverSeriesId, string kind);
    }

    // Talearr: the full facts of one series for a request app's detail page. AniList gives the
    // manga facts, Hardcover the volume list; either may be down, so what one source knows is
    // still shown with a notice about the other. Answers are kept for 6 hours.
    public class ComicPreviewService : IComicPreviewService
    {
        private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

        private readonly IAniListClient _aniList;
        private readonly IComicHardcoverClient _hardcover;
        private readonly ITalearrComicsStore _store;
        private readonly ICached<ComicPreview> _cache;
        private readonly Logger _logger;

        public ComicPreviewService(IAniListClient aniList,
                                   IComicHardcoverClient hardcover,
                                   ITalearrComicsStore store,
                                   ICacheManager cacheManager,
                                   Logger logger)
        {
            _aniList = aniList;
            _hardcover = hardcover;
            _store = store;
            _cache = cacheManager.GetCache<ComicPreview>(GetType(), "preview");
            _logger = logger;
        }

        public ComicPreview Preview(int? aniListId, int? hardcoverSeriesId, string kind)
        {
            if (!aniListId.HasValue && !hardcoverSeriesId.HasValue)
            {
                throw new ArgumentException("Pass aniListId or hardcoverSeriesId.");
            }

            var library = _store.GetAllSeries();
            var key = aniListId.HasValue
                ? $"anilist:{aniListId.Value}:{hardcoverSeriesId}"
                : $"hardcover:{hardcoverSeriesId.Value}:{HardcoverKind(kind)}";

            var preview = _cache.Find(key);
            if (preview == null)
            {
                var complete = true;
                preview = aniListId.HasValue
                    ? FromAniListId(aniListId.Value, hardcoverSeriesId, library, ref complete)
                    : FromHardcoverId(hardcoverSeriesId.Value, HardcoverKind(kind), ref complete);

                // A partial answer (a source failed) is not kept, so the next visit tries again.
                if (complete)
                {
                    _cache.Set(key, preview, Lifetime);
                }
            }

            var result = preview.Copy();
            result.SeriesId = library.FirstOrDefault(s => (result.AniListId.HasValue && s.AniListId == result.AniListId) ||
                                                          (result.HardcoverSeriesId.HasValue && s.HardcoverSeriesId == result.HardcoverSeriesId))?.Id;
            return result;
        }

        private ComicPreview FromAniListId(int aniListId, int? hardcoverSeriesId, List<ComicSeries> library, ref bool complete)
        {
            var notices = new List<string>();
            var owned = library.FirstOrDefault(s => s.AniListId == aniListId);
            AniListManga manga = null;
            try
            {
                manga = _aniList.GetPreview(aniListId);
                if (manga == null)
                {
                    throw new ComicPreviewNotFoundException("AniList has no manga with id {0}.", aniListId);
                }
            }
            catch (ComicPreviewNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Comics: AniList preview of {0} failed", aniListId);
                complete = false;
                if (owned == null)
                {
                    throw new ComicPreviewUnavailableException("AniList could not be reached: {0}", ex.Message.TrimEnd('.'));
                }

                notices.Add($"AniList could not be reached: {ex.Message.TrimEnd('.')}. Showing the facts saved in the library.");
            }

            var preview = manga != null ? MapAniList(manga) : MapLibrary(owned);
            preview.AniListId = aniListId;
            preview.Notices.AddRange(notices);

            // The Hardcover series for the volume list: the one asked for, the one the library
            // already pairs with it, else the search match Lookup would pick.
            var hardcoverId = hardcoverSeriesId ?? owned?.HardcoverSeriesId;
            if (!_hardcover.IsConfigured)
            {
                preview.HardcoverSeriesId = hardcoverId;
                preview.Notices.Add("Add a Hardcover API token in Settings > Metadata to get volume lists.");
                return preview;
            }

            HardcoverComicSeries series = null;
            try
            {
                if (!hardcoverId.HasValue && manga != null)
                {
                    hardcoverId = FindHardcoverSeries(ComicSeriesService.MangaTitles(manga));
                }

                if (hardcoverId.HasValue)
                {
                    series = _hardcover.GetSeries(hardcoverId.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Comics: Hardcover preview for AniList {0} failed", aniListId);
                complete = false;
                preview.Notices.Add($"Hardcover could not be reached, so the volume list is missing: {ex.Message.TrimEnd('.')}.");
            }

            preview.HardcoverSeriesId = series?.Id ?? hardcoverId;
            if (series != null)
            {
                preview.Volumes = MapVolumes(series);
                preview.CoverUrl ??= series.CoverUrl;
                preview.Authors ??= series.AuthorName;
                preview.Overview ??= AniListClient.CleanDescription(series.Description);
            }

            preview.VolumeCount = VolumeCount(manga != null ? manga.Volumes : owned?.TotalVolumes, series);
            return preview;
        }

        private ComicPreview FromHardcoverId(int hardcoverSeriesId, string kind, ref bool complete)
        {
            if (!_hardcover.IsConfigured)
            {
                complete = false;
                throw new ComicPreviewUnavailableException("Add a Hardcover API token in Settings > Metadata to preview Hardcover series.");
            }

            HardcoverComicSeries series;
            try
            {
                series = _hardcover.GetSeries(hardcoverSeriesId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Comics: Hardcover preview of {0} failed", hardcoverSeriesId);
                complete = false;
                throw new ComicPreviewUnavailableException("Hardcover could not be reached: {0}", ex.Message.TrimEnd('.'));
            }

            if (series == null)
            {
                throw new ComicPreviewNotFoundException("Hardcover has no series with id {0}.", hardcoverSeriesId);
            }

            return MapHardcover(series, kind);
        }

        // Same pairing as Lookup: an English or romaji title of the manga that names a Hardcover
        // series exactly; of several, the one with the most main volumes.
        private int? FindHardcoverSeries(IEnumerable<string> titles)
        {
            foreach (var title in titles.Where(t => !string.IsNullOrWhiteSpace(t) && t.All(c => c < 0x2E80)).Take(3))
            {
                var hit = PickHardcoverMatch(title, _hardcover.SearchSeries(title));
                if (hit != null)
                {
                    return hit.Id;
                }
            }

            return null;
        }

        internal static HardcoverComicSeries PickHardcoverMatch(string title, IEnumerable<HardcoverComicSeries> hits)
        {
            return (hits ?? Enumerable.Empty<HardcoverComicSeries>())
                .Where(h => h != null && ComicSeriesService.SameTitle(h.Name, title))
                .OrderByDescending(h => h.PrimaryBooksCount)
                .FirstOrDefault();
        }

        internal static string HardcoverKind(string kind)
        {
            return string.Equals(kind?.Trim(), ComicKinds.Manga, StringComparison.OrdinalIgnoreCase) ? ComicKinds.Manga : ComicKinds.Comic;
        }

        // The best known volume count: AniList's, else Hardcover's main volumes, else the volumes
        // Hardcover lists.
        internal static int? VolumeCount(int? aniListVolumes, HardcoverComicSeries series)
        {
            if (aniListVolumes > 0)
            {
                return aniListVolumes;
            }

            if (series?.PrimaryBooksCount > 0)
            {
                return series.PrimaryBooksCount;
            }

            return series?.Volumes.Count > 0 ? series.Volumes.Count : null;
        }

        // The AniList facts, mapped the way Lookup and Discover map them.
        internal static ComicPreview MapAniList(AniListManga manga)
        {
            var result = ComicSeriesService.FromAniList(manga, null, null);
            return new ComicPreview
            {
                AniListId = manga.Id,
                Kind = result.Kind,
                Title = result.Title,
                AlternateTitles = result.AlternateTitles,
                Overview = AniListClient.CleanDescription(result.Overview),
                Authors = string.IsNullOrWhiteSpace(result.Authors) ? null : result.Authors,
                Year = result.Year,
                Status = result.Status,
                Genres = manga.Genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToList(),
                Format = result.Format,
                AgeRating = result.AgeRating,
                IsAdult = result.IsAdult,
                CoverUrl = result.CoverUrl,
                BannerUrl = manga.BannerUrl,
                VolumeCount = manga.Volumes > 0 ? manga.Volumes : null
            };
        }

        internal static ComicPreview MapHardcover(HardcoverComicSeries series, string kind)
        {
            return new ComicPreview
            {
                HardcoverSeriesId = series.Id,
                Kind = kind,
                Title = series.Name,
                Overview = AniListClient.CleanDescription(series.Description),
                Authors = string.IsNullOrWhiteSpace(series.AuthorName) ? null : series.AuthorName,
                Year = series.Volumes.Select(v => v.ReleaseDate?.Year).FirstOrDefault(y => y.HasValue),
                AgeRating = ComicAgeRatings.Unknown,
                CoverUrl = series.CoverUrl,
                VolumeCount = VolumeCount(null, series),
                Volumes = MapVolumes(series)
            };
        }

        internal static List<ComicPreviewVolume> MapVolumes(HardcoverComicSeries series)
        {
            return (series?.Volumes ?? new List<HardcoverComicVolume>())
                .Select(v => new ComicPreviewVolume
                {
                    Number = v.Number,
                    Title = string.IsNullOrWhiteSpace(v.Title) ? null : v.Title,
                    ReleaseDate = v.ReleaseDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CoverUrl = string.IsNullOrWhiteSpace(v.CoverUrl) ? null : v.CoverUrl
                })
                .ToList();
        }

        // AniList is down but the series is in the library: show what was saved when it was added.
        private static ComicPreview MapLibrary(ComicSeries series)
        {
            return new ComicPreview
            {
                AniListId = series.AniListId,
                Kind = series.Kind,
                Title = series.Title,
                AlternateTitles = (series.AlternateTitles ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(),
                Overview = series.Overview,
                Authors = string.IsNullOrWhiteSpace(series.Authors) ? null : series.Authors,
                Year = series.Year,
                Status = series.Status,
                Genres = (series.Genres ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                AgeRating = series.AgeRating ?? ComicAgeRatings.Unknown,
                IsAdult = series.IsAdult,
                CoverUrl = series.CoverUrl
            };
        }
    }
}
