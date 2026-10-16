using NUnit.Framework;
using NzbDrone.Core.MediaFiles.TalearrImport;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    [TestFixture]
    public class ReleaseNameLanguageDetectorFixture
    {
        [TestCase("John.Grisham.The.Pelican.Brief.2021.SWEDiSH.RETAiL.ePub.eBOOK-DECiPHER-xpost", "swe")]
        [TestCase("Walter.Isaacson.Steve.Jobs.2023.DANiSH.RETAiL.ePub.eBOOK-DECiPHER-xpost", "dan")]
        [TestCase("Some Author - Some Book (Dutch) (epub)", "nld")]
        [TestCase("Some.Author.Some.Book.2019.NL.Nederlands.EPUB", "nld")]
        [TestCase("Some Author - Some Book [GER] (Hoerbuch)", "deu")]
        [TestCase("Some.Author.Some.Book.GERMAN.AUDIOBOOK.MP3-GRP", "deu")]
        [TestCase("Some Author - Some Book - Svenska", "swe")]
        [TestCase("Some.Book.FRENCH.EPUB-GRP", "fra")]
        public void should_find_explicit_language_markers_regardless_of_case(string name, string expected)
        {
            Assert.That(ReleaseNameLanguageDetector.Detect(new[] { name }), Is.EquivalentTo(new[] { expected }));
        }

        [TestCase("John.Grisham.The.Pelican.Brief.2010.RETAiL.ePub.eBook-LiBRiCiDE-xpost")]
        [TestCase("Some Author - Some Book (Unabridged) (m4b)")]
        [TestCase("Some.Book.2020.EN.EPUB")]
        [TestCase("Some.Book.2020.DE.FR.IT.EPUB")]
        public void should_find_nothing_without_an_explicit_marker(string name)
        {
            // Two-letter codes are never treated as evidence: too many false positives.
            Assert.That(ReleaseNameLanguageDetector.Detect(new[] { name }), Is.Empty);
        }

        [Test]
        public void should_ignore_language_words_that_are_part_of_the_title()
        {
            var detected = ReleaseNameLanguageDetector.Detect(
                new[] { "Armando.Lucas.Correa.The.German.Girl.2016.RETAIL.EPUB" },
                new[] { "The German Girl", "Armando Lucas Correa" });

            Assert.That(detected, Is.Empty);
        }

        [Test]
        public void should_still_see_a_marker_beside_a_protected_title()
        {
            var detected = ReleaseNameLanguageDetector.Detect(
                new[] { "The.German.Girl.2017.DUTCH.EPUB" },
                new[] { "The German Girl" });

            Assert.That(detected, Is.EquivalentTo(new[] { "nld" }));
        }

        [Test]
        public void should_report_every_language_named()
        {
            Assert.That(ReleaseNameLanguageDetector.Detect(new[] { "Some.Book.English.German.EPUB" }), Is.EquivalentTo(new[] { "eng", "deu" }));
        }
    }
}
