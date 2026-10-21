using System;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Comics;
using Chaptarr.Core.Test.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class TalearrComicsDatabaseFixture
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "talearr-comics-" + Guid.NewGuid().ToString("N"));
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

        private TalearrComicsDatabase CreateDatabase()
        {
            return new TalearrComicsDatabase(new TestAppFolderInfo(_folder), LogManager.GetLogger("TalearrTest"));
        }

        [Test]
        public void should_create_its_own_file_and_never_chaptarr_db()
        {
            var database = CreateDatabase();

            using (var connection = database.OpenConnection())
            {
                var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name").ToList();
                Assert.That(tables, Is.EquivalentTo(new[] { "ComicBlocklist", "ComicDownloads", "ComicFiles", "ComicHistory", "ComicRootFolders", "ComicSeries", "ComicSettings", "ComicVolumes" }));
            }

            Assert.That(File.Exists(Path.Combine(_folder, TalearrComicsDatabase.FileName)), Is.True);
            Assert.That(File.Exists(Path.Combine(_folder, "chaptarr.db")), Is.False);
            Assert.That(database.SchemaVersion, Is.EqualTo(TalearrComicsDatabase.CurrentSchemaVersion));
        }

        [Test]
        public void should_keep_a_damaged_file_and_refuse_to_open_it()
        {
            var path = Path.Combine(_folder, TalearrComicsDatabase.FileName);
            File.WriteAllText(path, "this is not a sqlite database, but it is somebody's library");

            var database = CreateDatabase();

            Assert.Throws<TalearrComicsDatabaseUnavailableException>(() => database.OpenConnection().Dispose());
            Assert.That(database.UnavailableReason, Is.Not.Null);
            Assert.That(File.ReadAllText(path), Does.StartWith("this is not a sqlite database"), "a durable library is never deleted or recreated");
        }

        [Test]
        public void should_refuse_a_file_from_a_newer_talearr()
        {
            var database = CreateDatabase();
            using (var connection = database.OpenConnection())
            {
                connection.Execute($"PRAGMA user_version = {TalearrComicsDatabase.CurrentSchemaVersion + 1};");
            }

            var reopened = CreateDatabase();

            Assert.Throws<TalearrComicsDatabaseUnavailableException>(() => reopened.OpenConnection().Dispose());
            Assert.That(reopened.UnavailableReason, Does.Contain("newer Talearr"));
        }

        [Test]
        public void should_store_series_volumes_files_and_downloads()
        {
            var store = new TalearrComicsStore(CreateDatabase());

            var folder = store.AddRootFolder(new ComicRootFolder { Path = "/manga", Kind = ComicKinds.Manga, IsKids = true, MaxAgeRating = ComicAgeRatings.Teen });
            var series = store.AddSeries(new ComicSeries
            {
                Kind = ComicKinds.Manga,
                Title = "Witch Hat Atelier",
                SortTitle = "witch hat atelier",
                AniListId = 100664,
                RootFolderId = folder.Id,
                Path = "/manga/Witch Hat Atelier",
                Monitored = true,
                MonitorNewVolumes = true,
                AgeRating = ComicAgeRatings.Teen
            });

            var added = store.MergeVolumes(series.Id, new[]
            {
                new ComicVolume { Number = 1, Title = "Volume 1", ReleaseDate = new DateTime(2019, 4, 9, 0, 0, 0, DateTimeKind.Utc) },
                new ComicVolume { Number = 2 }
            }, newVolumesMonitored: true);

            Assert.That(added.Count, Is.EqualTo(2));

            store.SetVolumesMonitored(new[] { added[1].Id }, false);
            var again = store.MergeVolumes(series.Id, new[] { new ComicVolume { Number = 2, Title = "Volume 2" }, new ComicVolume { Number = 3 } }, newVolumesMonitored: true);

            Assert.That(again.Select(v => v.Number), Is.EqualTo(new[] { 3m }));

            var volumes = store.GetVolumes(series.Id);
            Assert.That(volumes.Select(v => v.Number), Is.EqualTo(new[] { 1m, 2m, 3m }));
            Assert.That(volumes[1].Monitored, Is.False, "a refresh keeps the user's monitoring");
            Assert.That(volumes[1].Title, Is.EqualTo("Volume 2"));
            Assert.That(volumes[0].ReleaseDate, Is.EqualTo(new DateTime(2019, 4, 9, 0, 0, 0, DateTimeKind.Utc)));

            store.AddFile(new ComicFile { SeriesId = series.Id, VolumeId = volumes[0].Id, Path = "/manga/Witch Hat Atelier/Witch Hat Atelier v01 (2019).cbz", Format = ComicFormats.Cbz, Source = ComicSources.Digital, Size = 300 });
            store.SaveDownload(new ComicDownload { DownloadId = "SABnzbd_nzo_1", DownloadClientId = 2, Protocol = "usenet", SeriesId = series.Id, VolumeIds = volumes[1].Id + "," + volumes[2].Id, ReleaseTitle = "Witch Hat Atelier v02-v03", Status = ComicDownloadStatuses.Grabbed });
            var saved = store.GetDownload("SABnzbd_nzo_1");
            saved.Status = ComicDownloadStatuses.Completed;
            saved.OutputPath = "/downloads/x";
            saved.ReleaseTitle = "changed by mistake";
            store.SaveDownload(saved);

            var download = store.GetDownload("SABnzbd_nzo_1");
            Assert.That(download.Status, Is.EqualTo(ComicDownloadStatuses.Completed));
            Assert.That(download.ReleaseTitle, Is.EqualTo("Witch Hat Atelier v02-v03"), "an update keeps what was grabbed");
            Assert.That(download.GetVolumeIds(), Is.EqualTo(new[] { volumes[1].Id, volumes[2].Id }));
            Assert.That(store.GetTrackedDownloadIds(DateTime.UtcNow.AddDays(-30)), Does.Contain("SABnzbd_nzo_1"));

            var root = store.GetRootFolder(folder.Id);
            Assert.That(root.IsKids, Is.True);
            Assert.That(root.MaxAgeRating, Is.EqualTo(ComicAgeRatings.Teen));

            store.DeleteSeries(series.Id);
            Assert.That(store.GetVolumes(series.Id), Is.Empty);
            Assert.That(store.GetFiles(series.Id), Is.Empty);
            Assert.That(store.GetDownload("SABnzbd_nzo_1"), Is.Null);
        }

        [Test]
        public void should_store_settings()
        {
            var service = new ComicSettingsService(new TalearrComicsStore(CreateDatabase()));

            var defaults = service.Get();
            Assert.That(defaults.Categories, Is.EqualTo(new[] { 7030 }));

            defaults.AllowC2C = false;
            defaults.Categories = new System.Collections.Generic.List<int> { 7030, 107030 };
            var saved = service.Save(defaults);

            Assert.That(saved.AllowC2C, Is.False);
            Assert.That(saved.Categories, Is.EqualTo(new[] { 7030, 107030 }));
            Assert.Throws<ArgumentException>(() => service.Save(new ComicSettings { FileFormat = "{Series Title}" }));
        }

        [TestCase(ComicAgeRatings.Everyone, ComicAgeRatings.Teen, true)]
        [TestCase(ComicAgeRatings.Mature17, ComicAgeRatings.Teen, false)]
        [TestCase(ComicAgeRatings.Unknown, ComicAgeRatings.Teen, false)]
        [TestCase(ComicAgeRatings.AdultsOnly, null, true)]
        public void should_compare_age_ratings(string rating, string max, bool allowed)
        {
            Assert.That(ComicAgeRatings.IsAllowedFor(rating, max), Is.EqualTo(allowed));
        }

        [Test]
        public void should_guess_age_ratings_from_anilist_facts()
        {
            Assert.That(ComicAgeRatings.FromAniList(true, null, null), Is.EqualTo(ComicAgeRatings.AdultsOnly));
            Assert.That(ComicAgeRatings.FromAniList(false, new[] { "Ecchi", "Comedy" }, null), Is.EqualTo(ComicAgeRatings.Mature17));
            Assert.That(ComicAgeRatings.FromAniList(false, new[] { "Fantasy" }, new[] { ("Shounen", 90, false) }), Is.EqualTo(ComicAgeRatings.Teen));
            Assert.That(ComicAgeRatings.FromAniList(false, new[] { "Comedy" }, new[] { ("Kids", 90, false) }), Is.EqualTo(ComicAgeRatings.Everyone));
            Assert.That(ComicAgeRatings.FromAniList(false, new[] { "Drama" }, new[] { ("Seinen", 90, false) }), Is.EqualTo(ComicAgeRatings.Mature17));
            Assert.That(ComicAgeRatings.FromAniList(false, new[] { "Drama" }, null), Is.EqualTo(ComicAgeRatings.Unknown));
        }
    }
}
