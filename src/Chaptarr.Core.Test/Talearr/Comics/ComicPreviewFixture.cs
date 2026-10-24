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
    public class ComicPreviewFixture
    {
        private const string PreviewMedia = @"{
            ""id"": 30002,
            ""title"": { ""romaji"": ""Berserk"", ""english"": ""Berserk"", ""native"": ""ベルセルク"" },
            ""synonyms"": [ ""Berserk: The Prototype"" ],
            ""format"": ""MANGA"",
            ""status"": ""RELEASING"",
            ""volumes"": 42,
            ""chapters"": null,
            ""isAdult"": false,
            ""genres"": [ ""Action"", ""Drama"", ""Horror"" ],
            ""countryOfOrigin"": ""JP"",
            ""startDate"": { ""year"": 1989 },
            ""coverImage"": { ""extraLarge"": ""https://img.example/b-xl.jpg"", ""large"": ""https://img.example/b-l.jpg"" },
            ""bannerImage"": ""https://img.example/b-banner.jpg"",
            ""description"": ""<i>Guts</i>, a former mercenary,<br>\n<br>\nwanders &amp; fights.<br><br>(Source: Dark Horse)"",
            ""tags"": [ { ""name"": ""Gore"", ""rank"": 90, ""isAdult"": false } ],
            ""staff"": { ""edges"": [
                { ""role"": ""Story & Art"", ""node"": { ""name"": { ""full"": ""Kentarou Miura"" } } },
                { ""role"": ""Assistant"", ""node"": { ""name"": { ""full"": ""Someone Else"" } } }
            ] }
        }";

        [Test]
        public void preview_media_parses_banner_and_facts()
        {
            var manga = AniListClient.ParseMedia(JObject.Parse(PreviewMedia));

            Assert.That(manga.Id, Is.EqualTo(30002));
            Assert.That(manga.BannerUrl, Is.EqualTo("https://img.example/b-banner.jpg"));
            Assert.That(manga.CoverUrl, Is.EqualTo("https://img.example/b-xl.jpg"));
            Assert.That(manga.Volumes, Is.EqualTo(42));
            Assert.That(manga.Genres, Is.EqualTo(new[] { "Action", "Drama", "Horror" }));
            Assert.That(manga.Authors, Is.EqualTo(new[] { "Kentarou Miura" }));
            Assert.That(manga.Description, Is.EqualTo("Guts, a former mercenary,\n\nwanders & fights."));
        }

        [TestCase("Line one<br>Line two", "Line one\nLine two")]
        [TestCase("<i>Italic</i> and <b>bold</b>", "Italic and bold")]
        [TestCase("A<br/><br />\n<br>\nB", "A\n\nB")]
        [TestCase("Quote &quot;here&quot; (Source: Viz Media)", "Quote \"here\"")]
        [TestCase("<br><br>", null)]
        [TestCase("   ", null)]
        [TestCase(null, null)]
        public void description_is_plain_text(string html, string expected)
        {
            Assert.That(AniListClient.CleanDescription(html), Is.EqualTo(expected));
        }

        [Test]
        public void aniList_volumes_win_the_count()
        {
            Assert.That(ComicPreviewService.VolumeCount(42, Series(10, 5, 3)), Is.EqualTo(42));
        }

        [Test]
        public void hardcover_primary_count_is_next()
        {
            Assert.That(ComicPreviewService.VolumeCount(null, Series(10, 7, 3)), Is.EqualTo(7));
            Assert.That(ComicPreviewService.VolumeCount(0, Series(10, 7, 3)), Is.EqualTo(7));
        }

        [Test]
        public void volume_list_length_is_last()
        {
            Assert.That(ComicPreviewService.VolumeCount(null, Series(10, 0, 3)), Is.EqualTo(3));
            Assert.That(ComicPreviewService.VolumeCount(null, Series(10, 0, 0)), Is.Null);
            Assert.That(ComicPreviewService.VolumeCount(null, null), Is.Null);
        }

        [Test]
        public void aniList_maps_like_lookup()
        {
            var preview = ComicPreviewService.MapAniList(AniListClient.ParseMedia(JObject.Parse(PreviewMedia)));

            Assert.That(preview.AniListId, Is.EqualTo(30002));
            Assert.That(preview.Kind, Is.EqualTo("manga"));
            Assert.That(preview.Title, Is.EqualTo("Berserk"));
            Assert.That(preview.AlternateTitles, Is.EqualTo(new[] { "Berserk: The Prototype" }));
            Assert.That(preview.Status, Is.EqualTo("Ongoing"));
            Assert.That(preview.Year, Is.EqualTo(1989));
            Assert.That(preview.Format, Is.EqualTo("MANGA"));
            Assert.That(preview.Authors, Is.EqualTo("Kentarou Miura"));
            Assert.That(preview.BannerUrl, Is.EqualTo("https://img.example/b-banner.jpg"));
            Assert.That(preview.VolumeCount, Is.EqualTo(42));
            Assert.That(preview.IsAdult, Is.False);
            Assert.That(preview.AgeRating, Is.EqualTo(ComicAgeRatings.FromAniList(false, new List<string> { "Action", "Drama", "Horror" }, new List<(string, int, bool)> { ("Gore", 90, false) })));
            Assert.That(preview.Overview, Does.Not.Contain("<"));
        }

        [Test]
        public void hardcover_maps_volumes_and_dates()
        {
            var series = Series(10, 2, 0);
            series.Name = "Saga";
            series.Description = "Star-crossed <b>lovers</b>.";
            series.AuthorName = "Brian K. Vaughan";
            series.CoverUrl = "https://img.example/saga.jpg";
            series.Volumes.Add(new HardcoverComicVolume { BookId = 1, Number = 1, Title = "Saga, Volume 1", ReleaseDate = new DateTime(2012, 10, 10), CoverUrl = "https://img.example/s1.jpg", Pages = 160, Description = "Volume <i>one</i>.", Slug = "saga-volume-1" });
            series.Volumes.Add(new HardcoverComicVolume { BookId = 2, Number = 1.5m, Title = "", ReleaseDate = null });

            var preview = ComicPreviewService.MapHardcover(series, "comic");

            Assert.That(preview.HardcoverSeriesId, Is.EqualTo(series.Id));
            Assert.That(preview.AniListId, Is.Null);
            Assert.That(preview.Kind, Is.EqualTo("comic"));
            Assert.That(preview.Overview, Is.EqualTo("Star-crossed lovers."));
            Assert.That(preview.Authors, Is.EqualTo("Brian K. Vaughan"));
            Assert.That(preview.AgeRating, Is.EqualTo(ComicAgeRatings.Unknown));
            Assert.That(preview.Year, Is.EqualTo(2012));
            Assert.That(preview.VolumeCount, Is.EqualTo(2));
            Assert.That(preview.Volumes, Has.Count.EqualTo(2));
            Assert.That(preview.Volumes[0].ReleaseDate, Is.EqualTo("2012-10-10"));
            Assert.That(preview.Volumes[0].CoverUrl, Is.EqualTo("https://img.example/s1.jpg"));
            Assert.That(preview.Volumes[0].Pages, Is.EqualTo(160));
            Assert.That(preview.Volumes[0].Description, Is.EqualTo("Volume one."));
            Assert.That(preview.Volumes[0].HardcoverUrl, Is.EqualTo("https://hardcover.app/books/saga-volume-1"));
            Assert.That(preview.Volumes[1].Number, Is.EqualTo(1.5m));
            Assert.That(preview.Volumes[1].Pages, Is.Null);
            Assert.That(preview.Volumes[1].HardcoverUrl, Is.Null);
            Assert.That(preview.Volumes[1].Title, Is.Null);
            Assert.That(preview.Volumes[1].ReleaseDate, Is.Null);
        }

        [TestCase(null, "comic")]
        [TestCase("comic", "comic")]
        [TestCase("manga", "manga")]
        [TestCase("MANGA", "manga")]
        public void hardcover_kind_defaults_to_comic(string kind, string expected)
        {
            Assert.That(ComicPreviewService.HardcoverKind(kind), Is.EqualTo(expected));
        }

        [Test]
        public void hardcover_match_takes_same_title_with_most_volumes()
        {
            var hits = new[]
            {
                Named(1, "Berserk Deluxe Edition", 14),
                Named(2, "Berserk", 20),
                Named(3, "Berserk", 41),
                Named(4, "Berserker", 50)
            };

            Assert.That(ComicPreviewService.PickHardcoverMatch("Berserk", hits).Id, Is.EqualTo(3));
            Assert.That(ComicPreviewService.PickHardcoverMatch("Vagabond", hits), Is.Null);
        }

        [Test]
        public void aniList_preview_pairs_hardcover_volumes()
        {
            var hardcover = new FakeHardcover();
            var service = Service(new FakeAniList(), hardcover);

            var preview = service.Preview(30002, null, null);

            Assert.That(preview.HardcoverSeriesId, Is.EqualTo(3));
            Assert.That(preview.Volumes.Select(v => v.Number), Is.EqualTo(new[] { 1m, 2m }));
            Assert.That(preview.VolumeCount, Is.EqualTo(42));
            Assert.That(preview.Notices, Is.Empty);
            Assert.That(hardcover.Searches.First(), Is.EqualTo("Berserk"));
        }

        [Test]
        public void preview_is_cached()
        {
            var aniList = new FakeAniList();
            var service = Service(aniList, new FakeHardcover());

            service.Preview(30002, null, null);
            service.Preview(30002, null, null);

            Assert.That(aniList.Calls, Is.EqualTo(1));
        }

        [Test]
        public void missing_hardcover_token_keeps_aniList_facts_with_notice()
        {
            var service = Service(new FakeAniList(), new FakeHardcover { Configured = false });

            var preview = service.Preview(30002, null, null);

            Assert.That(preview.Title, Is.EqualTo("Berserk"));
            Assert.That(preview.Volumes, Is.Empty);
            Assert.That(preview.VolumeCount, Is.EqualTo(42));
            Assert.That(preview.Notices.Single(), Does.Contain("Hardcover API token"));
        }

        [Test]
        public void hardcover_failure_is_a_notice_and_not_cached()
        {
            var hardcover = new FakeHardcover { Error = new ComicHardcoverException("Hardcover answered 500") };
            var aniList = new FakeAniList();
            var service = Service(aniList, hardcover);

            var preview = service.Preview(30002, null, null);
            service.Preview(30002, null, null);

            Assert.That(preview.Title, Is.EqualTo("Berserk"));
            Assert.That(preview.Notices.Single(), Does.Contain("Hardcover could not be reached"));
            Assert.That(aniList.Calls, Is.EqualTo(2));
        }

        [Test]
        public void unknown_aniList_id_is_not_found()
        {
            var service = Service(new FakeAniList(), new FakeHardcover());

            Assert.Throws<ComicPreviewNotFoundException>(() => service.Preview(1, null, null));
        }

        [Test]
        public void unknown_hardcover_id_is_not_found()
        {
            var service = Service(new FakeAniList(), new FakeHardcover());

            Assert.Throws<ComicPreviewNotFoundException>(() => service.Preview(null, 999, null));
        }

        [Test]
        public void hardcover_id_preview_uses_the_series()
        {
            var service = Service(new FakeAniList(), new FakeHardcover());

            var preview = service.Preview(null, 3, "manga");

            Assert.That(preview.Kind, Is.EqualTo("manga"));
            Assert.That(preview.Title, Is.EqualTo("Berserk"));
            Assert.That(preview.Volumes, Has.Count.EqualTo(2));
            Assert.That(preview.AgeRating, Is.EqualTo(ComicAgeRatings.Unknown));
        }

        [Test]
        public void neither_id_is_an_argument_error()
        {
            var service = Service(new FakeAniList(), new FakeHardcover());

            Assert.Throws<ArgumentException>(() => service.Preview(null, null, null));
        }

        private static HardcoverComicSeries Series(int books, int primary, int volumes)
        {
            var series = new HardcoverComicSeries { Id = 77, Name = "Series", BooksCount = books, PrimaryBooksCount = primary };
            for (var i = 1; i <= volumes; i++)
            {
                series.Volumes.Add(new HardcoverComicVolume { BookId = i, Number = i });
            }

            return series;
        }

        private static HardcoverComicSeries Named(int id, string name, int primary)
        {
            return new HardcoverComicSeries { Id = id, Name = name, BooksCount = primary, PrimaryBooksCount = primary };
        }

        private static ComicPreviewService Service(IAniListClient aniList, IComicHardcoverClient hardcover)
        {
            var store = DispatchProxy.Create<ITalearrComicsStore, ComicDiscoverFixture.EmptyStore>();
            return new ComicPreviewService(aniList, hardcover, store, new CacheManager(), LogManager.GetCurrentClassLogger());
        }

        private class FakeAniList : IAniListClient
        {
            public int Calls { get; private set; }

            public List<AniListManga> Search(string query) => throw new NotSupportedException();

            public AniListManga Get(int id) => throw new NotSupportedException();

            public List<AniListManga> Browse(string sort, string status, int page, int perPage, int? minPopularity = null) => throw new NotSupportedException();

            public AniListManga GetPreview(int id)
            {
                Calls++;
                return id == 30002 ? AniListClient.ParseMedia(JObject.Parse(PreviewMedia)) : null;
            }
        }

        private class FakeHardcover : IComicHardcoverClient
        {
            public bool Configured { get; set; } = true;
            public Exception Error { get; set; }
            public List<string> Searches { get; } = new List<string>();

            public bool IsConfigured => Configured;

            public bool FillCovers(IList<HardcoverComicSeries> series)
            {
                return true;
            }

            public List<HardcoverComicSeries> SearchSeries(string query, bool withCovers = true)
            {
                Searches.Add(query);
                if (Error != null)
                {
                    throw Error;
                }

                return new List<HardcoverComicSeries> { Named(2, "Berserk", 20), Named(3, "Berserk", 41) };
            }

            public HardcoverComicSeries GetSeries(int id)
            {
                if (Error != null)
                {
                    throw Error;
                }

                if (id != 3)
                {
                    return null;
                }

                var series = Named(3, "Berserk", 41);
                series.Volumes.Add(new HardcoverComicVolume { BookId = 10, Number = 1, Title = "Berserk, Vol. 1", ReleaseDate = new DateTime(2003, 7, 1) });
                series.Volumes.Add(new HardcoverComicVolume { BookId = 11, Number = 2, Title = "Berserk, Vol. 2" });
                return series;
            }
        }
    }
}
