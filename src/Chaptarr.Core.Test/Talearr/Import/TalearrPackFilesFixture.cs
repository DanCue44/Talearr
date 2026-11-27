using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.Import;
using NzbDrone.Core.Talearr.Recordings;
using QueueItem = NzbDrone.Core.Queue.Queue;

namespace Chaptarr.Core.Test.Talearr.Import
{
    // Talearr (issue #14): J.N. Chaney's "Renegade Star" Publisher's Pack audiobooks (two books per
    // M4B) were attached to book 1 as three recordings of it. Pack files must never match a single
    // book: they go to the author's catalog entry for that pack, or stay unmatched.
    [TestFixture]
    public class TalearrPackFilesFixture
    {
        private const string Pack1 = "/audiobooks/audiobookshelf/J.N. Chaney/Renegade Star/JN Chaney-Renegade Star-#1-Publisher's Pack 1-Book 1-2/Renegade Star Publisher's Pack 1 (Unabridged).m4b";
        private const string Pack2 = "/audiobooks/audiobookshelf/J.N. Chaney/Renegade Star/JN Chaney-Renegade Star-#2-Publisher's Pack 2-Book 3-4/Renegade Star Publisher's Pack 2 (Unabridged).m4b";
        private const string Pack3 = "/audiobooks/audiobookshelf/J.N. Chaney/Renegade Star/JN Chaney-Renegade Star-#3-Publisher's Pack 3-Book 5-6/Renegade Star Publisher's Pack 3 (Unabridged).m4b";
        private const string Asin = "B07BK7J8C4";

        private RecordingsWorld _world;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private static DiscoveredFileWithMetadata File(string path, string title = null)
        {
            var file = new DiscoveredFileWithMetadata { Path = path };
            if (title != null)
            {
                file.AllTags["title"] = new List<string> { title };
                file.AllTags["album"] = new List<string> { title };
            }

            return file;
        }

        private Book AddBook(int id, string title, string position = null, string slug = null, bool omnibus = false)
        {
            var bookOneWork = id == 100 || (slug != null && slug.Contains("_copy_"));
            var book = new Book
            {
                Id = id,
                AuthorId = 7,
                Title = title,
                TitleSlug = slug ?? $"book-{id}",
                SeriesPosition = position,
                IsOmnibus = omnibus,
                BaseBookId = bookOneWork ? "hc:renegade-1" : $"hc:{id}",
                HardcoverBookId = bookOneWork ? "hc:renegade-1" : $"hc:{id}",
                MediaType = BookMediaType.Audiobook,
                Monitored = true,
                AudiobookMonitored = true,
                AddOptions = new AddBookOptions()
            };
            _world.Books[id] = book;
            _world.Editions.Add(new Edition
            {
                Id = id * 10,
                BookId = id,
                ForeignEditionId = $"hc:edition:{id}-audiobook",
                Title = title,
                AudibleASIN = bookOneWork ? Asin : "B0PACK" + id,
                ReadingFormatId = 2,
                Monitored = true,
                ManualAdd = id == 100
            });
            return book;
        }

        private Book BookOne() => AddBook(100, "Renegade Star", "1", "renegade-star");

        private List<Book> Catalog(int authorId) => _world.Books.Values.Where(b => b.AuthorId == authorId).ToList();

        private List<Edition> EditionsOf(int bookId) => _world.Editions.Where(e => e.BookId == bookId).ToList();

        // Parsing ------------------------------------------------------------------------------

        [Test]
        public void publishers_pack_number_is_a_pack_not_a_series_place()
        {
            var evidence = TalearrPackFiles.Parse("Renegade Star Publisher's Pack 3 (Unabridged)");

            Assert.Multiple(() =>
            {
                Assert.That(evidence, Is.Not.Null);
                Assert.That(evidence.PackNumber, Is.EqualTo(3));
                Assert.That(evidence.HasRange, Is.False, "Pack 3 says nothing about book 3");
            });
        }

        [Test]
        public void book_5_6_is_a_range_not_book_5()
        {
            var evidence = TalearrPackFiles.Parse("JN Chaney-Renegade Star-#3-Publisher's Pack 3-Book 5-6");

            Assert.Multiple(() =>
            {
                Assert.That(evidence.RangeStart, Is.EqualTo(5));
                Assert.That(evidence.RangeEnd, Is.EqualTo(6));
                Assert.That(evidence.PackNumber, Is.EqualTo(3));
            });
        }

        [TestCase("Renegade Star, Books 1-2", 1, 2)]
        [TestCase("The Poppy War Box Set (Books 1-3)", 1, 3)]
        [TestCase("Series Books 4 - 6", 4, 6)]
        [TestCase("Dresden Files Books 1 and 2", 1, 2)]
        [TestCase("Box Set 1-3", 1, 3)]
        public void ranges_are_read(string text, int start, int end)
        {
            var evidence = TalearrPackFiles.Parse(text);

            Assert.That(evidence?.RangeStart, Is.EqualTo(start));
            Assert.That(evidence?.RangeEnd, Is.EqualTo(end));
            Assert.That(evidence?.PackNumber, Is.Null);
        }

        [TestCase("Renegade Star Omnibus")]
        [TestCase("The Expanse Complete Series")]
        [TestCase("Mistborn Boxed Set")]
        [TestCase("Bobiverse 3 Books")]
        public void named_packs_are_packs(string text)
        {
            Assert.That(TalearrPackFiles.Parse(text), Is.Not.Null);
        }

        [TestCase("Renegade Star")]
        [TestCase("Renegade Star (Unabridged)")]
        [TestCase("Renegade Star, Book 3")]
        [TestCase("The Broken Earth Trilogy, Book 1 - The Fifth Season")]
        [TestCase("Book 1 - 02")]
        [TestCase("Book 1-02")]
        [TestCase("Renegade Star 05-06")]
        [TestCase("The Collector")]
        [TestCase("Six of Crows Duology, Book 2")]
        public void single_books_and_chapters_are_not_packs(string text)
        {
            Assert.That(TalearrPackFiles.Parse(text), Is.Null);
        }

        [Test]
        public void pack_number_spans_point_at_the_pack_number_only()
        {
            const string value = "Renegade Star Publisher's Pack 3 Book 5-6";
            var spans = TalearrPackFiles.PackNumberSpans(value);

            Assert.That(spans.Count, Is.EqualTo(1));
            Assert.That(value.Substring(spans[0].Index, spans[0].Length), Is.EqualTo("3"));
            Assert.That(spans[0].Index, Is.EqualTo(value.IndexOf("3", System.StringComparison.Ordinal)));
        }

        [Test]
        public void the_folder_only_adds_numbers_to_a_file_that_reads_as_a_pack()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TalearrPackFiles.FromPath(Pack3)?.RangeStart, Is.EqualTo(5));
                Assert.That(TalearrPackFiles.FromPath("/books/Author/Poppy War Box Set (Books 1-3)/The Poppy War.epub"), Is.Null,
                    "a single book inside a box set download folder is a single book");
            });
        }

        // Matching -----------------------------------------------------------------------------

        [Test]
        public void a_pack_file_matched_to_book_one_is_left_unmatched_when_no_pack_entry_exists()
        {
            var bookOne = BookOne();

            var decision = TalearrPackFiles.Decide(File(Pack3, "Renegade Star: Publisher's Pack 3"), bookOne, Catalog, EditionsOf);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrPackVerdict.Unmatch));
            Assert.That(decision.Reason, Does.Contain("Manual Import"));
        }

        [Test]
        public void a_pack_file_matched_to_book_one_goes_to_the_pack_entry()
        {
            var bookOne = BookOne();
            AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");
            AddBook(130, "Renegade Star: Renegade Star, Books 5-6", "3");

            var pack1 = TalearrPackFiles.Decide(File(Pack1), bookOne, Catalog, EditionsOf);
            var pack3 = TalearrPackFiles.Decide(File(Pack3), bookOne, Catalog, EditionsOf);

            Assert.Multiple(() =>
            {
                Assert.That(pack1.Verdict, Is.EqualTo(TalearrPackVerdict.Redirect));
                Assert.That(pack1.Target.Id, Is.EqualTo(110));
                Assert.That(pack1.Edition.Id, Is.EqualTo(1100));
                Assert.That(pack3.Verdict, Is.EqualTo(TalearrPackVerdict.Redirect));
                Assert.That(pack3.Target.Id, Is.EqualTo(130));
            });
        }

        [Test]
        public void a_pack_entry_of_another_series_is_not_used()
        {
            var bookOne = BookOne();
            AddBook(140, "Star Rogue: Books 5-6");

            var decision = TalearrPackFiles.Decide(File(Pack3), bookOne, Catalog, EditionsOf);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrPackVerdict.Unmatch));
        }

        [Test]
        public void a_pack_file_matched_to_its_own_pack_entry_stays()
        {
            BookOne();
            var entry = AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");

            Assert.That(TalearrPackFiles.Decide(File(Pack1), entry, Catalog, EditionsOf).Verdict, Is.EqualTo(TalearrPackVerdict.Keep));
        }

        [Test]
        public void a_pack_file_matched_to_another_packs_entry_moves()
        {
            BookOne();
            var wrong = AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");
            AddBook(130, "Renegade Star: Renegade Star, Books 5-6", "3");

            var decision = TalearrPackFiles.Decide(File(Pack3), wrong, Catalog, EditionsOf);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrPackVerdict.Redirect));
            Assert.That(decision.Target.Id, Is.EqualTo(130));
        }

        [Test]
        public void an_omnibus_book_keeps_its_omnibus_file()
        {
            var omnibus = AddBook(150, "The Poppy War Box Set", omnibus: true);

            Assert.That(TalearrPackFiles.Decide(File("/books/R.F. Kuang/The Poppy War Box Set/The Poppy War Box Set.m4b"), omnibus, Catalog, EditionsOf).Verdict,
                Is.EqualTo(TalearrPackVerdict.Keep));
        }

        [Test]
        public void a_single_book_file_is_never_touched()
        {
            var bookOne = BookOne();

            Assert.That(TalearrPackFiles.Decide(File("/audiobooks/J.N. Chaney/Renegade Star/Renegade Star (Unabridged).m4b", "Renegade Star"), bookOne, Catalog, EditionsOf).Verdict,
                Is.EqualTo(TalearrPackVerdict.Keep));
        }

        [Test]
        public void a_download_grabbed_for_the_book_keeps_its_match()
        {
            var bookOne = BookOne();

            var decision = TalearrPackFiles.Decide(File(Pack3), bookOne, Catalog, EditionsOf, targetBookIds: new List<int> { bookOne.Id });

            Assert.That(decision.Verdict, Is.EqualTo(TalearrPackVerdict.Keep));
        }

        [Test]
        public void the_pack_title_tag_alone_is_enough()
        {
            var bookOne = BookOne();

            var decision = TalearrPackFiles.Decide(File("/audiobooks/J.N. Chaney/Renegade Star/track.m4b", "Renegade Star: Publisher's Pack 2"), bookOne, Catalog, EditionsOf);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrPackVerdict.Unmatch));
        }

        [Test]
        public void the_matcher_moves_or_releases_pack_matches_before_returning()
        {
            var bookOne = BookOne();
            AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");
            var single = File("/audiobooks/J.N. Chaney/Renegade Star/Renegade Star (Unabridged).m4b", "Renegade Star");
            var result = new FileMatchResult
            {
                MatchedFiles = new[]
                {
                    new FileMatch { File = File(Pack1), AuthorId = 7, BookId = bookOne.Id, EditionId = 1000, BookTitle = bookOne.Title },
                    new FileMatch { File = File(Pack3), AuthorId = 7, BookId = bookOne.Id, EditionId = 1000, BookTitle = bookOne.Title },
                    new FileMatch { File = single, AuthorId = 7, BookId = bookOne.Id, EditionId = 1000, BookTitle = bookOne.Title }
                }
            };

            var matcher = new FileMatchingService(null, null, null, null, null, null, null, null, null, null, null, null,
                _world.BookService, _world.EditionService, null, null, LogManager.GetCurrentClassLogger());
            typeof(FileMatchingService)
                .GetMethod("ApplyTalearrPackGuard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(matcher, new object[] { result, null, null });

            Assert.Multiple(() =>
            {
                Assert.That(result.MatchedFiles.Select(m => (m.File.Path, m.BookId, m.EditionId)), Is.EqualTo(new[]
                {
                    (Pack1, 110, 1100),
                    (single.Path, 100, 1000)
                }));
                Assert.That(result.UnmatchedFiles.Single().File.Path, Is.EqualTo(Pack3));
            });
        }

        // Existing rows ------------------------------------------------------------------------

        // The user's state: book 1 (pinned to the pack's ASIN) holding Pack 3, and two import copies
        // of the same edition holding Pack 1 and Pack 2.
        private (Book BookOne, Book Copy1, Book Copy2) UsersRows()
        {
            var bookOne = BookOne();
            var copy1 = AddBook(101, "Renegade Star", "1", "renegade-star_copy_101");
            var copy2 = AddBook(102, "Renegade Star", "1", "renegade-star_copy_102");
            _world.Files.Add(new BookFile { Id = 1, EditionId = 1000, Path = Pack3 });
            _world.Files.Add(new BookFile { Id = 2, EditionId = 1010, Path = Pack1 });
            _world.Files.Add(new BookFile { Id = 3, EditionId = 1020, Path = Pack2 });
            return (bookOne, copy1, copy2);
        }

        private TalearrRecordingMaintenance Maintenance()
        {
            var (queue, queueFake) = InterfaceFake<IQueueService>.Create();
            queueFake.On(nameof(IQueueService.GetQueue), _ => new List<QueueItem>());

            return new TalearrRecordingMaintenance(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Groups, _world.Facts,
                LogManager.GetCurrentClassLogger(), queueService: queue);
        }

        private TalearrPackFileRepair Repair()
        {
            return new TalearrPackFileRepair(_world.BookService, _world.EditionService, _world.MediaFiles, LogManager.GetCurrentClassLogger(), Maintenance());
        }

        [Test]
        public void the_merge_never_moves_a_pack_file_onto_the_primary_book()
        {
            var bookOne = BookOne();
            AddBook(101, "Renegade Star", "1", "renegade-star_copy_101");
            _world.Files.Add(new BookFile { Id = 2, EditionId = 1010, Path = Pack3 });

            var removed = Maintenance().MergeDuplicates(bookOne);

            Assert.Multiple(() =>
            {
                Assert.That(removed, Is.Empty);
                Assert.That(_world.Files.Single().EditionId, Is.EqualTo(1010), "the pack file stays where it is");
                Assert.That(_world.Books.ContainsKey(101), Is.True);
            });
        }

        [Test]
        public void the_merge_still_folds_a_single_book_file_of_the_same_recording()
        {
            var bookOne = BookOne();
            AddBook(101, "Renegade Star", "1", "renegade-star_copy_101");
            _world.Files.Add(new BookFile { Id = 2, EditionId = 1010, Path = "/audiobooks/J.N. Chaney/Renegade Star/Renegade Star (Unabridged).m4b" });

            var removed = Maintenance().MergeDuplicates(bookOne);

            Assert.That(removed, Is.EqualTo(new[] { 101 }));
            Assert.That(_world.Files.Single().EditionId, Is.EqualTo(1000));
        }

        [Test]
        public void the_merge_leaves_rows_holding_different_pack_files_alone()
        {
            var (bookOne, _, _) = UsersRows();

            var removed = Maintenance().MergeDuplicates(bookOne);

            Assert.That(removed, Is.Empty);
            Assert.That(_world.Files.Select(f => f.EditionId), Is.EquivalentTo(new[] { 1000, 1010, 1020 }));
        }

        [Test]
        public void repair_releases_packs_that_leave_book_one_out_and_removes_the_empty_copies()
        {
            UsersRows();

            var changed = Repair().Repair(7);

            Assert.Multiple(() =>
            {
                Assert.That(changed, Is.EqualTo(2));
                Assert.That(_world.Files.Single(f => f.Id == 1).EditionId, Is.EqualTo(0), "Pack 3 (Books 5-6) is released for Manual Import");
                Assert.That(_world.Files.Single(f => f.Id == 3).EditionId, Is.EqualTo(0), "Pack 2 (Books 3-4) is released for Manual Import");
                Assert.That(_world.Files.Single(f => f.Id == 2).EditionId, Is.EqualTo(1010), "Pack 1 includes book 1 and there is no pack entry: left alone");
                Assert.That(_world.Deleted, Is.EqualTo(new[] { 102 }), "the copy row left empty is removed");
                Assert.That(_world.Books.ContainsKey(100), Is.True);
            });
        }

        [Test]
        public void repair_moves_a_pack_to_its_catalog_entry()
        {
            UsersRows();
            AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");

            Repair().Repair(7);

            Assert.Multiple(() =>
            {
                Assert.That(_world.Files.Single(f => f.Id == 2).EditionId, Is.EqualTo(1100), "Pack 1 moves to the Books 1-2 entry");
                Assert.That(_world.Files.Where(f => f.Id != 2).Select(f => f.EditionId), Is.All.EqualTo(0));
                Assert.That(_world.Deleted, Is.EquivalentTo(new[] { 101, 102 }));
                Assert.That(_world.Books.ContainsKey(100), Is.True, "book 1 itself is never removed");
            });
        }

        [Test]
        public void repair_never_touches_single_book_files_or_pack_entries()
        {
            var bookOne = BookOne();
            AddBook(110, "Renegade Star: Renegade Star, Books 1-2", "1");
            _world.Files.Add(new BookFile { Id = 1, EditionId = 1000, Path = "/audiobooks/J.N. Chaney/Renegade Star/Renegade Star (Unabridged).m4b" });
            _world.Files.Add(new BookFile { Id = 2, EditionId = 1100, Path = Pack1 });

            Assert.That(Repair().Repair(7), Is.EqualTo(0));
            Assert.That(_world.Files.Select(f => f.EditionId), Is.EquivalentTo(new[] { 1000, 1100 }));
            Assert.That(bookOne.Id, Is.EqualTo(100));
        }
    }
}
