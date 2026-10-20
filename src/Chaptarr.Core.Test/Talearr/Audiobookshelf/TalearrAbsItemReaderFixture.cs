using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrAbsItemReaderFixture
    {
        private static readonly DateTime SyncedAt = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        // Audiobookshelf sees the ebook share as /books, Talearr as /ebooks.
        private static readonly List<TalearrAbsFolderMapping> Mappings = new List<TalearrAbsFolderMapping>
        {
            new TalearrAbsFolderMapping { LibraryId = AbsFixtures.EbookLibraryId, LibraryFolderId = AbsFixtures.EbookFolderId, AbsFolderPath = "/books", LocalRootPath = "/ebooks" },
            new TalearrAbsFolderMapping { LibraryId = AbsFixtures.AudiobookLibraryId, LibraryFolderId = AbsFixtures.AudiobookFolderId, AbsFolderPath = "/audiobooks", LocalRootPath = "/audiobooks" }
        };

        private static readonly List<string> Roots = new List<string> { "/ebooks", "/audiobooks" };

        private static List<AbsLibraryItem> Items(string json)
        {
            return Json.Deserialize<AbsLibraryItemsPage>(json).Results;
        }

        private static TalearrAbsItem Read(string json, string id, IReadOnlyCollection<TalearrAbsFolderMapping> mappings = null)
        {
            var raw = Items(json).Single(i => i.Id == id);
            return TalearrAbsItemReader.Read(raw, 1, mappings ?? Mappings, Roots, SyncedAt);
        }

        [Test]
        public void should_read_an_audiobook_item()
        {
            var item = Read(AbsFixtures.AudiobookItems, "li-animal-farm-cosham");

            Assert.Multiple(() =>
            {
                Assert.That(item.Title, Is.EqualTo("Animal Farm"));
                Assert.That(item.Authors, Is.EqualTo("George Orwell"));
                Assert.That(item.Narrators, Is.EqualTo("Ralph Cosham"));
                Assert.That(item.Asin, Is.EqualTo("B0000545BA"));
                Assert.That(item.Isbn, Is.EqualTo("9781481540551"));
                Assert.That(item.DurationSeconds, Is.EqualTo(10040.88).Within(0.01));
                Assert.That(item.HasAudio, Is.True);
                Assert.That(item.HasEbook, Is.False);
                Assert.That(item.Abridged, Is.EqualTo(AbridgedMarker.Unknown), "ABS abridged=false means not set");
                Assert.That(item.LocalPath, Is.EqualTo("/audiobooks/George Orwell/Animal Farm - Ralph Cosham"));
                Assert.That(item.ConnectionId, Is.EqualTo(1));
                Assert.That(item.SyncedAt, Is.EqualTo(SyncedAt));
            });
        }

        [Test]
        public void should_map_the_audiobookshelf_folder_to_the_taleArr_root_folder()
        {
            var item = Read(AbsFixtures.EbookItemsSinglePage, "li-poppy-war-ebook");

            Assert.Multiple(() =>
            {
                Assert.That(item.Path, Is.EqualTo("/books/R.F. Kuang/The Poppy War"));
                Assert.That(item.LocalPath, Is.EqualTo("/ebooks/R.F. Kuang/The Poppy War"));
                Assert.That(item.HasEbook, Is.True);
                Assert.That(item.EbookFormat, Is.EqualTo("epub"));
                Assert.That(item.DurationSeconds, Is.Null);
            });
        }

        [Test]
        public void should_leave_the_local_path_empty_when_no_mapping_or_root_folder_covers_it()
        {
            var item = Read(AbsFixtures.EbookItemsSinglePage, "li-poppy-war-ebook", new List<TalearrAbsFolderMapping>());

            Assert.That(item.LocalPath, Is.Null);
        }

        [Test]
        public void should_use_the_audiobookshelf_path_as_is_when_it_is_inside_a_root_folder()
        {
            var item = Read(AbsFixtures.AudiobookItems, "li-steve-jobs", new List<TalearrAbsFolderMapping>());

            Assert.That(item.LocalPath, Is.EqualTo("/audiobooks/Walter Isaacson/Steve Jobs"));
        }

        [Test]
        public void should_normalize_a_hyphenated_isbn()
        {
            var item = Read(AbsFixtures.EbookItemsSinglePage, "li-firm-ebook");

            Assert.That(item.Isbn, Is.EqualTo("9780307576149"));
        }

        [Test]
        public void should_detect_abridged_from_the_folder_name()
        {
            var item = Read(AbsFixtures.AudiobookItems, "li-firm-abridged");

            Assert.That(item.Abridged, Is.EqualTo(AbridgedMarker.Abridged));
        }

        [Test]
        public void should_skip_missing_items()
        {
            Assert.That(Read(AbsFixtures.AudiobookItems, "li-gone"), Is.Null);
        }

        [Test]
        public void should_skip_podcasts_and_items_without_media()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TalearrAbsItemReader.Read(new AbsLibraryItem { Id = "p", MediaType = "podcast", Media = new AbsBookMedia { NumAudioFiles = 3, Metadata = new AbsBookMetadata { Title = "Show" } } }, 1, Mappings, Roots, SyncedAt), Is.Null);
                Assert.That(TalearrAbsItemReader.Read(new AbsLibraryItem { Id = "e", MediaType = "book", Media = new AbsBookMedia { Metadata = new AbsBookMetadata { Title = "Empty" } } }, 1, Mappings, Roots, SyncedAt), Is.Null);
            });
        }

        [TestCase(true, null, null, AbridgedMarker.Abridged)]
        [TestCase(false, "The Firm (Unabridged)", null, AbridgedMarker.Unabridged)]
        [TestCase(null, "The Firm", "Abridged Edition", AbridgedMarker.Abridged)]
        [TestCase(false, "The Firm", null, AbridgedMarker.Unknown)]
        public void should_detect_abridgement(bool? flag, string title, string subtitle, AbridgedMarker expected)
        {
            var metadata = new AbsBookMetadata { Abridged = flag, Title = title ?? "The Firm", Subtitle = subtitle };
            Assert.That(TalearrAbsItemReader.DetectAbridged(metadata, new AbsLibraryItem { RelPath = "John Grisham/The Firm" }), Is.EqualTo(expected));
        }

        [TestCase("978-0-307-57614-9", "9780307576149")]
        [TestCase("0307576140", "9780307576149")]
        [TestCase("0-8044-2957-X", "9780804429573")]
        [TestCase("12345", null)]
        [TestCase("", null)]
        public void should_normalize_isbns(string input, string expected)
        {
            Assert.That(TalearrAbsItemReader.NormalizeIsbn(input), Is.EqualTo(expected));
        }

        [Test]
        public void should_offer_the_isbn_10_form_for_lookups()
        {
            Assert.That(TalearrAbsItemReader.IsbnLookupForms("9780307576149"), Is.EqualTo(new[] { "9780307576149", "0307576140" }));
        }

        [TestCase("/books", "/books/A/B", true, "A/B")]
        [TestCase("/books/", "/books", true, "")]
        [TestCase("/books", "/bookshelf/A", false, null)]
        [TestCase("/books", "/other/A", false, null)]
        public void should_compute_relative_paths(string basePath, string fullPath, bool expected, string relative)
        {
            Assert.That(TalearrAbsItemReader.TryGetRelative(basePath, fullPath, out var actual), Is.EqualTo(expected));
            Assert.That(actual, Is.EqualTo(relative));
        }

        [Test]
        public void should_keep_windows_separators_for_a_windows_root_folder()
        {
            Assert.That(TalearrAbsItemReader.Combine(@"D:\Books", "R.F. Kuang/The Poppy War"), Is.EqualTo(@"D:\Books\R.F. Kuang\The Poppy War"));
        }
    }
}
