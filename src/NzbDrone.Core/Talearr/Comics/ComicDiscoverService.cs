using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Talearr.Comics.Metadata;
using NzbDrone.Core.Talearr.Comics.Parsing;

namespace NzbDrone.Core.Talearr.Comics
{
    public static class ComicDiscoverLists
    {
        public const string Trending = "trending";
        public const string Popular = "popular";
        public const string Releasing = "releasing";
        public const string Comics = "comics";

        public static readonly IReadOnlyList<string> All = new[] { Trending, Popular, Releasing, Comics };
    }

    public interface IComicDiscoverService
    {
        ComicLookupResponse Discover(string list, int page);
    }

    // Talearr: browse lists for request apps. Manga lists are AniList pages (one request per page,
    // cached for an hour). Western comics have no popularity list anywhere usable, so "comics" is a
    // curated set of well-known series, each resolved once through a Hardcover series search and
    // kept for a day.
    public class ComicDiscoverService : IComicDiscoverService
    {
        public const int PageSize = 20;

        private static readonly TimeSpan AniListLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan CuratedLifetime = TimeSpan.FromHours(24);

        // Each is both the Hardcover search term and the name a good hit must equal or start with.
        internal static readonly IReadOnlyList<string> CuratedComics = new[]
        {
            "Saga",
            "Watchmen",
            "The Sandman",
            "Invincible",
            "The Walking Dead",
            "Y: The Last Man",
            "Hellboy",
            "Batman: Year One",
            "The Dark Knight Returns",
            "Batman: The Long Halloween",
            "Spider-Man",
            "X-Men",
            "Ms. Marvel",
            "Daredevil",
            "Paper Girls",
            "Monstress",
            "Sweet Tooth",
            "Something is Killing the Children",
            "Fables",
            "Preacher",
            "Transmetropolitan",
            "Bone",
            "Maus",
            "Persepolis",
            "Black Hammer",
            "East of West",
            "Locke & Key",
            "Hawkeye",
            "Wolverine",
            "Usagi Yojimbo"
        };

        // Hardcover lists translations, reference books and boxed reprints as their own series.
        private static readonly string[] SkipWords =
        {
            "encyclopedia", "box set", "boxed set", "boxset", "slipcase", "companion", "guide", "art of", "making of", "coloring",
            "french", "francais", "german", "deutsch", "spanish", "espanol", "edicion", "italian", "italiano", "portuguese",
            "portugues", "japanese", "dutch", "polish", "edition francaise", "deutsche ausgabe"
        };

        private readonly IAniListClient _aniList;
        private readonly IComicHardcoverClient _hardcover;
        private readonly ITalearrComicsStore _store;
        private readonly ICached<List<AniListManga>> _aniListPages;
        private readonly ICached<CuratedHit> _curated;
        private readonly Logger _logger;

        public ComicDiscoverService(IAniListClient aniList,
                                    IComicHardcoverClient hardcover,
                                    ITalearrComicsStore store,
                                    ICacheManager cacheManager,
                                    Logger logger)
        {
            _aniList = aniList;
            _hardcover = hardcover;
            _store = store;
            _aniListPages = cacheManager.GetCache<List<AniListManga>>(GetType(), "anilist");
            _curated = cacheManager.GetCache<CuratedHit>(GetType(), "curated");
            _logger = logger;
        }

        public ComicLookupResponse Discover(string list, int page)
        {
            var name = (list ?? string.Empty).Trim().ToLowerInvariant();
            if (!ComicDiscoverLists.All.Contains(name))
            {
                throw new ArgumentException($"Unknown discover list '{list}'. Use one of: {string.Join(", ", ComicDiscoverLists.All)}.");
            }

            page = Math.Max(1, page);
            return name == ComicDiscoverLists.Comics ? Comics(page) : Manga(name, page);
        }

        // AniList sort and status for a manga list; null for anything else.
        internal static (string Sort, string Status)? AniListQuery(string list)
        {
            switch (list)
            {
                case ComicDiscoverLists.Trending:
                    return ("TRENDING_DESC", null);
                case ComicDiscoverLists.Popular:
                    return ("POPULARITY_DESC", null);
                case ComicDiscoverLists.Releasing:
                    return ("POPULARITY_DESC", "RELEASING");
                default:
                    return null;
            }
        }

        // The curated titles shown on a page: the first 20, then the rest.
        internal static List<string> CuratedPage(int page)
        {
            return CuratedComics.Skip((Math.Max(1, page) - 1) * PageSize).Take(PageSize).ToList();
        }

        // The Hardcover series that is the curated title: named the same or the title plus more
        // (a subtitle, a year), English, not a reference book or box set, with books in it. An exact
        // name beats a longer one (Saga, not Saga of the Swamp Thing); then the most books wins.
        internal static HardcoverComicSeries PickCuratedHit(string title, IEnumerable<HardcoverComicSeries> hits)
        {
            var wanted = ComicReleaseParser.NormalizeTitle(title);
            if (wanted.Length == 0)
            {
                return null;
            }

            return (hits ?? Enumerable.Empty<HardcoverComicSeries>())
                .Where(h => h != null && h.BooksCount > 0 && !string.IsNullOrWhiteSpace(h.Name))
                .Where(h => h.Name.All(c => c < 0x2E80))
                .Select(h => new { Hit = h, Name = ComicReleaseParser.NormalizeTitle(h.Name) })
                .Where(h => h.Name == wanted || h.Name.StartsWith(wanted + " ", StringComparison.Ordinal))
                .Where(h => !SkipWords.Any(w => (" " + h.Name + " ").Contains(" " + w + " ", StringComparison.Ordinal)))
                .OrderByDescending(h => h.Name == wanted)
                .ThenByDescending(h => Math.Max(h.Hit.PrimaryBooksCount, h.Hit.BooksCount))
                .Select(h => h.Hit)
                .FirstOrDefault();
        }

        internal static ComicLookupResult FromHardcover(HardcoverComicSeries series, IReadOnlyCollection<ComicSeries> library)
        {
            return new ComicLookupResult
            {
                HardcoverSeriesId = series.Id,
                Kind = ComicKinds.Comic,
                Title = series.Name,
                Volumes = series.PrimaryBooksCount > 0 ? series.PrimaryBooksCount : series.BooksCount,
                Authors = series.AuthorName,
                CoverUrl = series.CoverUrl,
                AgeRating = ComicAgeRatings.Unknown,
                SeriesId = library?.FirstOrDefault(s => s.HardcoverSeriesId == series.Id)?.Id
            };
        }

        private ComicLookupResponse Manga(string list, int page)
        {
            var response = new ComicLookupResponse();
            var query = AniListQuery(list).Value;
            var key = $"{list}:{page}";

            var media = _aniListPages.Find(key);
            if (media == null)
            {
                try
                {
                    media = _aniList.Browse(query.Sort, query.Status, page, PageSize);
                    _aniListPages.Set(key, media, AniListLifetime);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Comics: AniList discover list {0} page {1} failed", list, page);
                    response.Notices.Add($"AniList could not be reached: {ex.Message.TrimEnd('.')}. Try again later.");
                    return response;
                }
            }

            var library = _store.GetAllSeries();
            response.Results.AddRange(media.Select(m => ComicSeriesService.FromAniList(m, null, library)));
            return response;
        }

        private ComicLookupResponse Comics(int page)
        {
            var response = new ComicLookupResponse();
            var titles = CuratedPage(page);
            if (titles.Count == 0)
            {
                return response;
            }

            if (!_hardcover.IsConfigured)
            {
                response.Notices.Add("Add a Hardcover API token in Settings > Metadata to list comics.");
                return response;
            }

            var library = _store.GetAllSeries();
            var seen = new HashSet<int>();

            // One search per title, in order. A title resolves once a day (a miss counts too); a
            // failure stops the run so a rate limit is not hammered, and the next visit picks up
            // where this one stopped.
            foreach (var title in titles)
            {
                var cached = _curated.Find(title);
                if (cached == null)
                {
                    try
                    {
                        cached = new CuratedHit { Series = PickCuratedHit(title, _hardcover.SearchSeries(title)) };
                        _curated.Set(title, cached, CuratedLifetime);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Comics: Hardcover search for curated title {0} failed", title);
                        var busy = ex.Message.Contains("429", StringComparison.Ordinal) || ex.Message.Contains("rate", StringComparison.OrdinalIgnoreCase);
                        response.Notices.Add(busy
                            ? "Hardcover is rate limiting requests, so this list is incomplete. Try again in a minute."
                            : $"Hardcover could not be searched, so this list is incomplete: {ex.Message.TrimEnd('.')}.");
                        break;
                    }
                }

                if (cached.Series != null && seen.Add(cached.Series.Id))
                {
                    response.Results.Add(FromHardcover(cached.Series, library));
                }
            }

            return response;
        }

        // A resolved curated title; Series is null when Hardcover had no good match.
        private class CuratedHit
        {
            public HardcoverComicSeries Series { get; set; }
        }
    }
}
