using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Linq;
using Chaptarr.Core.Test.MediaFiles.TalearrImport;
using Chaptarr.Core.Test.Talearr.Recordings;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Import;
using NzbDrone.Core.Talearr.Comics.Metadata;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicImportFixture
    {
        private static readonly byte[] Page = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8 };

        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "talearr-comics-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(_folder, true);
            }
            catch (IOException)
            {
            }
        }

        private static ComicSeries Series()
        {
            return new ComicSeries
            {
                Id = 1,
                Kind = ComicKinds.Manga,
                Title = "Witch Hat Atelier",
                Year = 2019,
                TotalVolumes = 14,
                Overview = "Coco wants to be a witch.",
                Authors = "Kamome Shirahama",
                AgeRating = ComicAgeRatings.Teen,
                ReadingDirection = "rtl",
                LanguageIso = "en",
                AniListId = 100664
            };
        }

        [Test]
        public void should_rewrite_a_zip_as_a_clean_cbz_with_comicinfo_first()
        {
            var source = Path.Combine(_folder, "in.cbr");
            File.WriteAllBytes(source, TestArchives.Zip(
                ("Witch Hat Atelier v01/010.jpg", Page),
                ("Witch Hat Atelier v01/002.jpg", Page),
                ("Witch Hat Atelier v01/Thumbs.db", Page),
                ("../escape.png", Page),
                ("ComicInfo.xml", TestArchives.Text("<ComicInfo><AgeRating>Adults Only 18+</AgeRating></ComicInfo>"))));

            var target = Path.Combine(_folder, "out.cbz");
            var pages = ComicArchive.WriteCbz(source, target, ComicInfoBuilder.Build(Series(), new ComicVolume { Number = 1 }, 3));

            Assert.That(pages, Is.EqualTo(3));
            using var zip = ZipFile.OpenRead(target);
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.That(names, Is.EqualTo(new[] { "ComicInfo.xml", "escape.png", "Witch Hat Atelier v01/002.jpg", "Witch Hat Atelier v01/010.jpg" }));

            using var reader = new StreamReader(zip.GetEntry("ComicInfo.xml").Open());
            Assert.That(ComicInfoBuilder.ReadAgeRating(reader.ReadToEnd()), Is.EqualTo(ComicAgeRatings.Teen), "the release's own ComicInfo.xml is replaced");
            Assert.That(File.Exists(target + ".talearr-tmp"), Is.False);
        }

        [Test]
        public void should_convert_cb7_to_cbz()
        {
            var source = Path.Combine(_folder, "in.cb7");
            File.WriteAllBytes(source, TestArchives.SevenZip(("p1.jpg", Page), ("p2.jpg", Page), ("notes.txt", TestArchives.Text("hi"))));

            var target = Path.Combine(_folder, "out.cbz");
            var pages = ComicArchive.WriteCbz(source, target, null);

            Assert.That(pages, Is.EqualTo(2));
            Assert.That(ComicArchive.IsZip(target), Is.True);
        }

        [Test]
        public void should_refuse_an_archive_without_pages_and_leave_nothing_behind()
        {
            var source = Path.Combine(_folder, "in.cbz");
            File.WriteAllBytes(source, TestArchives.Zip(("readme.txt", TestArchives.Text("no pages here"))));
            var target = Path.Combine(_folder, "out.cbz");

            Assert.Throws<ComicArchiveException>(() => ComicArchive.WriteCbz(source, target, null));
            Assert.That(File.Exists(target), Is.False);
            Assert.That(File.Exists(target + ".talearr-tmp"), Is.False);
        }

        [Test]
        public void should_write_comicinfo_for_a_manga_volume()
        {
            var xml = ComicInfoBuilder.Build(Series(), new ComicVolume { Number = 3, Title = "Witch Hat Atelier, Vol. 3", ReleaseDate = new DateTime(2019, 10, 15) }, 192);

            Assert.That(xml, Does.Contain("<Series>Witch Hat Atelier</Series>"));
            Assert.That(xml, Does.Contain("<Volume>3</Volume>"));
            Assert.That(xml, Does.Not.Contain("<Number>"));
            Assert.That(xml, Does.Contain("<Manga>YesAndRightToLeft</Manga>"));
            Assert.That(xml, Does.Contain("<AgeRating>Teen</AgeRating>"));
            Assert.That(xml, Does.Contain("<Count>14</Count>"));
            Assert.That(xml, Does.Contain("<Year>2019</Year>"));
            Assert.That(xml, Does.Contain("<PageCount>192</PageCount>"));
        }

        [Test]
        public void should_name_folders_and_files()
        {
            var settings = new ComicSettings();
            var manga = Series();
            var volume = new ComicVolume { Number = 3, ReleaseDate = new DateTime(2019, 10, 15) };

            Assert.That(ComicNaming.SeriesFolder(manga, settings), Is.EqualTo("Witch Hat Atelier"));
            Assert.That(ComicNaming.FileName(manga, volume, settings), Is.EqualTo("Witch Hat Atelier v03 (2019)"));
            Assert.That(ComicNaming.FileName(manga, new ComicVolume { Number = 10.5m }, settings), Is.EqualTo("Witch Hat Atelier v10.5 (2019)"));

            var comic = new ComicSeries { Kind = ComicKinds.Comic, Title = "Saga", Year = 2012 };
            Assert.That(ComicNaming.SeriesFolder(comic, settings), Is.EqualTo("Saga (2012)"));

            var noYear = new ComicSeries { Kind = ComicKinds.Comic, Title = "Saga: Book One" };
            Assert.That(ComicNaming.SeriesFolder(noYear, settings), Does.Not.Contain("()"));
        }

        [Test]
        public void should_pick_one_english_volume_per_position_from_hardcover()
        {
            var json = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Talearr", "Comics", "Fixtures", "hardcover-series-witch-hat-atelier.json"));
            var series = ComicHardcoverClient.ParseSeries(JToken.Parse(json));

            Assert.That(series.Name, Is.EqualTo("Witch Hat Atelier"));
            Assert.That(series.AuthorName, Is.EqualTo("Kamome Shirahama"));
            Assert.That(series.Volumes.Select(v => v.Number).Take(15), Is.EqualTo(Enumerable.Range(1, 15).Select(n => (decimal)n)));
            Assert.That(series.Volumes.Where(v => v.Number <= 15).Select(v => v.Title), Has.All.StartsWith("Witch Hat Atelier, Vol."));
            Assert.That(series.Volumes.First().ReleaseDate, Is.EqualTo(new DateTime(2017, 1, 1)));
        }

        [Test]
        public void should_read_hardcover_search_hits_with_title_lists()
        {
            var results = JToken.Parse(@"{ ""hits"": [ { ""document"": { ""id"": ""62928"", ""name"": ""Witch Hat Atelier"", ""books_count"": 30, ""primary_books_count"": 16,
                ""author_name"": ""Kamome Shirahama"", ""books"": [""Witch Hat Atelier, Vol. 1"", ""Witch Hat Atelier, Vol. 2""] } } ] }");

            var hits = ComicHardcoverClient.ParseSearch(results);
            var asString = ComicHardcoverClient.ParseSearch(new JValue(results.ToString()));

            Assert.That(hits.Single().Id, Is.EqualTo(62928));
            Assert.That(hits.Single().PrimaryBooksCount, Is.EqualTo(16));
            Assert.That(asString.Single().Name, Is.EqualTo("Witch Hat Atelier"));
        }

        [Test]
        public void should_read_anilist_media()
        {
            var media = JToken.Parse(@"{ ""id"": 100664, ""title"": { ""romaji"": ""Tongari Boushi no Atelier"", ""english"": ""Witch Hat Atelier"", ""native"": ""とんがり帽子のアトリエ"" },
                ""synonyms"": [""Atelier of Witch Hat""], ""format"": ""MANGA"", ""status"": ""RELEASING"", ""volumes"": null, ""isAdult"": false,
                ""genres"": [""Adventure"", ""Fantasy""], ""countryOfOrigin"": ""JP"", ""startDate"": { ""year"": 2016 },
                ""coverImage"": { ""extraLarge"": ""https://img/x.jpg"" }, ""description"": ""Coco<br>wants to be a witch. (Source: Kodansha)"",
                ""tags"": [ { ""name"": ""Seinen"", ""rank"": 90, ""isAdult"": false } ],
                ""staff"": { ""edges"": [ { ""role"": ""Story & Art"", ""node"": { ""name"": { ""full"": ""Kamome Shirahama"" } } } ] } }");

            var manga = AniListClient.ParseMedia(media);

            Assert.That(manga.DisplayTitle, Is.EqualTo("Witch Hat Atelier"));
            Assert.That(manga.Authors, Is.EqualTo(new[] { "Kamome Shirahama" }));
            Assert.That(manga.Description, Is.EqualTo("Coco\nwants to be a witch."));
            Assert.That(ComicAgeRatings.FromAniList(manga.IsAdult, manga.Genres, manga.Tags), Is.EqualTo(ComicAgeRatings.Mature17));
        }

        [Test]
        public void should_import_a_finished_download_into_the_series_folder()
        {
            var store = new TalearrComicsStore(new TalearrComicsDatabase(new TestAppFolderInfo(_folder), LogManager.GetLogger("TalearrTest")));
            var root = store.AddRootFolder(new ComicRootFolder { Path = Path.Combine(_folder, "manga"), Kind = ComicKinds.Manga });
            var series = Series();
            series.RootFolderId = root.Id;
            series.Path = Path.Combine(root.Path, "Witch Hat Atelier");
            series.Monitored = true;
            series = store.AddSeries(series);
            var volumes = store.MergeVolumes(series.Id, new[] { new ComicVolume { Number = 1, ReleaseDate = new DateTime(2019, 4, 9) }, new ComicVolume { Number = 2 } }, true);

            var downloads = Path.Combine(_folder, "downloads", "Witch Hat Atelier v01-v02 (Digital) (danke-Empire)");
            Directory.CreateDirectory(downloads);
            File.WriteAllBytes(Path.Combine(downloads, "Witch Hat Atelier v01 (2019) (Digital) (danke-Empire).cbz"), TestArchives.Zip(("001.jpg", Page), ("002.jpg", Page)));
            File.WriteAllBytes(Path.Combine(downloads, "Witch Hat Atelier v02 (2019) (Digital) (danke-Empire).cb7"), TestArchives.SevenZip(("001.jpg", Page)));
            File.WriteAllBytes(Path.Combine(downloads, "Witch Hat Atelier v09 (2021) (Digital) (danke-Empire).cbz"), TestArchives.Zip(("001.jpg", Page)));

            store.SaveDownload(new ComicDownload
            {
                DownloadId = "nzo_1",
                DownloadClientId = 1,
                Protocol = "usenet",
                SeriesId = series.Id,
                VolumeIds = string.Join(",", volumes.Select(v => v.Id)),
                ReleaseTitle = "Witch Hat Atelier v01-v02 (2019) (Digital) (danke-Empire)",
                Status = ComicDownloadStatuses.Completed,
                OutputPath = downloads,
                Score = 380
            });

            var notifier = new FakeNotifier();
            var service = new ComicImportService(store, new ComicSettingsService(store), notifier, new NoDownloadClients(), new LocalDisk(), LogManager.GetLogger("TalearrTest"));

            var result = service.ImportDownload("nzo_1");

            Assert.That(result.Imported, Is.EquivalentTo(new[] { "Witch Hat Atelier v01 (2019).cbz", "Witch Hat Atelier v02 (2019).cbz" }));
            Assert.That(result.Skipped.Single(), Does.Contain("v09"));
            Assert.That(File.Exists(Path.Combine(series.Path, "Witch Hat Atelier v01 (2019).cbz")), Is.True);
            Assert.That(File.Exists(Path.Combine(downloads, "Witch Hat Atelier v01 (2019) (Digital) (danke-Empire).cbz")), Is.True, "the download is left for seeding");
            Assert.That(store.GetFiles(series.Id).Select(f => f.Source).Distinct(), Is.EqualTo(new[] { ComicSources.Digital }));
            Assert.That(store.GetDownload("nzo_1").Status, Is.EqualTo(ComicDownloadStatuses.Imported));
            Assert.That(notifier.Folders, Is.EqualTo(new[] { series.Path }));

            // A rescan finds nothing new, and a deleted file is forgotten.
            File.Delete(Path.Combine(series.Path, "Witch Hat Atelier v02 (2019).cbz"));
            var rescan = service.RescanSeries(series.Id);
            Assert.That(rescan.Imported, Is.Empty);
            Assert.That(store.GetFiles(series.Id).Count, Is.EqualTo(1));
        }

        private sealed class FakeNotifier : IComicLibraryNotifier
        {
            public List<string> Folders { get; } = new List<string>();

            public void FolderChanged(ComicSeries series, string folder)
            {
                Folders.Add(folder);
            }
        }

        private sealed class NoDownloadClients : IProvideDownloadClient
        {
            public IDownloadClient GetDownloadClient(DownloadProtocol downloadProtocol, BookMediaType mediaType, int indexerId = 0, bool filterBlockedClients = false, HashSet<int> tags = null) => null;
            public IEnumerable<IDownloadClient> GetDownloadClients(bool filterBlockedClients = false) => Enumerable.Empty<IDownloadClient>();
            public IDownloadClient Get(int id) => null;
        }

        private sealed class LocalDisk : DiskProviderBase
        {
            public LocalDisk()
                : base(new FileSystem())
            {
            }

            public override long? GetAvailableSpace(string path) => null;
            public override void InheritFolderPermissions(string filename)
            {
            }

            public override void SetEveryonePermissions(string filename)
            {
            }

            public override void SetFilePermissions(string path, string mask, string group)
            {
            }

            public override void SetPermissions(string path, string mask, string group)
            {
            }

            public override void CopyPermissions(string sourcePath, string targetPath)
            {
            }

            public override long? GetTotalSize(string path) => null;
            public override bool TryCreateHardLink(string source, string destination) => false;
        }
    }
}
