using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TalearrImport;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    [TestFixture]
    public class SafeArchiveExtractorFixture
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
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

        [TestCase("book.zip", true)]
        [TestCase("book.rar", true)]
        [TestCase("book.7z", true)]
        [TestCase("book.part1.rar", true)]
        [TestCase("book.part01.rar", true)]
        [TestCase("book.part001.rar", true)]
        [TestCase("book.part2.rar", false)]
        [TestCase("book.part10.rar", false)]
        [TestCase("book.r00", false)]
        [TestCase("book.epub", false)]
        [TestCase("book.nfo", false)]
        [TestCase("._book.zip", false)]
        public void should_only_open_archive_sets_from_their_first_volume(string fileName, bool expected)
        {
            Assert.That(SafeArchiveExtractor.IsFirstArchiveVolume(Path.Combine(_root, fileName)), Is.EqualTo(expected));
        }

        [TestCase("../evil.txt")]
        [TestCase("folder/../../evil.txt")]
        [TestCase("..\\evil.txt")]
        [TestCase("/etc/evil.txt")]
        [TestCase("\\evil.txt")]
        [TestCase("C:/evil.txt")]
        [TestCase("C:\\Windows\\evil.txt")]
        public void should_refuse_entry_names_that_escape_the_unpack_folder(string entryName)
        {
            Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.ResolveEntryPath(_root, entryName));
        }

        [Test]
        public void should_keep_nested_entry_names_inside_the_unpack_folder()
        {
            var resolved = SafeArchiveExtractor.ResolveEntryPath(_root, "Author/./Book/book.epub");

            Assert.That(resolved, Is.EqualTo(Path.Combine(Path.GetFullPath(_root), "Author", "Book", "book.epub")));
        }

        [Test]
        public void should_not_write_anything_outside_the_folder_for_a_traversal_zip()
        {
            var archive = Path.Combine(_root, "evil.zip");
            File.WriteAllBytes(archive, TestArchives.Zip(("../../escaped.txt", TestArchives.Text("nope"))));
            var destination = Path.Combine(_root, "out");

            Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(archive, destination, null));

            Assert.That(Directory.EnumerateFiles(_root, "escaped.txt", SearchOption.AllDirectories), Is.Empty);
            Assert.That(File.Exists(Path.Combine(Path.GetDirectoryName(_root), "escaped.txt")), Is.False);
        }

        [Test]
        public void should_stop_a_zip_bomb_by_counting_written_bytes()
        {
            // 8 MiB of zeros compresses to a few KiB; with a 10x ratio and a 1 MiB floor that is
            // far over budget.
            var archive = Path.Combine(_root, "bomb.zip");
            File.WriteAllBytes(archive, TestArchives.Zip(("zeros.bin", new byte[8 * 1024 * 1024])));
            var budget = new ArchiveExtractionBudget(new ArchiveExtractionLimits
            {
                MaxExpansionRatio = 10,
                MinExpansionAllowanceBytes = 1024 * 1024
            });

            var ex = Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(archive, Path.Combine(_root, "out"), budget));

            Assert.That(ex.Message, Does.Contain("zip bomb"));
            Assert.That(budget.BytesWritten, Is.LessThanOrEqualTo(1024 * 1024 + 81920));
        }

        [Test]
        public void should_stop_when_the_total_size_limit_is_reached()
        {
            var archive = Path.Combine(_root, "big.zip");
            File.WriteAllBytes(archive, TestArchives.Zip(("a.bin", new byte[600 * 1024]), ("b.bin", new byte[600 * 1024])));
            var budget = new ArchiveExtractionBudget(new ArchiveExtractionLimits { MaxTotalBytes = 1024 * 1024 });

            var ex = Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(archive, Path.Combine(_root, "out"), budget));

            Assert.That(ex.Message, Does.Contain("in total"));
        }

        [Test]
        public void should_stop_when_an_archive_has_too_many_entries()
        {
            var entries = Enumerable.Range(0, 20).Select(i => ($"file{i}.txt", TestArchives.Text("x"))).ToArray();
            var archive = Path.Combine(_root, "many.zip");
            File.WriteAllBytes(archive, TestArchives.Zip(entries));
            var budget = new ArchiveExtractionBudget(new ArchiveExtractionLimits { MaxEntries = 10 });

            var ex = Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(archive, Path.Combine(_root, "out"), budget));

            Assert.That(ex.Message, Does.Contain("more than 10 entries"));
        }

        [Test]
        public void should_extract_a_rar_set_split_over_several_volumes()
        {
            var epub = TestArchives.Epub("en");
            var volumes = TestArchives.RarVolumes(new[] { ("book.epub", epub) }, 3);
            for (var i = 0; i < volumes.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(_root, $"set.part{i + 1}.rar"), volumes[i]);
            }

            var written = SafeArchiveExtractor.Extract(Path.Combine(_root, "set.part1.rar"), Path.Combine(_root, "out"), null);

            Assert.That(written, Has.Count.EqualTo(1));
            Assert.That(File.ReadAllBytes(written[0]), Is.EqualTo(epub));
        }

        [Test]
        public void should_report_a_missing_volume()
        {
            var epub = TestArchives.Epub("en");
            var volumes = TestArchives.RarVolumes(new[] { ("book.epub", epub) }, 3);
            File.WriteAllBytes(Path.Combine(_root, "set.part1.rar"), volumes[0]);
            File.WriteAllBytes(Path.Combine(_root, "set.part2.rar"), volumes[1]);

            Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(Path.Combine(_root, "set.part1.rar"), Path.Combine(_root, "out"), null));
        }

        [Test]
        public void should_report_a_damaged_archive()
        {
            var archive = Path.Combine(_root, "broken.rar");
            File.WriteAllBytes(archive, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 1, 2, 3, 4, 5 });

            Assert.Throws<ArchiveExtractionException>(() => SafeArchiveExtractor.Extract(archive, Path.Combine(_root, "out"), null));
        }

        [Test]
        public void should_extract_a_7z_archive()
        {
            var epub = TestArchives.Epub("en");
            var archive = Path.Combine(_root, "book.7z");
            File.WriteAllBytes(archive, TestArchives.SevenZip(("Book/book.epub", epub)));

            var written = SafeArchiveExtractor.Extract(archive, Path.Combine(_root, "out"), null);

            Assert.That(written.Single(), Does.EndWith(Path.Combine("Book", "book.epub")));
            Assert.That(File.ReadAllBytes(written.Single()), Is.EqualTo(epub));
        }
    }
}
