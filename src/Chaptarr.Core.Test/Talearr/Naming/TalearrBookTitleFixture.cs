using System;
using System.Collections.Generic;
using System.IO;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Blocklisting;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Naming;

namespace Chaptarr.Core.Test.Talearr.Naming
{
    // Folder and file names use the book's title, not the edition's store listing.
    [TestFixture]
    public class TalearrBookTitleFixture
    {
        private const string LiveFormat = "{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:smart)}";

        private class StubNamingConfigService : INamingConfigService
        {
            public NamingConfig GetConfig() => NamingConfig.Default;
            public void Save(NamingConfig namingConfig)
            {
            }
        }

        private class StubQualityDefinitionService : IQualityDefinitionService
        {
            public void Update(QualityDefinition qualityDefinition) => throw new NotImplementedException();
            public void UpdateMany(List<QualityDefinition> qualityDefinitions) => throw new NotImplementedException();
            public List<QualityDefinition> All() => throw new NotImplementedException();
            public QualityDefinition GetById(int id) => throw new NotImplementedException();
            public QualityDefinition Get(Quality quality) => new QualityDefinition(quality);
        }

        private class StubCustomFormatCalculationService : ICustomFormatCalculationService
        {
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new ();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new ();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new ();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new ();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new ();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new ();
        }

        private FileNameBuilder _builder;
        private Author _orwell;

        [SetUp]
        public void SetUp()
        {
            _builder = new FileNameBuilder(new StubNamingConfigService(), new StubQualityDefinitionService(), new CacheManager(), new StubCustomFormatCalculationService(), LogManager.GetCurrentClassLogger());
            _orwell = new Author { Name = "George Orwell" };
        }

        // The live import on 2026-10-16: book row 5002 is "1984", its monitored edition 39500 is
        // "Nineteen Eighty-Four (MP3 Book)" read by "Samuel  West" (two spaces in the metadata).
        private Edition Live1984(string bookTitle = "1984", string editionTitle = "Nineteen Eighty-Four (MP3 Book)")
        {
            var book = new Book { Id = 5002, Title = bookTitle, Author = _orwell, MediaType = BookMediaType.Audiobook };
            return new Edition { Id = 39500, BookId = 5002, Title = editionTitle, Book = book, Narrator = "Samuel  West", NarratorNames = new List<string> { "Samuel  West" }, Format = "audiobook" };
        }

        private static BookFile Mp3(string path = "/downloads/Orwell G. - 1984 (West S.) - 2010 (96kbps)/Chapter 1-2.mp3", int part = 1, int partCount = 1)
        {
            return new BookFile { Path = path, Part = part, PartCount = partCount, Quality = new QualityModel(Quality.MP3), MediaType = "audiobook" };
        }

        private string Build(Edition edition, BookFile file, bool rename, string format = LiveFormat)
        {
            var config = NamingConfig.Default;
            config.RenameBooks = rename;
            config.StandardBookFormat = format;

            return _builder.BuildBookFileName(_orwell, edition, file, config, new List<CustomFormat>()).Replace('\\', '/');
        }

        [Test]
        public void live_1984_import_gets_the_book_title_folder()
        {
            Assert.That(Build(Live1984(), Mp3(), rename: false), Is.EqualTo("1984 - Samuel West/Chapter 1-2"));
        }

        [Test]
        public void live_1984_import_with_renaming_on_names_files_by_the_book_title_too()
        {
            Assert.That(Build(Live1984(), Mp3(), rename: true), Is.EqualTo("1984 - Samuel West/1984"));
            Assert.That(Build(Live1984(), Mp3(part: 3, partCount: 10), rename: true), Is.EqualTo("1984 - Samuel West/1984 (03)"));
        }

        [Test]
        public void edition_title_is_used_without_noise_when_the_book_has_no_title()
        {
            Assert.That(Build(Live1984(bookTitle: null), Mp3(), rename: true), Is.EqualTo("Nineteen Eighty-Four - Samuel West/Nineteen Eighty-Four"));

            var detached = Live1984();
            detached.Book = null;
            Assert.That(Build(detached, Mp3(), rename: true), Is.EqualTo("Nineteen Eighty-Four - Samuel West/Nineteen Eighty-Four"));
        }

        [Test]
        public void every_title_token_uses_the_book_title()
        {
            var edition = Live1984(bookTitle: "The Road to Wigan Pier (Audible Audio)", editionTitle: "The Road to Wigan Pier: Penguin Modern Classics (Audible Audio)");

            Assert.That(Build(edition, Mp3(), true, "{Book Title}"), Is.EqualTo("The Road to Wigan Pier"));
            Assert.That(Build(edition, Mp3(), true, "{Book CleanTitle}"), Is.EqualTo("The Road to Wigan Pier"));
            Assert.That(Build(edition, Mp3(), true, "{Book TitleThe}"), Is.EqualTo("Road to Wigan Pier, The"));
            Assert.That(Build(edition, Mp3(), true, "{Book TitleNoSub}"), Is.EqualTo("The Road to Wigan Pier"));
        }

        [Test]
        public void ebook_names_use_the_book_title_as_well()
        {
            var book = new Book { Title = "The Client", Author = new Author { Name = "John Grisham" }, MediaType = BookMediaType.Ebook };
            var edition = new Edition { Title = "The Client: A Novel", Book = book, Format = "ebook", IsEbook = true };
            var file = new BookFile { Path = "/downloads/The Client.epub", Part = 1, PartCount = 1, Quality = new QualityModel(Quality.EPUB), MediaType = "ebook" };

            var config = NamingConfig.Default;
            config.EbookRenameBooks = true;
            config.EbookStandardBookFormat = "{Book Title}/{Book Title}";

            Assert.That(_builder.BuildBookFileName(book.Author, edition, file, config, new List<CustomFormat>()).Replace('\\', '/'), Is.EqualTo("The Client/The Client"));
        }

        [TestCase("Nineteen Eighty-Four (MP3 Book)", "Nineteen Eighty-Four")]
        [TestCase("1984 (Audio Cassette)", "1984")]
        [TestCase("Animal Farm (Audible Audio)", "Animal Farm")]
        [TestCase("The Client (Unabridged)", "The Client")]
        [TestCase("A Painted House [Unabridged]", "A Painted House")]
        [TestCase("Bleak House [MP3]", "Bleak House")]
        [TestCase("Coming Up for Air (MP3 CD)", "Coming Up for Air")]
        [TestCase("The Firm (Audio CD)", "The Firm")]
        [TestCase("The Firm (CD)", "The Firm")]
        [TestCase("Chamber [Audiobook]", "Chamber")]
        [TestCase("In Cold Blood (Audiobook)", "In Cold Blood")]
        [TestCase("The King of Torts [Abridged 5-CD Set]", "The King of Torts")]
        [TestCase("The Testament Books - (Books on Tapes) (9 Cassette Tapes)", "The Testament Books")]
        [TestCase("A Time to Kill & The King of Torts [Unabridged] [Audible Audio Edition]", "A Time to Kill & The King of Torts")]
        [TestCase("The Racketeer by Grisham, John (Abridged Edition) [AudioCD(2012)]", "The Racketeer by Grisham, John")]
        [TestCase("[Compact Disc - Unabridged] ANIMAL FARM (Audio CD)", "ANIMAL FARM")]
        [TestCase("True Crime Stories, Volume 5: 12 Shocking True Crime Murder Cases (Unabridged).", "True Crime Stories, Volume 5: 12 Shocking True Crime Murder Cases.")]
        [TestCase("Animal Farm Unabridged (Audio Cassette)", "Animal Farm")]
        [TestCase("The Street Lawyer COMPLETE AND UNABRIDGED", "The Street Lawyer")]
        [TestCase("Dune (Unabridged Library Edition)", "Dune")]
        [TestCase("Dune (Playaway)", "Dune")]
        public void format_noise_is_stripped(string title, string expected)
        {
            Assert.That(TalearrBookTitle.Strip(title), Is.EqualTo(expected));
        }

        [TestCase("Murderous Minds (Murderous Minds Collection, Book 1)")]
        [TestCase("Dune (Book 1)")]
        [TestCase("1984 (French Edition)")]
        [TestCase("Lightlark (Special Edition)")]
        [TestCase("Animal Farm [Dramatised]")]
        [TestCase("Animal Farm (1539)")]
        [TestCase("A Capote Reader (Penguin Modern Classics)")]
        [TestCase("1984 (Nineteen Eighty-Four)")]
        [TestCase("The Audiobook")]
        [TestCase("Unabridged")]
        [TestCase("Audio")]
        public void real_title_parts_stay(string title)
        {
            Assert.That(TalearrBookTitle.Strip(title), Is.EqualTo(title));
        }

        [Test]
        public void a_title_made_only_of_noise_is_kept()
        {
            Assert.That(TalearrBookTitle.Strip("(Audiobook)"), Is.EqualTo("(Audiobook)"));
            Assert.That(TalearrBookTitle.Strip(null), Is.Null);
            Assert.That(TalearrBookTitle.Strip(" "), Is.EqualTo(" "));
        }

        [Test]
        public void book_title_wins_over_the_edition_title()
        {
            Assert.That(TalearrBookTitle.ForNaming(Live1984()), Is.EqualTo("1984"));
            Assert.That(TalearrBookTitle.ForNaming(new Book { Title = "Animal Farm" }, new Edition { Title = "Animal Farm: A Fairy Story (Audible Audio)" }), Is.EqualTo("Animal Farm"));
            Assert.That(TalearrBookTitle.ForNaming(null, new Edition { Title = "Animal Farm (Audiobook)" }), Is.EqualTo("Animal Farm"));
            Assert.That(TalearrBookTitle.ForNaming(null), Is.Null);
        }
    }
}
