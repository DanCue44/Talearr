using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Naming;

namespace Chaptarr.Core.Test.Talearr.Naming
{
    [TestFixture]
    public class TalearrSeriesTitleCleanerFixture
    {
        private const string Viridian = "The Viridian Gate Archives";

        private static readonly string[] ViridianTitles =
        {
            "Viridian Gate Online: Cataclysm",
            "Viridian Gate Online: Crimson Alliance",
            "Darkling Siege",
            "Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5"
        };

        private static List<TalearrTitleSeries> Series(string name, string position)
        {
            return new List<TalearrTitleSeries> { new TalearrTitleSeries(name, position) };
        }

        [TestCase("Viridian Gate Online: Cataclysm", "1", "Cataclysm")]
        [TestCase("Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5", "5", "The Lich Priest")]
        [TestCase("Crimson Alliance", "2", "Crimson Alliance")]
        [TestCase("Darkling Siege", "3", "Darkling Siege")]
        public void should_clean_the_issue_examples(string title, string position, string expected)
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean(title, Series(Viridian, position), ViridianTitles), Is.EqualTo(expected));
        }

        [Test]
        public void should_keep_a_prefix_no_other_book_of_the_series_shares()
        {
            var siblings = new[] { "Viridian Gate Online: Cataclysm", "Darkling Siege" };

            Assert.That(TalearrSeriesTitleCleaner.Clean("Viridian Gate Online: Cataclysm", Series(Viridian, "1"), siblings),
                        Is.EqualTo("Viridian Gate Online: Cataclysm"));
        }

        [Test]
        public void should_not_count_the_same_title_twice_as_a_shared_prefix()
        {
            // The audiobook and the ebook rows of book 1 have the same title.
            var siblings = new[] { "Viridian Gate Online: Cataclysm", "Viridian Gate Online:  Cataclysm" };

            Assert.That(TalearrSeriesTitleCleaner.Clean("Viridian Gate Online: Cataclysm", Series(Viridian, "1"), siblings),
                        Is.EqualTo("Viridian Gate Online: Cataclysm"));
        }

        [TestCase("Mistborn: The Final Empire", "Mistborn", "1", "The Final Empire")]
        [TestCase("The Expanse: Leviathan Wakes", "The Expanse Series", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes: The Expanse, Book 1", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes (The Expanse #1)", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes (Expanse Book 1)", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes [The Expanse, Book 1]", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes, Book 1", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("Leviathan Wakes - The Expanse, Book 1", "The Expanse", "1", "Leviathan Wakes")]
        [TestCase("The Way of Kings: The Stormlight Archive, Book 1", "The Stormlight Archive", "1", "The Way of Kings")]
        [TestCase("Words of Radiance: Stormlight Archive, Book 2", "The Stormlight Archive", "2", "Words of Radiance")]
        [TestCase("Half-Blood Prince: Harry Potter, Book 6", "Harry Potter", "6", "Half-Blood Prince")]
        public void should_remove_the_series(string title, string series, string position, string expected)
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean(title, Series(series, position), null), Is.EqualTo(expected));
        }

        [TestCase("Sapiens: A Brief History of Humankind", "Harari Nonfiction", "1")]
        [TestCase("Leviathan Wakes, Book 2", "The Expanse", "1")]
        [TestCase("Leviathan Wakes: Dune, Book 1", "The Expanse", "1")]
        [TestCase("Leviathan Wakes (The Expanse #2)", "The Expanse", "1")]
        [TestCase("Dune: Part Two", "Dune", "2")]
        [TestCase("Kill Bill: Volume One", "Kill Bill", "1")]
        [TestCase("Mistborn: Part IV", "Mistborn", "4")]
        [TestCase("Viridian: The Civil War", "Unrelated", "1")]
        public void should_leave_a_real_subtitle_or_another_place(string title, string series, string position)
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean(title, Series(series, position), null), Is.EqualTo(title));
        }

        [Test]
        public void should_leave_a_subtitle_no_other_book_shares()
        {
            var siblings = new[] { "Homo Deus: A Brief History of Tomorrow", "21 Lessons for the 21st Century" };

            Assert.That(TalearrSeriesTitleCleaner.Clean("Sapiens: A Brief History of Humankind", Series("Harari Nonfiction", "1"), siblings),
                        Is.EqualTo("Sapiens: A Brief History of Humankind"));
        }

        [Test]
        public void should_leave_titles_of_books_without_a_series()
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean("Sapiens: A Brief History of Humankind", null, new[] { "Sapiens: Other" }),
                        Is.EqualTo("Sapiens: A Brief History of Humankind"));
        }

        [TestCase("Mistborn: 1", "Mistborn: 1")]
        [TestCase("Mistborn: The", "Mistborn: The")]
        [TestCase("Mistborn: #", "Mistborn: #")]
        [TestCase("(Mistborn #1)", "(Mistborn #1)")]
        [TestCase("Viridian Gate Online: 5", "Viridian Gate Online: 5")]
        [TestCase("Viridian Gate Online: Book 1", "Viridian Gate Online")]
        [TestCase("Mistborn: Book 1", "Mistborn")]
        [TestCase("Mistborn - Book 1", "Mistborn")]
        public void should_never_reduce_a_title_to_nothing_or_a_number(string title, string expected)
        {
            var series = new List<TalearrTitleSeries>
            {
                new TalearrTitleSeries("Mistborn", "1"),
                new TalearrTitleSeries(Viridian, "1")
            };

            var result = TalearrSeriesTitleCleaner.Clean(title, series, new[] { "Viridian Gate Online: Cataclysm", "Mistborn: Book 2" });

            Assert.That(result, Is.EqualTo(expected));
        }

        [Test]
        public void should_match_a_suffix_against_any_of_the_series()
        {
            var series = new List<TalearrTitleSeries>
            {
                new TalearrTitleSeries("Cosmere", null),
                new TalearrTitleSeries("Mistborn", "1")
            };

            Assert.That(TalearrSeriesTitleCleaner.Clean("The Final Empire: Mistborn, Book 1", series, null), Is.EqualTo("The Final Empire"));
        }

        [Test]
        public void should_accept_a_named_series_without_a_known_position()
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean("The Final Empire (Mistborn #1)", Series("Mistborn", null), null), Is.EqualTo("The Final Empire"));
        }

        [Test]
        public void should_not_remove_a_bare_book_number_without_a_known_position()
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean("The Final Empire, Book 1", Series("Mistborn", null), null), Is.EqualTo("The Final Empire, Book 1"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void should_pass_empty_titles_through(string title)
        {
            Assert.That(TalearrSeriesTitleCleaner.Clean(title, Series("Mistborn", "1"), null), Is.EqualTo(title));
        }

        [TestCase("Viridian Gate Online: Cataclysm", "Viridian Gate Online")]
        [TestCase("Viridian Gate Online:Cataclysm", null)]
        [TestCase("No colon here", null)]
        [TestCase(": Leading", null)]
        public void should_find_the_prefix(string title, string expected)
        {
            Assert.That(TalearrSeriesTitleCleaner.Prefix(title), Is.EqualTo(expected));
        }

        [TestCase("Viridian Gate", "The Viridian Gate Archives", true)]
        [TestCase("Viridian Gate Online", "The Viridian Gate Archives", false)]
        [TestCase("the expanse", "The Expanse Series", true)]
        [TestCase("The", "The Series", false)]
        public void should_match_series_names_loosely(string text, string series, bool expected)
        {
            Assert.That(TalearrSeriesTitleCleaner.MatchesSeries(text, series), Is.EqualTo(expected));
        }
    }
}
