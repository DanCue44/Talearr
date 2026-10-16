using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // The guard measures both sides and feeds TalearrUpgradeRules; keep both reroutes to an
    // upstream wanted copy instead of replacing.
    [TestFixture]
    public class TalearrUpgradeGuardFixture
    {
        private RecordingsWorld _world;
        private Dictionary<string, MediaInfoModel> _media;
        private TalearrUpgradeGuard _guard;
        private string _downloads;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _media = new Dictionary<string, MediaInfoModel>(StringComparer.OrdinalIgnoreCase);
            var (extractor, fake) = InterfaceFake<IMediaInfoExtractor>.Create();
            fake.On(nameof(IMediaInfoExtractor.ExtractMediaInfo), args => _media.TryGetValue((string)args[0], out var info) ? info : new MediaInfoModel());

            _guard = new TalearrUpgradeGuard(extractor, _world.Resolver, _world.Recordings, _world.BookService, _world.EditionService, null, _world.Settings, LogManager.GetCurrentClassLogger());

            _downloads = Path.Combine(_world.Folder.Folder, "downloads");
            Directory.CreateDirectory(_downloads);
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        // The abridged D.W. Moffett reading is downloaded (6 h, 64 kbps AAC).
        private (Book Book, List<BookFile> Files) AbridgedFirm()
        {
            var book = _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");

            var file = _world.Files.Single();
            file.Quality = new QualityModel(Quality.M4B);
            file.MediaInfo = new MediaInfoModel { AudioFormat = "AAC", AudioBitrate = 64, AudioChannels = 2, Duration = TimeSpan.FromHours(6) };
            return (book, _world.Files.ToList());
        }

        private LocalBook Download(string folder, string name, int minutes, int kbps, string codec = "AAC", int channels = 2)
        {
            var dir = Path.Combine(_downloads, folder);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "audio");
            _media[path] = new MediaInfoModel { AudioFormat = codec, AudioBitrate = kbps, AudioChannels = channels, Duration = TimeSpan.FromMinutes(minutes) };

            return new LocalBook { Path = path, Quality = new QualityModel(Path.GetExtension(name) == ".mp3" ? Quality.MP3 : Quality.M4B) };
        }

        [Test]
        public void unabridged_release_for_abridged_files_is_rerouted_to_a_new_recording()
        {
            var (book, files) = AbridgedFirm();
            var incoming = Download("John Grisham - The Firm (Unabridged) [Scott Brick]", "The Firm.m4b", 1030, 64);

            var verdict = _guard.Evaluate(book, null, files, new[] { incoming }, new DownloadClientItem { Title = "John Grisham - The Firm (Unabridged) [Scott Brick]" }, null);
            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.KeepBoth));

            var destination = _guard.ResolveKeepBothDestination(book);
            Assert.Multiple(() =>
            {
                Assert.That(destination, Is.Not.Null);
                Assert.That(destination.Created, Is.True);
                Assert.That(destination.Book.Id, Is.Not.EqualTo(3401));
                Assert.That(destination.Book.TitleSlug, Is.EqualTo("the-firm_2_wanted_28701"), "an upstream wanted copy of the unabridged Scott Brick edition");
                Assert.That(destination.Edition.BookId, Is.EqualTo(destination.Book.Id));
                Assert.That(_world.SearchedBookIds, Is.Empty, "the download is already here, no search");
                Assert.That(_world.Deleted, Is.Empty, "the abridged recording is kept");
            });

            var again = _guard.ResolveKeepBothDestination(book);
            Assert.That(again.Book.Id, Is.EqualTo(destination.Book.Id));
            Assert.That(again.Created, Is.False, "an existing unabridged copy is reused");
        }

        [Test]
        public void with_keep_both_off_the_unabridged_release_is_rejected()
        {
            var (book, files) = AbridgedFirm();
            _world.Settings.Save(new TalearrRecordingsSettings { KeepBoth = false });
            var incoming = Download("The Firm Unabridged", "The Firm.m4b", 1030, 64);

            var verdict = _guard.Evaluate(book, null, files, new[] { incoming }, null, null);

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Reason, Does.StartWith("TaleArr: different recording"));
        }

        [Test]
        public void a_better_copy_of_the_same_recording_is_allowed()
        {
            var (book, files) = AbridgedFirm();
            var incoming = Download("The Firm Abridged 128k", "The Firm.m4b", 361, 128);

            Assert.That(_guard.Evaluate(book, null, files, new[] { incoming }, null, null).Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
        }

        [Test]
        public void measured_bitrate_from_disk_is_used_when_the_database_has_none()
        {
            var (book, files) = AbridgedFirm();

            // Chaptarr stores no bitrate for imported files; the guard reads the file itself.
            var onDisk = Path.Combine(_world.Folder.Folder, "library.m4b");
            File.WriteAllText(onDisk, "audio");
            _media[onDisk] = new MediaInfoModel { AudioFormat = "AAC", AudioBitrate = 128, AudioChannels = 2, Duration = TimeSpan.FromHours(6) };
            files[0].Path = onDisk;
            files[0].MediaInfo = new MediaInfoModel();

            var incoming = Download("The Firm Abridged", "The Firm.m4b", 360, 64);
            var verdict = _guard.Evaluate(book, null, files, new[] { incoming }, null, null);

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Rule, Is.EqualTo(TalearrUpgradeRules.RuleNeverWorse));
        }

        [Test]
        public void multi_file_downloads_are_summed_and_weighted()
        {
            var (book, files) = AbridgedFirm();
            var parts = Enumerable.Range(1, 3)
                .Select(i => Download("The Firm Abridged mp3", $"Part {i:00}.mp3", 120, 64, "MPEG Audio"))
                .ToList();

            var verdict = _guard.Evaluate(book, null, files, parts, null, null);

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject), "same length, same bitrate, different codec: not better");
            Assert.That(verdict.Reason, Does.Contain("same quality and no higher bitrate"));
        }

        [Test]
        public void the_guard_stays_out_of_the_way_when_it_does_not_apply()
        {
            var (book, files) = AbridgedFirm();
            var incoming = Download("The Firm (Unabridged)", "The Firm.m4b", 1030, 64);

            Assert.That(_guard.Evaluate(book, null, files, new[] { incoming }, new DownloadClientItem { DownloadForced = true }, null), Is.Null, "a forced grab overrides");

            incoming.IsManualImport = true;
            Assert.That(_guard.Evaluate(book, null, files, new[] { incoming }, null, null), Is.Null, "manual import overrides");
            incoming.IsManualImport = false;

            Assert.That(_guard.Evaluate(book, null, new List<BookFile>(), new[] { incoming }, null, null), Is.Null, "nothing to replace");
            Assert.That(_guard.Evaluate(_world.Books[3402], null, files, new[] { incoming }, null, null), Is.Null, "ebooks are not guarded");

            _world.Settings.Save(new TalearrRecordingsSettings { UpgradeGuard = false });
            Assert.That(_guard.Evaluate(book, null, files, new[] { incoming }, null, null), Is.Null, "guard off");
        }

        [TestCase("John Grisham - The Firm (Unabridged) 2009", AbridgedMarker.Unabridged)]
        [TestCase("The Firm [Abridged] 64k", AbridgedMarker.Abridged)]
        [TestCase("The Firm 64k", AbridgedMarker.Unknown)]
        public void incoming_abridged_status_comes_from_the_release(string title, AbridgedMarker expected)
        {
            Assert.That(TalearrUpgradeGuard.IncomingAbridged(new[] { new LocalBook { Path = "/downloads/x/The Firm.m4b" } }, new DownloadClientItem { Title = title }), Is.EqualTo(expected));
        }
    }
}
