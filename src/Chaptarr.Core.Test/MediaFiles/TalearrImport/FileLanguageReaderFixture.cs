using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TalearrImport;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    [TestFixture]
    public class FileLanguageReaderFixture
    {
        private string _root;
        private FileLanguageReader _subject;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _subject = new FileLanguageReader(LogManager.GetCurrentClassLogger());
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

        private string Write(string name, byte[] data)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        [TestCase("sv", "swe")]
        [TestCase("nl-NL", "nld")]
        [TestCase("en-US", "eng")]
        [TestCase("eng", "eng")]
        [TestCase("dut", "nld")]
        public void should_read_epub_dc_language(string declared, string expected)
        {
            var evidence = _subject.Read(Write("book.epub", TestArchives.Epub(declared)));

            Assert.That(evidence.Languages, Is.EquivalentTo(new[] { expected }));
            Assert.That(evidence.Source, Does.Contain("EPUB"));
        }

        [TestCase(null)]
        [TestCase("und")]
        [TestCase("mul")]
        [TestCase("")]
        public void should_treat_missing_or_non_language_epub_values_as_no_evidence(string declared)
        {
            var evidence = _subject.Read(Write("book.epub", TestArchives.Epub(declared)));

            Assert.That(evidence.HasLanguage, Is.False);
        }

        [Test]
        public void should_return_no_evidence_for_an_unreadable_epub()
        {
            var evidence = _subject.Read(Write("book.epub", new byte[] { 1, 2, 3 }));

            Assert.That(evidence.HasLanguage, Is.False);
        }

        [TestCase("book.mobi", "nl", "nld")]
        [TestCase("book.azw3", "sv", "swe")]
        [TestCase("book.azw3", "en-GB", "eng")]
        public void should_read_the_mobi_exth_language(string fileName, string declared, string expected)
        {
            var evidence = _subject.Read(Write(fileName, TestArchives.Mobi(declared)));

            Assert.That(evidence.Languages, Is.EquivalentTo(new[] { expected }));
            Assert.That(evidence.Source, Does.Contain("MOBI"));
        }

        [Test]
        public void should_return_no_evidence_for_a_mobi_without_a_language()
        {
            var evidence = _subject.Read(Write("book.mobi", TestArchives.Mobi(null)));

            Assert.That(evidence.HasLanguage, Is.False);
        }

        [Test]
        public void should_read_the_pdf_catalog_language()
        {
            var evidence = _subject.Read(Write("book.pdf", TestArchives.Pdf("nl-NL")));

            Assert.That(evidence.Languages, Is.EquivalentTo(new[] { "nld" }));
            Assert.That(evidence.Source, Does.Contain("PDF"));
        }

        [Test]
        public void should_fall_back_to_the_pdf_xmp_language()
        {
            var evidence = _subject.Read(Write("book.pdf", TestArchives.Pdf(null, "sv-SE")));

            Assert.That(evidence.Languages, Is.EquivalentTo(new[] { "swe" }));
        }

        [Test]
        public void should_return_no_evidence_for_a_pdf_without_language_metadata()
        {
            var evidence = _subject.Read(Write("book.pdf", TestArchives.Pdf(null)));

            Assert.That(evidence.HasLanguage, Is.False);
        }

        [Test]
        public void should_read_audio_language_tags()
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID3v2:TIT2"] = new List<string> { "Chapter 1" },
                ["ID3v2:TLAN"] = new List<string> { "ger" }
            };

            var evidence = _subject.Read(Path.Combine(_root, "01.mp3"), tags);

            Assert.That(evidence.Languages, Is.EquivalentTo(new[] { "deu" }));
            Assert.That(evidence.Source, Does.Contain("audio"));
        }

        [TestCase("MP4:----:com.apple.iTunes:LANGUAGE", "Dutch", "nld")]
        [TestCase("XIPH:LANGUAGE", "sv", "swe")]
        [TestCase("ASF:WM/Language", "en-US", "eng")]
        [TestCase("LANGUAGE", "eng; fre", "eng,fra")]
        public void should_read_the_common_audio_language_tag_spellings(string key, string value, string expected)
        {
            var tags = new Dictionary<string, List<string>> { [key] = new List<string> { value } };

            var evidence = _subject.Read(Path.Combine(_root, "book.m4b"), tags);

            Assert.That(evidence.Languages, Is.EquivalentTo(expected.Split(',')));
        }

        [Test]
        public void should_ignore_audio_files_without_language_tags()
        {
            var tags = new Dictionary<string, List<string>> { ["ID3v2:TIT2"] = new List<string> { "Swedish Chapter" } };

            Assert.That(_subject.Read(Path.Combine(_root, "01.mp3"), tags).HasLanguage, Is.False);
            Assert.That(_subject.Read(Path.Combine(_root, "01.mp3"), null).HasLanguage, Is.False);
        }

        [Test]
        public void should_ignore_garbage_language_values()
        {
            Assert.That(FileLanguageReader.Canonicalize("XXX").ToList(), Is.Empty);
            Assert.That(FileLanguageReader.Canonicalize("not a language").ToList(), Is.Empty);
            Assert.That(FileLanguageReader.Canonicalize("und").ToList(), Is.Empty);
        }
    }
}
