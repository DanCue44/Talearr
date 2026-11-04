using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Ebook.Epub;

namespace Chaptarr.Core.Test.Talearr.Ebook
{
    [TestFixture]
    public class TalearrEpubConverterFixture
    {
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-epub-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private string Zip(params (string Name, string Text)[] entries)
        {
            var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".epub");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in entries)
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                    writer.Write(text);
                }
            }

            return path;
        }

        [Test]
        public void a_complete_epub_passes()
        {
            var path = Zip(("mimetype", "application/epub+zip"),
                           ("META-INF/container.xml", "<container/>"),
                           ("OEBPS/content.opf", "<package/>"),
                           ("OEBPS/Text/part0000.xhtml", "<html/>"));

            Assert.That(TalearrEpubConverter.CheckEpub(path), Is.Null);
        }

        [TestCase("mimetype")]
        [TestCase("META-INF/container.xml")]
        [TestCase("OEBPS/content.opf")]
        [TestCase("OEBPS/Text/part0000.xhtml")]
        public void an_epub_missing_a_part_fails(string missing)
        {
            var entries = new[]
            {
                ("mimetype", "application/epub+zip"),
                ("META-INF/container.xml", "<container/>"),
                ("OEBPS/content.opf", "<package/>"),
                ("OEBPS/Text/part0000.xhtml", "<html/>")
            };

            var path = Zip(Array.FindAll(entries, e => e.Item1 != missing));

            Assert.That(TalearrEpubConverter.CheckEpub(path), Is.Not.Null);
        }

        [Test]
        public void a_file_that_is_not_a_zip_fails()
        {
            var path = Path.Combine(_folder, "book.epub");
            File.WriteAllText(path, "not a zip");

            Assert.That(TalearrEpubConverter.CheckEpub(path), Is.EqualTo("it is not a zip file"));
        }

        [Test]
        public void kindle_extensions_are_convertible_and_others_are_not()
        {
            Assert.That(TalearrEpubConverter.ConvertibleExtensions.Contains(".AZW3"), Is.True);
            Assert.That(TalearrEpubConverter.ConvertibleExtensions.Contains(".mobi"), Is.True);
            Assert.That(TalearrEpubConverter.ConvertibleExtensions.Contains(".pdf"), Is.False);
            Assert.That(TalearrEpubConverter.ConvertibleExtensions.Contains(".epub"), Is.False);
        }
    }
}
