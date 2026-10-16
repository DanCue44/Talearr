using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TalearrImport;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    [TestFixture]
    public class DownloadArchiveUnpackerFixture
    {
        private string _root;
        private string _download;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-tests", Guid.NewGuid().ToString("N"));
            _download = Path.Combine(_root, "Some.Author.Some.Book.2021.RETAiL.ePub.eBook-GRP");
            Directory.CreateDirectory(_download);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        private static DownloadArchiveUnpacker Subject(bool enabled = true, long? freeSpace = null)
        {
            return new DownloadArchiveUnpacker(FreeSpaceDiskProviderProxy.Create(freeSpace), TalearrConfigProxy.Create(unpackArchives: enabled), LogManager.GetCurrentClassLogger());
        }

        private void WriteSceneRelease(byte[] epub)
        {
            // The shape seen on real scene ebook releases: a zip holding a rar plus .nfo/.diz,
            // next to another .nfo.
            var rar = TestArchives.Rar(("some.author.some.book.2021.retail.epub.ebook-grp.epub", epub));
            var zip = TestArchives.Zip(
                ("grp123b.rar", rar),
                ("grp123.diz", TestArchives.Text("file_id")),
                ("GRP.nfo", TestArchives.Text("release notes")));

            File.WriteAllBytes(Path.Combine(_download, "grp123b.zip"), zip);
            File.WriteAllText(Path.Combine(_download, "grp.nfo"), "release notes");
        }

        private Dictionary<string, string> SnapshotDownload()
        {
            return Directory.EnumerateFiles(_download, "*", SearchOption.AllDirectories)
                .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }

        [Test]
        public void should_unpack_a_zip_holding_a_rar_into_a_folder_next_to_the_download()
        {
            var epub = TestArchives.Epub("en");
            WriteSceneRelease(epub);

            var result = Subject().Unpack(_download);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(Path.GetDirectoryName(result.WorkFolder), Is.EqualTo(_root));
            Assert.That(Path.GetFileName(result.WorkFolder), Is.EqualTo(".talearr-unpack.Some.Author.Some.Book.2021.RETAiL.ePub.eBook-GRP"));
            Assert.That(result.ArchivesUnpacked, Has.Count.EqualTo(2));
            Assert.That(result.MediaFiles, Has.Count.EqualTo(1));
            Assert.That(result.MediaFiles[0], Does.EndWith(".epub"));
            Assert.That(File.ReadAllBytes(result.MediaFiles[0]), Is.EqualTo(epub));
        }

        [Test]
        public void should_never_change_or_delete_the_original_download()
        {
            WriteSceneRelease(TestArchives.Epub("en"));
            var before = SnapshotDownload();
            var subject = Subject();

            var result = subject.Unpack(_download);
            subject.Cleanup(result);

            Assert.That(SnapshotDownload(), Is.EqualTo(before));
            Assert.That(Directory.Exists(result.WorkFolder), Is.False);
        }

        [Test]
        public void should_unpack_a_new_style_multi_part_rar_set()
        {
            var epub = TestArchives.Epub("en");
            var volumes = TestArchives.RarVolumes(new[] { ("book.epub", epub) }, 3);
            for (var i = 0; i < volumes.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(_download, $"book.part{i + 1:00}.rar"), volumes[i]);
            }

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.ArchivesUnpacked, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllBytes(result.MediaFiles.Single()), Is.EqualTo(epub));
        }

        [Test]
        public void should_unpack_a_zip_holding_a_multi_part_rar_set()
        {
            var epub = TestArchives.Epub("en");
            var volumes = TestArchives.RarVolumes(new[] { ("book.epub", epub) }, 2);
            var zip = TestArchives.Zip(("book.part1.rar", volumes[0]), ("book.part2.rar", volumes[1]), ("book.nfo", TestArchives.Text("nfo")));
            File.WriteAllBytes(Path.Combine(_download, "book.zip"), zip);

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(File.ReadAllBytes(result.MediaFiles.Single()), Is.EqualTo(epub));
        }

        [Test]
        public void should_unpack_a_7z_inside_a_zip()
        {
            var epub = TestArchives.Epub("en");
            File.WriteAllBytes(Path.Combine(_download, "outer.zip"), TestArchives.Zip(("inner.7z", TestArchives.SevenZip(("book.epub", epub)))));

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(File.ReadAllBytes(result.MediaFiles.Single()), Is.EqualTo(epub));
        }

        [Test]
        public void should_unpack_a_single_archive_file_download()
        {
            var epub = TestArchives.Epub("en");
            var archive = Path.Combine(_root, "Single.Book.zip");
            File.WriteAllBytes(archive, TestArchives.Zip(("book.epub", epub)));

            var result = Subject().Unpack(archive);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.WorkFolder, Is.EqualTo(Path.Combine(_root, ".talearr-unpack.Single.Book")));
            Assert.That(File.Exists(archive), Is.True);
        }

        [Test]
        public void should_leave_payloads_that_already_have_book_files_alone()
        {
            // BookUnpack (or the download client) already unpacked it: nothing to do.
            WriteSceneRelease(TestArchives.Epub("en"));
            File.WriteAllBytes(Path.Combine(_download, "book.epub"), TestArchives.Epub("en"));

            Assert.That(Subject().Unpack(_download), Is.Null);
            Assert.That(Directory.EnumerateDirectories(_root, ".talearr-unpack.*"), Is.Empty);
        }

        [Test]
        public void should_do_nothing_without_archives()
        {
            File.WriteAllText(Path.Combine(_download, "readme.nfo"), "nfo");

            Assert.That(Subject().Unpack(_download), Is.Null);
        }

        [Test]
        public void should_do_nothing_when_turned_off()
        {
            WriteSceneRelease(TestArchives.Epub("en"));

            Assert.That(Subject(enabled: false).Unpack(_download), Is.Null);
        }

        [Test]
        public void should_fail_cleanly_when_the_archives_hold_no_book()
        {
            File.WriteAllBytes(Path.Combine(_download, "junk.zip"), TestArchives.Zip(("readme.txt", TestArchives.Text("nothing"))));

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("did not contain any supported"));
            Assert.That(Directory.Exists(result.WorkFolder), Is.False);
        }

        [Test]
        public void should_fail_cleanly_for_a_damaged_archive()
        {
            File.WriteAllBytes(Path.Combine(_download, "broken.rar"), new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 9, 9, 9 });

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("broken.rar"));
            Assert.That(Directory.Exists(result.WorkFolder), Is.False);
            Assert.That(File.Exists(Path.Combine(_download, "broken.rar")), Is.True);
        }

        [Test]
        public void should_refuse_archives_nested_deeper_than_the_limit()
        {
            var level3 = TestArchives.Zip(("book.epub", TestArchives.Epub("en")));
            var level2 = TestArchives.Zip(("level3.zip", level3));
            var level1 = TestArchives.Zip(("level2.zip", level2));
            File.WriteAllBytes(Path.Combine(_download, "level1.zip"), level1);

            var subject = Subject();
            subject.Limits = new ArchiveExtractionLimits { MaxDepth = 2 };

            var result = subject.Unpack(_download);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("nested more than 2 levels"));
        }

        [Test]
        public void should_fail_cleanly_for_a_zip_bomb()
        {
            File.WriteAllBytes(Path.Combine(_download, "bomb.zip"), TestArchives.Zip(("zeros.bin", new byte[4 * 1024 * 1024])));
            var subject = Subject();
            subject.Limits = new ArchiveExtractionLimits { MaxExpansionRatio = 10, MinExpansionAllowanceBytes = 512 * 1024 };

            var result = subject.Unpack(_download);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("zip bomb"));
            Assert.That(Directory.Exists(result.WorkFolder), Is.False);
        }

        [Test]
        public void should_refuse_to_unpack_without_enough_free_space()
        {
            WriteSceneRelease(TestArchives.Epub("en"));

            var result = Subject(freeSpace: 10).Unpack(_download);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("not enough free space"));
        }

        [Test]
        public void should_reuse_a_finished_unpack_while_a_conversion_still_needs_it()
        {
            WriteSceneRelease(TestArchives.Epub("en"));
            var subject = Subject();
            var first = subject.Unpack(_download);

            var second = subject.Unpack(_download);

            Assert.That(second.Success, Is.True);
            Assert.That(second.Reused, Is.True);
            Assert.That(second.WorkFolder, Is.EqualTo(first.WorkFolder));
            Assert.That(second.MediaFiles, Is.EqualTo(first.MediaFiles));
        }

        [Test]
        public void should_start_again_when_an_earlier_unpack_did_not_finish()
        {
            WriteSceneRelease(TestArchives.Epub("en"));
            var workFolder = DownloadArchiveUnpacker.GetWorkFolder(_download, true);
            Directory.CreateDirectory(workFolder);
            File.WriteAllText(Path.Combine(workFolder, "half-written.epub"), "partial");

            var result = Subject().Unpack(_download);

            Assert.That(result.Success, Is.True, result.Error);
            Assert.That(result.Reused, Is.False);
            Assert.That(File.Exists(Path.Combine(workFolder, "half-written.epub")), Is.False);
        }

        [Test]
        public void should_remove_stale_unpack_folders_left_by_removed_downloads()
        {
            WriteSceneRelease(TestArchives.Epub("en"));
            var stale = Path.Combine(_root, ".talearr-unpack.Long.Gone.Download");
            Directory.CreateDirectory(stale);
            Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-5));
            var recent = Path.Combine(_root, ".talearr-unpack.Still.Importing");
            Directory.CreateDirectory(recent);

            Subject().Unpack(_download);

            Assert.That(Directory.Exists(stale), Is.False);
            Assert.That(Directory.Exists(recent), Is.True);
        }

        [Test]
        public void should_not_unpack_from_inside_a_work_folder()
        {
            var workFolder = Path.Combine(_root, ".talearr-unpack.Something");
            Directory.CreateDirectory(workFolder);
            File.WriteAllBytes(Path.Combine(workFolder, "again.zip"), TestArchives.Zip(("book.epub", TestArchives.Epub("en"))));

            Assert.That(Subject().Unpack(workFolder), Is.Null);
        }

        [Test]
        public void should_only_delete_folders_it_created()
        {
            var subject = Subject();

            subject.Cleanup(new ArchiveUnpackResult { SourcePath = _download, WorkFolder = _download });

            Assert.That(Directory.Exists(_download), Is.True);
        }
    }
}
