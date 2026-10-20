using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Two recordings of one book never land in the same book folder.
    [TestFixture]
    public class TalearrRecordingFoldersFixture
    {
        private const string NarratorFormat = "{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:smart)}";
        private const string PrimaryFolder = "/audiobooks/John Grisham/The Firm - Scott Brick";

        private RecordingsWorld _world;
        private TalearrRecordingFolders _folders;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _folders = new TalearrRecordingFolders(_world.BookService, _world.EditionService, _world.MediaFiles, _world.Groups, _world.Facts, _world.Settings, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        // Scott Brick read The Firm twice: the unabridged recording (primary, downloaded) and an
        // abridged one. Same narrator, so {Book Title}{ - Narrator} gives both the same folder.
        private static Edition ScottBrickAbridged() => new Edition
        {
            Id = 28790,
            BookId = 3401,
            ForeignEditionId = "hc:edition:5550001-audiobook",
            HardcoverEditionId = "5550001",
            Title = "The Firm",
            Language = "eng",
            Publisher = "Random House Audio",
            ReleaseDate = new DateTime(1993, 3, 1),
            NarratorNames = new List<string> { "Scott Brick" },
            EditionInfo = "Abridged",
            DurationSeconds = 6 * 3600,
            Asins = new List<string>(),
            ReadingFormatId = 2
        };

        private Book PrimaryWithScottBrickFiles()
        {
            var primary = _world.AddFirm(FirmEditions.ScottBrickUs(), withFiles: false);
            _world.Editions.First(e => e.Id == 28701).EditionInfo = "Unabridged";
            _world.Editions.Add(ScottBrickAbridged());
            _world.Files.Add(new BookFile
            {
                Id = 1,
                EditionId = 28701,
                Path = PrimaryFolder + "/The Firm.m4b",
                Quality = new QualityModel(Quality.M4B),
                MediaType = "audiobook",
                Part = 1,
                PartCount = 1,
                DurationSeconds = 61800,
                Narrator = "Scott Brick"
            });

            return primary;
        }

        private Edition AddCopy(int editionId)
        {
            var copy = _world.BookService.AddWantedEdition(3401, editionId);
            Assert.That(copy.Id, Is.Not.EqualTo(3401), "the primary has files, so upstream makes a wanted copy row");
            Assert.That(copy.TitleSlug, Does.Contain("_wanted_"));

            var edition = _world.Editions.Where(e => e.BookId == copy.Id).First(e => e.Monitored);
            edition.Book = copy;
            return edition;
        }

        private FileNameBuilder Builder(bool withFolders = true)
        {
            var (quality, qualityFake) = InterfaceFake<IQualityDefinitionService>.Create();
            qualityFake.On(nameof(IQualityDefinitionService.Get), args => new QualityDefinition((Quality)args[0]));
            var (naming, _) = InterfaceFake<INamingConfigService>.Create();
            var (formats, _) = InterfaceFake<ICustomFormatCalculationService>.Create();
            var folders = withFolders ? new Lazy<ITalearrRecordingFolders>(() => _folders) : null;

            return new FileNameBuilder(naming, quality, new CacheManager(), formats, LogManager.GetCurrentClassLogger(), _world.Resolver, folders);
        }

        private static string Build(FileNameBuilder builder, Edition edition, bool rename, string format = NarratorFormat, string sourceName = "Grisham - The Firm (Abridged) 64k.m4b")
        {
            var config = NamingConfig.Default;
            config.RenameBooks = rename;
            config.StandardBookFormat = format;
            var file = new BookFile { Path = "/downloads/The Firm/" + sourceName, Part = 1, PartCount = 1, Quality = new QualityModel(Quality.M4B), MediaType = "audiobook", Edition = edition };

            return builder.BuildBookFileName(new Author { Name = "John Grisham" }, edition, file, config, new List<CustomFormat>()).Replace('\\', '/');
        }

        [Test]
        public void same_narrator_abridged_copy_gets_its_own_folder_with_renaming_on()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);
            var primary = _world.Editions.First(e => e.Id == 28701);
            primary.Book = _world.Books[3401];

            Assert.That(Build(Builder(), primary, rename: true), Is.EqualTo("The Firm - Scott Brick/The Firm"), "the base row's path never changes");
            Assert.That(Build(Builder(), copy, rename: true), Is.EqualTo("The Firm - Scott Brick (Abridged)/The Firm"));
            Assert.That(Build(Builder(withFolders: false), copy, rename: true), Is.EqualTo("The Firm - Scott Brick/The Firm"), "without the Talearr hook both would share a folder");
        }

        // The edition titles are store listings that used to keep the folders apart by accident;
        // with the book title both reduce to "The Firm - Scott Brick", and the copy still gets
        // the abridged disambiguator.
        [Test]
        public void noisy_edition_titles_use_the_book_title_and_keep_the_copy_disambiguated()
        {
            PrimaryWithScottBrickFiles();
            _world.Editions.First(e => e.Id == 28701).Title = "The Firm (MP3 CD)";
            _world.Editions.First(e => e.Id == 28790).Title = "The Firm [Abridged 5-CD Set]";
            var copy = AddCopy(28790);
            var primary = _world.Editions.First(e => e.Id == 28701);
            primary.Book = _world.Books[3401];

            Assert.That(Build(Builder(), primary, rename: true), Is.EqualTo("The Firm - Scott Brick/The Firm"));
            Assert.That(Build(Builder(), copy, rename: true), Is.EqualTo("The Firm - Scott Brick (Abridged)/The Firm"));
        }

        [Test]
        public void same_narrator_abridged_copy_gets_its_own_folder_with_renaming_off()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);

            Assert.That(Build(Builder(), copy, rename: false), Is.EqualTo("The Firm - Scott Brick (Abridged)/Grisham - The Firm (Abridged) 64k"), "the extension is added by the mover");
        }

        [Test]
        public void a_different_narrator_is_tried_first_when_the_format_has_no_narrator()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28705);

            Assert.That(Build(Builder(), copy, rename: true, format: "{Book Title}/{Book Title}"), Is.EqualTo("The Firm (D.W. Moffett)/The Firm"));
            Assert.That(Build(Builder(), copy, rename: true), Is.EqualTo("The Firm - D.W. Moffett/The Firm"), "the narrator already tells them apart");
        }

        [Test]
        public void the_edition_breaks_the_tie_when_narrator_and_abridgement_match()
        {
            PrimaryWithScottBrickFiles();

            // The UK Scott Brick product: same narrator, both unabridged.
            _world.Editions.First(e => e.Id == 28702).EditionInfo = "Unabridged";
            var copy = AddCopy(28702);

            Assert.That(Build(Builder(), copy, rename: true), Is.EqualTo("The Firm - Scott Brick (2014)/The Firm"));
        }

        [Test]
        public void a_folder_that_already_holds_another_recordings_files_counts_as_taken()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);

            // An older import left the primary's files in the folder the copy would try next.
            _world.Files.Single().Path = PrimaryFolder + " (Abridged)/The Firm.m4b";

            Assert.That(Build(Builder(), copy, rename: true), Is.EqualTo("The Firm - Scott Brick (1993)/The Firm"));
        }

        [Test]
        public void books_with_one_recording_and_ebooks_are_untouched()
        {
            var primary = PrimaryWithScottBrickFiles();
            var edition = _world.Editions.First(e => e.Id == 28701);
            edition.Book = primary;

            Assert.That(_folders.GetContext(edition), Is.Null);
            Assert.That(Build(Builder(), edition, rename: true), Is.EqualTo("The Firm - Scott Brick/The Firm"));
        }

        [Test]
        public void a_flat_layout_has_no_book_folder_to_disambiguate()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);

            Assert.That(Build(Builder(), copy, rename: true, format: "{Book Title}{ - Narrator}"), Is.EqualTo("The Firm - Scott Brick"));
        }

        [Test]
        public void import_refuses_a_folder_holding_another_recordings_files()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);

            var message = _folders.FindFolderConflict(copy.Book, PrimaryFolder);
            Assert.That(message, Does.StartWith("Talearr: " + PrimaryFolder + " already holds the files of another recording of this book (book 3401)"));
            Assert.That(_folders.FindFolderConflict(copy.Book, PrimaryFolder + " (Abridged)"), Is.Null);
            Assert.That(_folders.FindFolderConflict(_world.Books[3401], PrimaryFolder), Is.Null, "the primary imports where it always did");
        }

        [Test]
        public void import_hook_uses_the_planned_destination()
        {
            PrimaryWithScottBrickFiles();
            var copy = AddCopy(28790);
            var (mover, moverFake) = InterfaceFake<IMoveBookFiles>.Create();
            var destination = PrimaryFolder + "/The Firm.m4b";
            moverFake.On(nameof(IMoveBookFiles.GetImportDestinationPath), _ => destination);
            var items = new[] { new LocalBook { Path = "/downloads/The Firm/The Firm.m4b", Quality = new QualityModel(Quality.M4B), Edition = copy, Book = copy.Book } };

            var conflict = TalearrRecordingFolders.FindImportConflict(_folders, mover, copy.Book, copy, new Author { Name = "John Grisham" }, items, LogManager.GetCurrentClassLogger());
            Assert.That(conflict, Does.Contain("already holds the files of another recording"));

            destination = Path.Combine(PrimaryFolder + " (Abridged)", "The Firm.m4b");
            Assert.That(TalearrRecordingFolders.FindImportConflict(_folders, mover, copy.Book, copy, new Author { Name = "John Grisham" }, items, LogManager.GetCurrentClassLogger()), Is.Null);
            Assert.That(TalearrRecordingFolders.FindImportConflict(null, mover, copy.Book, copy, null, items, null), Is.Null, "no service, no check");
        }

        [Test]
        public void resolve_tries_narrator_then_abridgement_then_edition_then_copy_number()
        {
            var primary = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Unabridged", EditionYear = "2009", Asin = "B002V5BSLC", Ordinal = 0 };
            var taken = new List<(string, RecordingFolderKey)> { ("The Firm", primary) };
            var none = new List<(string, RecordingFolderKey)>();

            Assert.That(TalearrRecordingFolders.Resolve("The Firm", new RecordingFolderKey { Narrator = "D.W. Moffett", Abridgement = "Abridged", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm (D.W. Moffett)"));
            Assert.That(TalearrRecordingFolders.Resolve("The Firm", new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Abridged", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm (Abridged)"));
            Assert.That(TalearrRecordingFolders.Resolve("The Firm", new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Unabridged", EditionYear = "2014", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm (2014)"));
            Assert.That(TalearrRecordingFolders.Resolve("The Firm", new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Unabridged", EditionYear = "2009", Asin = "B00NIXU8GW", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm (B00NIXU8GW)"));
            Assert.That(TalearrRecordingFolders.Resolve("The Firm", new RecordingFolderKey { Narrator = "Scott Brick", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm (Copy 2)"));
            Assert.That(TalearrRecordingFolders.Resolve("The Firm - Scott Brick", new RecordingFolderKey { Narrator = "Scott Brick", Ordinal = 1 }, taken, none), Is.EqualTo("The Firm - Scott Brick"), "no collision, no change");
        }

        [Test]
        public void resolve_skips_a_disambiguated_folder_an_earlier_copy_already_took()
        {
            var primary = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Unabridged", EditionYear = "1993", Ordinal = 0 };
            var firstCopy = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Abridged", EditionYear = "1993", Ordinal = 1 };
            var taken = new List<(string, RecordingFolderKey)> { ("Firm", primary), ("Firm (Abridged)", firstCopy) };

            var second = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Abridged", EditionYear = "1995", Ordinal = 2 };
            Assert.That(TalearrRecordingFolders.Resolve("Firm", second, taken, null), Is.EqualTo("Firm (1995)"));

            var identical = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Abridged", EditionYear = "1993", Ordinal = 2 };
            Assert.That(TalearrRecordingFolders.Resolve("Firm", identical, taken, null), Is.EqualTo("Firm (Copy 3)"));
        }

        [Test]
        public void resolve_matches_occupied_absolute_folders_by_their_end()
        {
            var other = new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Unabridged" };
            var occupied = new List<(string, RecordingFolderKey)> { (@"/audiobooks/John Grisham/Series/The Firm", other) };

            Assert.That(TalearrRecordingFolders.Resolve("Series/The Firm", new RecordingFolderKey { Narrator = "Scott Brick", Abridgement = "Abridged", Ordinal = 1 }, null, occupied), Is.EqualTo("Series/The Firm (Abridged)"));
            Assert.That(TalearrRecordingFolders.Resolve("Firm", new RecordingFolderKey { Abridgement = "Abridged", Ordinal = 1 }, null, occupied), Is.EqualTo("Firm"), "only whole folder names match");
        }
    }
}
