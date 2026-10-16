using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrAudibleMatcherFixture
    {
        [Test]
        public void candidate_asins_put_the_stored_asin_first_and_drop_isbns()
        {
            var asins = TalearrAudibleMatcher.CandidateAsins(FirmEditions.ScottBrickUs());

            Assert.That(asins.First(), Is.EqualTo("B002PE36VO"));
            Assert.That(asins, Does.Not.Contain("0739366203"));
            Assert.That(asins, Does.Contain("B002V5BSLC"));
            Assert.That(TalearrAudibleMatcher.CandidateAsins(new Edition { Asin = "080442957X" }), Is.Empty);
        }

        [TestCase("D.W. Moffett", "DW Moffett", true)]
        [TestCase("Scott Brick", "scott  brick", true)]
        [TestCase("Johannes Klaußner", "Johannes Klaussner", true)]
        [TestCase("Sergio Zamora", "Sergio Zamora Solà", true)]
        [TestCase("Scott Brick", "D.W. Moffett", false)]
        public void narrator_names_compare_loosely(string a, string b, bool expected)
        {
            Assert.That(TalearrAudibleMatcher.NarratorsMatch(new[] { a }, new[] { b }), Is.EqualTo(expected));
        }

        [Test]
        public void unknown_narrators_are_neither_a_match_nor_a_mismatch()
        {
            Assert.That(TalearrAudibleMatcher.NarratorsMatch(new string[0], new[] { "Scott Brick" }), Is.Null);
        }

        [TestCase(61800, 1030, true)]
        [TestCase(61800, 1080, true)]
        [TestCase(61800, 1100, false)]
        [TestCase(0, 1030, null)]
        [TestCase(61800, null, null)]
        public void runtime_must_be_within_five_percent(int seconds, int? minutes, bool? expected)
        {
            Assert.That(TalearrAudibleMatcher.RuntimeMatches(seconds, minutes), Is.EqualTo(expected));
        }

        [TestCase("eng", "english", true)]
        [TestCase("deu", "german", true)]
        [TestCase("eng", "german", false)]
        [TestCase("und", "english", null)]
        [TestCase("en-US", "english", true)]
        public void languages_compare_across_code_and_name(string edition, string product, bool? expected)
        {
            Assert.That(TalearrAudibleMatcher.LanguageMatches(edition, product), Is.EqualTo(expected));
        }

        [Test]
        public void asin_match_rejects_a_product_read_by_someone_else()
        {
            var edition = FirmEditions.ScottBrickUs();
            var other = Product("B002V5BSLC", "The Firm", "D.W. Moffett", 1030, "english");

            Assert.That(TalearrAudibleMatcher.MatchByAsin(edition, new[] { other }), Is.Null);
        }

        [Test]
        public void search_match_ignores_other_titles_and_languages()
        {
            var edition = FirmEditions.ScottBrickUs();
            var products = new List<AudibleProduct>
            {
                Product("B0BYKFH2RN", "The Exchange", "Scott Brick", 1030, "english"),
                Product("B0GYFFPZDS", "Die Firma", "Scott Brick", 1031, "german"),
                Product("B002V5BSLC", "The Firm", "Scott Brick", 1030, "english")
            };

            var match = TalearrAudibleMatcher.MatchBySearch(edition, "The Firm", products);

            Assert.That(match.Product.Asin, Is.EqualTo("B002V5BSLC"));
            Assert.That(match.Confidence, Is.EqualTo(1.0));
        }

        [Test]
        public void search_match_needs_more_than_a_title()
        {
            var edition = FirmEditions.TitledAbridged();

            var match = TalearrAudibleMatcher.MatchBySearch(edition, "The Firm", new[] { Product("B002V5BSLC", "The Firm", "Scott Brick", 1030, "english") });

            Assert.That(match, Is.Null);
        }

        [Test]
        public void search_match_refuses_a_tie_between_abridged_and_unabridged()
        {
            var edition = new Edition { Title = "Steve Jobs", Language = "eng", NarratorNames = new List<string> { "Dylan Baker" } };
            var unabridged = Product("B005TKKCWC", "Steve Jobs", "Dylan Baker", 1517, "english");
            var abridged = Product("B005TKLCTR", "Steve Jobs", "Dylan Baker", 557, "english");
            abridged.FormatType = "abridged";

            Assert.That(TalearrAudibleMatcher.MatchBySearch(edition, "Steve Jobs", new[] { unabridged, abridged }), Is.Null);
        }

        private static AudibleProduct Product(string asin, string title, string narrator, int minutes, string language)
        {
            return new AudibleProduct
            {
                Asin = asin,
                Marketplace = "us",
                Status = AudibleProductStatus.Ok,
                Title = title,
                FormatType = "unabridged",
                RuntimeMinutes = minutes,
                Narrators = new List<string> { narrator },
                Language = language,
                Publisher = "Random House Audio",
                ReleaseDate = "2009-09-16"
            };
        }
    }
}
