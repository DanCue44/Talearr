using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Metadata;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicDiscoverFixture
    {
        private const string BrowsePage = @"{
            ""Page"": {
                ""media"": [
                    {
                        ""id"": 30013,
                        ""title"": { ""romaji"": ""ONE PIECE"", ""english"": ""One Piece"", ""native"": ""ONE PIECE"" },
                        ""synonyms"": [ ""OP"" ],
                        ""format"": ""MANGA"",
                        ""status"": ""RELEASING"",
                        ""volumes"": null,
                        ""chapters"": null,
                        ""isAdult"": false,
                        ""genres"": [ ""Action"", ""Adventure"" ],
                        ""countryOfOrigin"": ""JP"",
                        ""startDate"": { ""year"": 1997 },
                        ""coverImage"": { ""extraLarge"": ""https://img.example/op-xl.jpg"", ""large"": ""https://img.example/op-l.jpg"" },
                        ""description"": ""Gol D. Roger<br>was known.<br><br>(Source: Viz)"",
                        ""tags"": [ { ""name"": ""Pirates"", ""rank"": 95, ""isAdult"": false } ],
                        ""staff"": { ""edges"": [ { ""role"": ""Story & Art"", ""node"": { ""name"": { ""full"": ""Eiichiro Oda"" } } } ] }
                    },
                    {
                        ""id"": 1,
                        ""title"": { ""romaji"": ""Some Light Novel"" },
                        ""format"": ""NOVEL"",
                        ""isAdult"": false
                    },
                    {
                        ""id"": 2,
                        ""title"": { ""romaji"": ""Adult Entry"" },
                        ""format"": ""MANGA"",
                        ""isAdult"": true
                    },
                    {
                        ""id"": 105398,
                        ""title"": { ""romaji"": ""Na Honjaman Level Up"", ""english"": ""Solo Leveling"" },
                        ""format"": ""MANGA"",
                        ""status"": ""FINISHED"",
                        ""volumes"": 14,
                        ""isAdult"": false,
                        ""startDate"": { ""year"": 2018 },
                        ""coverImage"": { ""large"": ""https://img.example/sl-l.jpg"" }
                    }
                ]
            }
        }";

        [TestCase("trending", "TRENDING_DESC", null)]
        [TestCase("popular", "POPULARITY_DESC", null)]
        [TestCase("releasing", "POPULARITY_DESC", "RELEASING")]
        public void aniList_lists_map_to_sort_and_status(string list, string sort, string status)
        {
            var query = ComicDiscoverService.AniListQuery(list);

            Assert.That(query, Is.Not.Null);
            Assert.That(query.Value.Sort, Is.EqualTo(sort));
            Assert.That(query.Value.Status, Is.EqualTo(status));
        }

        [Test]
        public void comics_list_is_not_an_aniList_query()
        {
            Assert.That(ComicDiscoverService.AniListQuery("comics"), Is.Null);
        }

        [Test]
        public void parse_page_keeps_order_and_drops_novels_and_adult_entries()
        {
            var media = AniListClient.ParsePage(JToken.Parse(BrowsePage));

            Assert.That(media.Select(m => m.Id), Is.EqualTo(new[] { 30013, 105398 }));
            Assert.That(media[0].Authors, Is.EqualTo(new[] { "Eiichiro Oda" }));
            Assert.That(media[0].CoverUrl, Is.EqualTo("https://img.example/op-xl.jpg"));
            Assert.That(media[1].CoverUrl, Is.EqualTo("https://img.example/sl-l.jpg"));
        }

        [Test]
        public void parse_page_handles_missing_data()
        {
            Assert.That(AniListClient.ParsePage(null), Is.Empty);
            Assert.That(AniListClient.ParsePage(JToken.Parse("{}")), Is.Empty);
        }

        [Test]
        public void aniList_media_maps_like_lookup()
        {
            var media = AniListClient.ParsePage(JToken.Parse(BrowsePage));
            var library = new List<ComicSeries> { new ComicSeries { Id = 7, AniListId = 105398 } };

            var onePiece = ComicSeriesService.FromAniList(media[0], null, library);
            Assert.That(onePiece.AniListId, Is.EqualTo(30013));
            Assert.That(onePiece.HardcoverSeriesId, Is.Null);
            Assert.That(onePiece.Kind, Is.EqualTo(ComicKinds.Manga));
            Assert.That(onePiece.Title, Is.EqualTo("One Piece"));
            Assert.That(onePiece.AlternateTitles, Does.Contain("OP"));
            Assert.That(onePiece.AlternateTitles, Does.Not.Contain("One Piece"));
            Assert.That(onePiece.Year, Is.EqualTo(1997));
            Assert.That(onePiece.Status, Is.EqualTo("Ongoing"));
            Assert.That(onePiece.Volumes, Is.Null);
            Assert.That(onePiece.Authors, Is.EqualTo("Eiichiro Oda"));
            Assert.That(onePiece.Overview, Is.EqualTo("Gol D. Roger\nwas known."));
            Assert.That(onePiece.CoverUrl, Is.EqualTo("https://img.example/op-xl.jpg"));
            Assert.That(onePiece.AgeRating, Is.EqualTo(ComicAgeRatings.FromAniList(false, media[0].Genres, media[0].Tags)));
            Assert.That(onePiece.IsAdult, Is.False);
            Assert.That(onePiece.Format, Is.EqualTo("MANGA"));
            Assert.That(onePiece.SeriesId, Is.Null);

            var soloLeveling = ComicSeriesService.FromAniList(media[1], null, library);
            Assert.That(soloLeveling.Status, Is.EqualTo("Finished"));
            Assert.That(soloLeveling.Volumes, Is.EqualTo(14));
            Assert.That(soloLeveling.SeriesId, Is.EqualTo(7));
        }

        [Test]
        public void curated_pages_split_twenty_and_the_rest()
        {
            var first = ComicDiscoverService.CuratedPage(1);
            var second = ComicDiscoverService.CuratedPage(2);

            Assert.That(first, Has.Count.EqualTo(20));
            Assert.That(second, Has.Count.EqualTo(ComicDiscoverService.CuratedComics.Count - 20));
            Assert.That(first.Intersect(second), Is.Empty);
            Assert.That(ComicDiscoverService.CuratedPage(3), Is.Empty);
            Assert.That(ComicDiscoverService.CuratedComics, Is.Unique);
        }

        [Test]
        public void curated_pick_prefers_the_series_with_most_books()
        {
            var hits = new[]
            {
                Hit(1, "Saga", 2, 2),
                Hit(2, "Saga", 12, 11),
                Hit(3, "Saga of the Swamp Thing", 40, 40)
            };

            Assert.That(ComicDiscoverService.PickCuratedHit("Saga", hits).Id, Is.EqualTo(2));
        }

        [Test]
        public void curated_pick_accepts_a_longer_name_that_starts_with_the_title()
        {
            var hits = new[] { Hit(5, "Hellboy (Dark Horse)", 13, 12), Hit(6, "Hellboyz", 99, 99) };

            Assert.That(ComicDiscoverService.PickCuratedHit("Hellboy", hits).Id, Is.EqualTo(5));
        }

        [Test]
        public void curated_pick_ignores_the_and_punctuation()
        {
            var hits = new[] { Hit(8, "Sandman", 10, 10), Hit(9, "Locke and Key", 6, 6) };

            Assert.That(ComicDiscoverService.PickCuratedHit("The Sandman", hits).Id, Is.EqualTo(8));
            Assert.That(ComicDiscoverService.PickCuratedHit("Locke & Key", hits).Id, Is.EqualTo(9));
        }

        [Test]
        public void curated_pick_skips_translations_reference_books_box_sets_and_empty_series()
        {
            var hits = new[]
            {
                Hit(1, "Watchmen (French)", 30, 30),
                Hit(2, "Watchmen Encyclopedia", 20, 20),
                Hit(3, "Watchmen Box Set", 15, 15),
                Hit(4, "ウォッチメン", 50, 50),
                Hit(5, "Watchmen Deluxe", 0, 0),
                Hit(6, "Watchmen", 3, 1)
            };

            Assert.That(ComicDiscoverService.PickCuratedHit("Watchmen", hits).Id, Is.EqualTo(6));
        }

        [Test]
        public void curated_pick_returns_null_without_a_good_hit()
        {
            Assert.That(ComicDiscoverService.PickCuratedHit("Maus", new[] { Hit(1, "Mausoleum", 4, 4), Hit(2, "Art Spiegelman", 3, 3) }), Is.Null);
            Assert.That(ComicDiscoverService.PickCuratedHit("Maus", null), Is.Null);
        }

        [Test]
        public void unknown_list_is_rejected()
        {
            var service = Service(new FakeAniList(), new FakeHardcover());

            Action act = () => service.Discover("seasonal", 1);

            var ex = Assert.Throws<ArgumentException>(() => act());
            Assert.That(ex.Message, Does.Contain("trending, popular, releasing, comics"));
        }

        [Test]
        public void aniList_pages_are_cached_and_one_request_per_page()
        {
            var aniList = new FakeAniList { Page = AniListClient.ParsePage(JToken.Parse(BrowsePage)) };
            var service = Service(aniList, new FakeHardcover());

            Assert.That(service.Discover("Trending", 1).Results, Has.Count.EqualTo(2));
            Assert.That(service.Discover("trending", 1).Results, Has.Count.EqualTo(2));
            service.Discover("trending", 2);

            Assert.That(aniList.Calls, Is.EqualTo(new List<(string, string, int, int)> { ("TRENDING_DESC", null, 1, 20), ("TRENDING_DESC", null, 2, 20) }));
        }

        [Test]
        public void aniList_failure_gives_a_notice_and_no_results()
        {
            var service = Service(new FakeAniList { Error = new AniListException("AniList is unavailable (403)") }, new FakeHardcover());

            var response = service.Discover("popular", 1);

            Assert.That(response.Results, Is.Empty);
            Assert.That(response.Notices, Has.Count.EqualTo(1));
            Assert.That(response.Notices[0], Does.Contain("AniList"));
        }

        [Test]
        public void comics_resolve_each_title_once_and_stop_when_rate_limited()
        {
            var hardcover = new FakeHardcover { FailAfter = 3 };
            var service = Service(new FakeAniList(), hardcover);

            var first = service.Discover("comics", 1);
            Assert.That(first.Results, Has.Count.EqualTo(3));
            Assert.That(first.Results.All(r => r.Kind == ComicKinds.Comic && r.HardcoverSeriesId.HasValue), Is.True);
            Assert.That(first.Notices, Has.Count.EqualTo(1));
            Assert.That(first.Notices[0], Does.Contain("rate limiting"));

            hardcover.FailAfter = int.MaxValue;
            var again = service.Discover("comics", 1);
            Assert.That(again.Results, Has.Count.EqualTo(20));
            Assert.That(again.Notices, Is.Empty);

            // 3 resolved, 1 failed, then the other 17: the first three were not searched again.
            Assert.That(hardcover.Searches, Has.Count.EqualTo(21));
            Assert.That(hardcover.Searches.Take(3), Is.Unique);
            Assert.That(hardcover.Searches.Skip(4).Intersect(hardcover.Searches.Take(3)), Is.Empty);
        }

        [Test]
        public void comics_need_a_hardcover_token()
        {
            var service = Service(new FakeAniList(), new FakeHardcover { Configured = false });

            var response = service.Discover("comics", 1);

            Assert.That(response.Results, Is.Empty);
            Assert.That(response.Notices, Has.Count.EqualTo(1));
            Assert.That(response.Notices[0], Does.Contain("Hardcover API token"));
        }

        private static HardcoverComicSeries Hit(int id, string name, int books, int primary)
        {
            return new HardcoverComicSeries { Id = id, Name = name, BooksCount = books, PrimaryBooksCount = primary };
        }

        private static ComicDiscoverService Service(IAniListClient aniList, IComicHardcoverClient hardcover)
        {
            var store = DispatchProxy.Create<ITalearrComicsStore, EmptyStore>();
            return new ComicDiscoverService(aniList, hardcover, store, new CacheManager(), LogManager.GetCurrentClassLogger());
        }

        private class FakeAniList : IAniListClient
        {
            public List<AniListManga> Page { get; set; } = new List<AniListManga>();
            public Exception Error { get; set; }
            public List<(string, string, int, int)> Calls { get; } = new List<(string, string, int, int)>();

            public List<AniListManga> Search(string query) => throw new NotSupportedException();

            public AniListManga Get(int id) => throw new NotSupportedException();

            public AniListManga GetPreview(int id) => throw new NotSupportedException();

            public List<AniListManga> Browse(string sort, string status, int page, int perPage, int? minPopularity = null)
            {
                Calls.Add((sort, status, page, perPage));
                if (Error != null)
                {
                    throw Error;
                }

                return Page;
            }
        }

        private class FakeHardcover : IComicHardcoverClient
        {
            public bool Configured { get; set; } = true;
            public int FailAfter { get; set; } = int.MaxValue;
            public List<string> Searches { get; } = new List<string>();

            public bool IsConfigured => Configured;

            public HardcoverComicSeries GetSeries(int id) => throw new NotSupportedException();

            public bool FillCovers(IList<HardcoverComicSeries> series)
            {
                return true;
            }

            public List<HardcoverComicSeries> SearchSeries(string query, bool withCovers = true)
            {
                Searches.Add(query);
                if (Searches.Count > FailAfter)
                {
                    FailAfter = int.MaxValue - 1;
                    throw new ComicHardcoverException("Hardcover answered 429");
                }

                return new List<HardcoverComicSeries> { Hit(Searches.Count, query, 5, 5) };
            }
        }

        // The store only has to answer GetAllSeries (an empty library) here.
        public class EmptyStore : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ITalearrComicsStore.GetAllSeries))
                {
                    return new List<ComicSeries>();
                }

                throw new NotSupportedException(targetMethod.Name);
            }
        }

        [Test]
        public void should_leave_unset_filters_out_of_the_browse_query()
        {
            var popular = AniListClient.BrowseQuery(null, null);
            var releasing = AniListClient.BrowseQuery("RELEASING", null);
            var trending = AniListClient.BrowseQuery(null, 5000);

            Assert.That(popular, Does.Not.Contain("status: "));
            Assert.That(popular, Does.Not.Contain("popularity_greater"));
            Assert.That(releasing, Does.Contain("status: RELEASING"));
            Assert.That(trending, Does.Contain("popularity_greater: 5000"));
        }
    }
}
