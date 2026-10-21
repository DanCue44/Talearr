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

namespace Chaptarr.Core.Test.Organizer
{
    [TestFixture]
    public class FileNameBuilderIdentifierTokensFixture
    {
        private class StubNamingConfigService : INamingConfigService
        {
            public NamingConfig GetConfig() => NamingConfig.Default;
            public void Save(NamingConfig namingConfig) { }
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
            public List<CustomFormat> ParseCustomFormat(RemoteBook remoteBook, long size) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(BookFile bookFile) => new();
            public List<CustomFormat> ParseCustomFormat(Blocklist blocklist, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(EntityHistory history, Author artist) => new();
            public List<CustomFormat> ParseCustomFormat(LocalBook localBook) => new();
        }

        private static string Build(Book book, Edition edition, string format)
        {
            var builder = new FileNameBuilder(
                new StubNamingConfigService(),
                new StubQualityDefinitionService(),
                new CacheManager(),
                new StubCustomFormatCalculationService(),
                LogManager.GetCurrentClassLogger());

            var namingConfig = NamingConfig.Default;
            namingConfig.RenameBooks = true;
            namingConfig.StandardBookFormat = format;

            var bookFile = new BookFile { Path = "/books/a.m4b", Quality = new QualityModel(Quality.M4B) };

            return builder.BuildBookFileName(book.Author, edition, bookFile, namingConfig, customFormats: new List<CustomFormat>());
        }

        private static (Book Book, Edition Edition) BadBlood(BookMediaType mediaType)
        {
            var author = new Author { Name = "Jennifer Lynn Barnes" };
            var book = new Book { Author = author, MediaType = mediaType, ISBN13 = "9781484757000", ASIN = "B01BOOKASN" };
            var edition = new Edition
            {
                Title = "Bad Blood",
                Book = book,
                Isbn13 = "978-0316441421",
                Isbn10 = "0316441422",
                Asin = "B0EBOOKASN",
                AudibleASIN = "B0BXMH98NM"
            };

            return (book, edition);
        }

        [Test]
        public void should_render_isbn13_without_hyphens()
        {
            var (book, edition) = BadBlood(BookMediaType.Ebook);

            Assert.That(Build(book, edition, "{Book Title} {ISBN}"), Is.EqualTo("Bad Blood 9780316441421"));
        }

        [Test]
        public void should_fall_back_to_isbn10_then_the_book()
        {
            var (book, edition) = BadBlood(BookMediaType.Ebook);
            edition.Isbn13 = null;
            Assert.That(Build(book, edition, "{ISBN}"), Is.EqualTo("0316441422"));

            edition.Isbn10 = null;
            Assert.That(Build(book, edition, "{ISBN}"), Is.EqualTo("9781484757000"));
        }

        [Test]
        public void should_use_the_audible_asin_for_audiobooks_and_the_store_asin_for_ebooks()
        {
            var (audio, audioEdition) = BadBlood(BookMediaType.Audiobook);
            var (ebook, ebookEdition) = BadBlood(BookMediaType.Ebook);

            Assert.That(Build(audio, audioEdition, "{Book Title} [{ASIN}]"), Is.EqualTo("Bad Blood [B0BXMH98NM]"));
            Assert.That(Build(ebook, ebookEdition, "{Book Title} [{ASIN}]"), Is.EqualTo("Bad Blood [B0EBOOKASN]"));
        }

        [Test]
        public void should_drop_the_optional_block_when_there_is_no_asin()
        {
            var author = new Author { Name = "Jennifer Lynn Barnes" };
            var book = new Book { Author = author, MediaType = BookMediaType.Audiobook };
            var edition = new Edition { Title = "Bad Blood", Book = book };

            Assert.That(Build(book, edition, "{Book Title}{ [ASIN]}"), Is.EqualTo("Bad Blood"));
        }
    }
}
