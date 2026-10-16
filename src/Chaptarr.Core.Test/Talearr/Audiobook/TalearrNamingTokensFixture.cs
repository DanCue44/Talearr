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
using NzbDrone.Core.Talearr.Audiobook;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class TalearrNamingTokensFixture
    {
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
        private Author _author;

        [SetUp]
        public void SetUp()
        {
            _builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            _author = new Author { Name = "Terry Goodkind" };
        }

        private Edition Edition(string series = "Sword of Truth", string position = "1", int? year = 1994, string narrator = "Sam Tsoutsouvas", string title = "Wizards First Rule")
        {
            var book = new Book
            {
                Title = title,
                Author = _author,
                SeriesName = series,
                SeriesPosition = position,
                ReleaseDate = year.HasValue ? new DateTime(year.Value, 8, 15) : null
            };

            return new Edition { Title = title, Book = book, Narrator = narrator };
        }

        private static BookFile File(string path = "/downloads/Wizards First Rule.m4b", int part = 1, int partCount = 1)
        {
            return new BookFile
            {
                Path = path,
                Part = part,
                PartCount = partCount,
                Quality = new QualityModel(Path.GetExtension(path) == ".m4b" ? Quality.M4B : Quality.MP3),
                MediaType = "audiobook"
            };
        }

        private string Build(string format, Edition edition, BookFile file, bool rename = true)
        {
            var config = NamingConfig.Default;
            config.RenameBooks = rename;
            config.StandardBookFormat = format;

            return _builder.BuildBookFileName(_author, edition, file, config, new List<CustomFormat>());
        }

        [TestCase("1", "00", "01")]
        [TestCase("3", "000", "003")]
        [TestCase("12", "0", "12")]
        [TestCase("2.5", "00", "02.5")]
        [TestCase("007", "00", "07")]
        [TestCase("1-3", "00", "1-3")]
        [TestCase("Prequel", "00", "Prequel")]
        [TestCase("4", null, "4")]
        public void series_position_is_zero_padded(string position, string format, string expected)
        {
            Assert.That(TalearrNamingTokens.FormatSeriesPosition(position, format), Is.EqualTo(expected));
        }

        [Test]
        public void series_position_token_takes_a_padding_format()
        {
            Assert.That(Build("{Book SeriesPosition:00}", Edition(position: "3"), File()), Is.EqualTo("03"));
            Assert.That(Build("{Book SeriesPosition}", Edition(position: "3"), File()), Is.EqualTo("3"));
        }

        [Test]
        public void audiobookshelf_layout_builds_series_volume_year_and_narrator_folder()
        {
            var result = Build(TalearrNamingTokens.AudiobookshelfFormat, Edition(), File());

            Assert.That(result, Is.EqualTo(Path.Combine("Sword of Truth", "01 - 1994 - Wizards First Rule {Sam Tsoutsouvas}", "Wizards First Rule")));
        }

        [Test]
        public void audiobookshelf_layout_drops_series_parts_for_a_standalone_book()
        {
            var result = Build(TalearrNamingTokens.AudiobookshelfFormat, Edition(series: null, position: null, year: null, narrator: null, title: "In Cold Blood"), File());

            Assert.That(result, Is.EqualTo(Path.Combine("In Cold Blood", "In Cold Blood")));
        }

        [Test]
        public void audiobookshelf_layout_numbers_multi_part_files()
        {
            var result = Build(TalearrNamingTokens.AudiobookshelfFormat, Edition(), File("/downloads/x/track07.mp3", 7, 12));

            Assert.That(result, Is.EqualTo(Path.Combine("Sword of Truth", "01 - 1994 - Wizards First Rule {Sam Tsoutsouvas}", "Wizards First Rule (07)")));
        }

        [TestCase("{Book Series}/{Book SeriesPosition:00 - }{Book Title}/{Book Title}{ (PartNumber:smart)}", "Sword of Truth/01 - Wizards First Rule/Wizards First Rule")]
        [TestCase("{Book Title}{ {Narrator}}/{Book Title}{ (PartNumber:smart)}", "Wizards First Rule {Sam Tsoutsouvas}/Wizards First Rule")]
        public void other_audiobook_layouts_from_the_naming_ui(string format, string expected)
        {
            Assert.That(Build(format, Edition(), File()), Is.EqualTo(expected.Replace('/', Path.DirectorySeparatorChar)));
        }

        [Test]
        public void book_year_is_empty_when_unknown_so_the_separator_goes_too()
        {
            Assert.That(Build("{Book Year - }{Book Title}", Edition(year: null), File()), Is.EqualTo("Wizards First Rule"));
            Assert.That(Build("{Book Year - }{Book Title}", Edition(year: 1994), File()), Is.EqualTo("1994 - Wizards First Rule"));
        }

        [Test]
        public void book_year_falls_back_to_the_edition_year()
        {
            var edition = Edition(year: null);
            edition.ReleaseDate = new DateTime(2011, 10, 24);

            Assert.That(Build("{Book Year}", edition, File()), Is.EqualTo("2011"));
        }

        [Test]
        public void abridged_tokens_follow_the_edition()
        {
            var abridged = Edition();
            abridged.EditionInfo = "Abridged";
            Assert.That(Build("{Book Title}{ (Abridged)}", abridged, File()), Is.EqualTo("Wizards First Rule (Abridged)"));
            Assert.That(Build("{Book Title}{ (Abridgement)}", abridged, File()), Is.EqualTo("Wizards First Rule (Abridged)"));

            var unabridged = Edition();
            unabridged.Format = "Unabridged Audiobook";
            Assert.That(Build("{Book Title}{ (Abridged)}", unabridged, File()), Is.EqualTo("Wizards First Rule"));
            Assert.That(Build("{Book Title}{ (Abridgement)}", unabridged, File()), Is.EqualTo("Wizards First Rule (Unabridged)"));

            var unknown = Edition();
            Assert.That(Build("{Book Title}{ (Abridgement)}", unknown, File()), Is.EqualTo("Wizards First Rule"));
        }

        [Test]
        public void abridged_by_runtime_against_other_editions()
        {
            var full = new Edition { Title = "Wizards First Rule", DurationSeconds = 34 * 3600, Language = "eng" };
            var shortEdition = Edition();
            shortEdition.DurationSeconds = 6 * 3600;
            shortEdition.Language = "eng";
            shortEdition.Book.Editions = new List<Edition> { full, shortEdition };

            Assert.That(Build("{Abridgement}", shortEdition, File()), Is.EqualTo("Abridged"));
        }

        [Test]
        public void narrator_falls_back_to_the_file_narrator()
        {
            var file = File();
            file.Narrator = "Sam Tsoutsouvas";

            Assert.That(Build("{Book Title}{ {Narrator}}", Edition(narrator: null), file), Is.EqualTo("Wizards First Rule {Sam Tsoutsouvas}"));
        }

        [Test]
        public void disc_folder_files_keep_original_names_with_their_part_number_when_renaming_is_off()
        {
            // Real layout: Orwell G. - 1984 (West S.) - 2010 (96kbps)/Part 1|2|3/Chapter 1-2.mp3
            var part1 = File("/downloads/1984/Part 1/Chapter 1-2.mp3", 1, 11);
            var part3 = File("/downloads/1984/Part 3/Chapter 1-2.mp3", 9, 11);

            var a = Build(NamingConfig.Default.StandardBookFormat, Edition(series: null, title: "1984"), part1, rename: false);
            var b = Build(NamingConfig.Default.StandardBookFormat, Edition(series: null, title: "1984"), part3, rename: false);

            Assert.That(Path.GetFileName(a), Is.EqualTo("01 - Chapter 1-2"));
            Assert.That(Path.GetFileName(b), Is.EqualTo("09 - Chapter 1-2"));
            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void flat_folder_files_keep_their_original_names()
        {
            var file = File("/downloads/Steve Jobs/101 - Walter Isaacson - Steve Jobs - 2011 - Introduction To Series.m4a", 1, 75);

            var result = Build(NamingConfig.Default.StandardBookFormat, Edition(series: null, title: "Steve Jobs"), file, rename: false);

            Assert.That(Path.GetFileName(result), Is.EqualTo("101 - Walter Isaacson - Steve Jobs - 2011 - Introduction To Series"));
        }

        [Test]
        public void disc_folder_prefix_also_applies_when_the_format_has_no_part_number()
        {
            var file = File("/downloads/Book/CD2/Track 01.mp3", 13, 24);

            var result = Build("{Book Title}/{Book Title}", Edition(series: null, title: "Book"), file, rename: true);

            Assert.That(Path.GetFileName(result), Is.EqualTo("13 - Track 01"));
        }

        [Test]
        public void a_title_folder_ending_in_part_is_not_a_disc_folder()
        {
            var file = File("/downloads/The Dark Tower Part Two/01.mp3", 1, 5);

            Assert.That(TalearrNamingTokens.GetOriginalFileName(file), Is.EqualTo("01"));
        }

        [Test]
        public void single_files_are_never_prefixed()
        {
            Assert.That(TalearrNamingTokens.GetOriginalFileName(File("/downloads/Book/CD1/Book.m4b")), Is.EqualTo("Book"));
        }
    }
}
