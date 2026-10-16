using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Pins the upstream Chaptarr behaviour the recordings feature is built on, so an upstream merge
    // that changes it fails CI instead of quietly breaking recordings. TaleArr does not fork any of
    // this code (docs/TALEARR_RECORDINGS_DESIGN.md, "Upstream merges"). If one of these fails after
    // a merge, read the upstream change and adapt TalearrRecordingsService before relaxing the test.
    [TestFixture]
    public class TalearrRecordingsUpstreamPinFixture
    {
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

        // --- Copy creation (BookService.AddWantedEdition) ---

        [Test]
        public void add_wanted_edition_on_a_book_with_files_creates_a_pinned_manual_copy_row()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            var ebookEdition = new Edition { Id = 28999, BookId = 3401, ForeignEditionId = "hc:edition:1-ebook", HardcoverEditionId = "1", Title = "The Firm", ReadingFormatId = 3 };
            _world.Editions.Add(ebookEdition);

            var copy = _world.BookService.AddWantedEdition(3401, 28701);

            var copyEditions = _world.Editions.Where(e => e.BookId == copy.Id).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(copy.Id, Is.Not.EqualTo(3401), "a new row");
                Assert.That(copy.TitleSlug, Is.EqualTo("the-firm_2_wanted_28701"));
                Assert.That(copy.BaseBookId, Is.EqualTo("hc:378630"), "same identity group");
                Assert.That(copy.AuthorId, Is.EqualTo(7));
                Assert.That(copy.MediaType, Is.EqualTo(BookMediaType.Audiobook));
                Assert.That(copy.AddOptions.AddType, Is.EqualTo(BookAddType.Manual), "refresh keeps manual rows");
                Assert.That(copy.AddOptions.SearchForNewBook, Is.False, "the caller decides about searching");
                Assert.That(copy.AnyEditionOk, Is.False);
                Assert.That(copy.Monitored && copy.AudiobookMonitored && !copy.EbookMonitored, Is.True);

                Assert.That(copyEditions.All(e => e.ReadingFormatId == 2), Is.True, "only audiobook editions are cloned");
                Assert.That(copyEditions.Count, Is.EqualTo(FirmEditions.All().Count));
                var pinned = copyEditions.Single(e => e.Monitored);
                Assert.That(TalearrEditionKey.Same(pinned, FirmEditions.ScottBrickUs()), Is.True);
                Assert.That(pinned.ManualAdd, Is.True);

                Assert.That(_world.Editions.Single(e => e.BookId == 3401 && e.Monitored).Id, Is.EqualTo(28705), "the base row keeps its recording");
                Assert.That(_world.Events.OfType<BookAddedEvent>().Single().DoRefresh, Is.False, "no author refresh");
            });
        }

        [Test]
        public void add_wanted_edition_is_audiobook_only()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);

            Assert.Throws<InvalidOperationException>(() => _world.BookService.AddWantedEdition(3402, 28701));
        }

        [Test]
        public void add_wanted_edition_rejects_an_edition_of_another_row()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            var copy = _world.BookService.AddWantedEdition(3401, 28701);
            var copyEdition = _world.Editions.First(e => e.BookId == copy.Id);

            Assert.Throws<InvalidOperationException>(() => _world.BookService.AddWantedEdition(3401, copyEdition.Id));
        }

        // --- Per-row upgrade (UpgradeMediaFileService) ---

        [Test]
        public void an_upgrade_replaces_only_the_files_of_its_own_row()
        {
            var abridgedFile = new BookFile { Id = 1, Path = "/audiobooks/John Grisham/The Firm - D.W. Moffett/The Firm.m4b" };
            var oldUnabridged = new BookFile { Id = 2, Path = "/audiobooks/John Grisham/The Firm - Scott Brick/The Firm.mp3" };
            var copy = new Book { Id = 9000, Title = "The Firm", MediaType = BookMediaType.Audiobook, BookFiles = new List<BookFile> { oldUnabridged } };

            var deletedFiles = new List<BookFile>();
            var recycled = new List<string>();

            var (recycleBin, recycleFake) = InterfaceFake<IRecycleBinProvider>.Create();
            recycleFake.On(nameof(IRecycleBinProvider.DeleteFile), args =>
            {
                recycled.Add((string)args[0]);
                return null;
            });
            var (mediaFiles, mediaFake) = InterfaceFake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.Delete), args =>
            {
                deletedFiles.Add((BookFile)args[0]);
                return null;
            });
            var (tags, tagFake) = InterfaceFake<IMetadataTagService>.Create();
            tagFake.On(nameof(IMetadataTagService.WriteTags), _ => null);
            var (mover, moverFake) = InterfaceFake<IMoveBookFiles>.Create();
            moverFake.On(nameof(IMoveBookFiles.MoveBookFile), args => args[0]);
            var (disk, diskFake) = InterfaceFake<IDiskProvider>.Create();
            diskFake.On(nameof(IDiskProvider.GetParentFolder), args => System.IO.Path.GetDirectoryName(((string)args[0]).TrimEnd('/')));
            diskFake.On(nameof(IDiskProvider.FolderExists), _ => true);
            diskFake.On(nameof(IDiskProvider.FileExistsCanonical), _ => true);
            var (roots, rootFake) = InterfaceFake<IRootFolderService>.Create();
            rootFake.On(nameof(IRootFolderService.GetBestRootFolder), _ => null);
            var (calibre, _) = InterfaceFake<ICalibreProxy>.Create();

            var subject = new UpgradeMediaFileService(recycleBin, mediaFiles, tags, mover, disk, roots, calibre, LogManager.GetCurrentClassLogger());
            var newFile = new BookFile { Path = "/downloads/The Firm Unabridged/The Firm.m4b" };

            var result = subject.UpgradeBookFile(newFile, new LocalBook
            {
                Book = copy,
                Path = newFile.Path,
                Author = new Author { Path = "/audiobooks/John Grisham" }
            });

            Assert.That(deletedFiles, Is.EqualTo(new[] { oldUnabridged }));
            Assert.That(recycled, Is.EqualTo(new[] { oldUnabridged.Path }));
            Assert.That(recycled, Does.Not.Contain(abridgedFile.Path), "the other recording's files are untouched");
            Assert.That(result.OldFiles, Is.EqualTo(new[] { oldUnabridged }));
        }

        // --- Refresh preservation ---

        [TestCase(typeof(RefreshBookService))]
        [TestCase(typeof(RefreshAuthorService))]
        public void refresh_keeps_copy_rows(Type service)
        {
            var marker = service.GetMethod("HasLocalBookInstancePreservationMarker", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            Assert.That(marker, Is.Not.Null, $"{service.Name}.HasLocalBookInstancePreservationMarker is gone; recheck refresh preservation of copy rows");

            var target = marker.IsStatic ? null : System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(service);
            bool Preserved(Book book) => (bool)marker.Invoke(target, new object[] { book });

            var wantedCopy = new Book { AnyEditionOk = false, AddOptions = new AddBookOptions { AddType = BookAddType.Manual }, Editions = new List<Edition>() };
            var pinnedOnly = new Book { AnyEditionOk = false, AddOptions = new AddBookOptions(), Editions = new List<Edition>() };
            var manualEdition = new Book { AnyEditionOk = true, AddOptions = new AddBookOptions(), Editions = new List<Edition> { new Edition { ManualAdd = true } } };
            var plain = new Book { AnyEditionOk = true, AddOptions = new AddBookOptions(), Editions = new List<Edition> { new Edition() } };

            Assert.Multiple(() =>
            {
                Assert.That(Preserved(wantedCopy), Is.True, "AddType Manual");
                Assert.That(Preserved(pinnedOnly), Is.True, "AnyEditionOk false");
                Assert.That(Preserved(manualEdition), Is.True, "a manually picked edition");
                Assert.That(Preserved(plain), Is.False, "ordinary rows can still be pruned");
            });

            if (service == typeof(RefreshAuthorService))
            {
                var importCopy = new Book { AnyEditionOk = true, UnitKeyHash = "abc", AddOptions = new AddBookOptions(), Editions = new List<Edition>() };
                Assert.That(Preserved(importCopy), Is.True, "import copies (UnitKeyHash) are never merged away");
            }
        }

        // --- TaleArr Phase 1 code that must leave copies alone ---

        [Test]
        public void catalog_dedupe_never_folds_a_copy_row()
        {
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            var copy = _world.BookService.AddWantedEdition(3401, 28701);
            copy.Editions = _world.Editions.Where(e => e.BookId == copy.Id).ToList();
            var primary = _world.Books[3401];
            primary.Editions = _world.Editions.Where(e => e.BookId == 3401).ToList();

            Assert.That(TalearrCatalogDedupe.IsAudibleOnly(copy), Is.False);
            Assert.That(TalearrCatalogDedupe.IsAudibleDuplicateOf(primary, copy), Is.False);
            Assert.That(TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { primary, copy }), Has.Count.EqualTo(2));
        }

        [Test]
        public void edition_repair_keeps_the_copy_rows_pinned_edition()
        {
            var selector = new EditionSelector(LogManager.GetCurrentClassLogger());
            var other = FirmEditions.ScottBrickUs();
            other.Monitored = false;
            var pinned = FirmEditions.MoffettAbridged();
            pinned.Monitored = true;
            pinned.ManualAdd = true;
            var editions = new List<Edition> { other, pinned };

            selector.EnsureSingleMonitoredEdition(editions, null, BookMediaType.Audiobook);
            var released = selector.ReleaseForeignAutoSelectedEdition(editions, new Dictionary<int, int>(), BookMediaType.Audiobook);

            Assert.That(released, Is.False, "a manual pick is never released");
            Assert.That(editions.Single(e => e.Monitored), Is.SameAs(pinned));
        }
    }
}
