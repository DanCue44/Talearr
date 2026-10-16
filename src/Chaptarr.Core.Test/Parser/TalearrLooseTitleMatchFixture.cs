using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace Chaptarr.Core.Test.Parser
{
    // TaleArr: looser release title matching (subtitles, articles, series prefixes and suffixes)
    // without matching across different books by the same author. Release names are real-world
    // shapes taken from indexers and from the TaleArr test server's history.
    [TestFixture]
    public class TalearrLooseTitleMatchFixture
    {
        private static Book CreateBook(string author, string title, string seriesName = null, string seriesPosition = null, string monitoredEditionTitle = null)
        {
            return new Book
            {
                Title = title,
                Author = new Author { Name = author },
                SeriesName = seriesName,
                SeriesPosition = seriesPosition,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, Title = monitoredEditionTitle ?? title, Monitored = true }
                }
            };
        }

        private static TitleMatchResult Match(string release, Book target, params Book[] catalog)
        {
            var authorCatalog = catalog.Length == 0 ? new[] { target } : catalog;
            return ReleaseTitleMatchScorer.FindBestMatch(release, target.Author.Name, new[] { target }, target.Author.Name, authorCatalog);
        }

        [TestCase("Ryan Becker", "Murderous Minds: Stories of Real Life Murderers That Escaped the Headlines", "Ryan Becker - Murderous Minds (2017) [Audiobook] [MP3]")]

        [TestCase("Truman Capote", "In Cold Blood: A True Account of a Multiple Murder and Its Consequences", "Truman Capote - In Cold Blood [M4B] {Scott Brick}")]
        [TestCase("Michael Parenti", "Blackshirts and Reds: Rational Fascism and the Overthrow of Communism", "Michael Parenti - Blackshirts & Reds (1997) MP3")]
        [TestCase("Ryan Becker", "True Crime Stories: 48 Terrifying True Crime Murder Cases", "Ryan Becker - True Crime Stories (epub)")]
        [TestCase("Andy Weir", "Project Hail Mary: A Novel", "Andy Weir - Project Hail Mary (2021) [M4B]")]
        [TestCase("Walter Isaacson", "Steve Jobs: The Exclusive Biography", "Steve.Jobs.by.Walter.Isaacson.EPUB-xpost")]
        public void should_match_release_without_the_subtitle(string author, string title, string release)
        {
            var result = Match(release, CreateBook(author, title));

            Assert.That(result, Is.Not.Null);
            Assert.That(result.IsMatch, Is.True, $"problems: {string.Join(", ", result.Problems.Select(p => p.Code + ":" + p.Value))}");
        }

        [TestCase("John Grisham", "The Pelican Brief", "John Grisham - Pelican Brief, The (retail) (epub)")]
        [TestCase("Ali Hazelwood", "The Love Hypothesis", "Ali Hazelwood - Love Hypothesis (2021) [Audiobook]")]
        [TestCase("R. F. Kuang", "The Poppy War", "R.F. Kuang - [Poppy War 1] - Poppy War (m4b)")]
        [TestCase("John Grisham", "The Firm", "John Grisham - Firm, The (1991) epub")]
        public void should_match_moved_or_missing_leading_article(string author, string title, string release)
        {
            var result = Match(release, CreateBook(author, title));

            Assert.That(result?.IsMatch, Is.True);
        }

        [Test]
        public void should_match_series_prefixed_title_released_without_the_series()
        {
            var book = CreateBook("Brandon Sanderson", "Mistborn: The Final Empire", "Mistborn", "1");

            Assert.That(Match("Brandon Sanderson - The Final Empire (2006) [m4b]", book)?.IsMatch, Is.True);
        }

        [TestCase("Alex.Aster.-.Skyshade.Lightlark.3.2024.RETAIL.EPUB.eBook-CTO")]
        [TestCase("Alex Aster - Skyshade (Lightlark, #3) epub")]
        public void should_match_title_with_series_suffix(string release)
        {
            var skyshade = CreateBook("Alex Aster", "Skyshade", "Lightlark", "3");
            var lightlark = CreateBook("Alex Aster", "Lightlark", "Lightlark", "1");

            Assert.That(Match(release, skyshade, skyshade, lightlark)?.IsMatch, Is.True);
            Assert.That(Match(release, lightlark, skyshade, lightlark)?.IsMatch, Is.False);
        }

        [Test]
        public void should_match_book_whose_title_carries_a_series_subtitle()
        {
            var bride = CreateBook("Ali Hazelwood", "Bride", "Bride", "1");
            var mate = CreateBook("Ali Hazelwood", "Mate: Bride, Vol. 2", "Bride", "2");

            Assert.That(Match("Ali Hazelwood - Mate (Bride #2) [m4b]", mate, bride, mate)?.IsMatch, Is.True);
            Assert.That(Match("Ali Hazelwood - Mate (Bride #2) [m4b]", bride, bride, mate)?.IsMatch, Is.False);
        }

        [Test]
        public void should_not_match_series_name_followed_by_series_marker()
        {
            var bride = CreateBook("Ali Hazelwood", "Bride", "Bride", "1");
            var mate = CreateBook("Ali Hazelwood", "Mate: Bride, Vol. 2", "Bride", "2");
            const string release = "Ali.Hazelwood.-.Bride.Series.-.02.-.Mate.[AUDIOBOOK]-xpost";

            var brideResult = Match(release, bride, bride, mate);

            Assert.That(brideResult.IsMatch, Is.False);
            Assert.That(brideResult.Problems.Select(p => p.Code), Does.Contain(TitleMatchProblemCode.SeriesNameOnly));
            Assert.That(Match(release, mate, bride, mate)?.IsMatch, Is.True);
        }

        [Test]
        public void should_not_use_a_short_title_shared_by_several_books_of_the_author()
        {
            var fortyEight = CreateBook("Ryan Becker", "True Crime Stories: 48 Terrifying True Crime Murder Cases");
            var twelve = CreateBook("Ryan Becker", "True Crime Stories: 12 Terrifying True Crime Murder Cases");
            const string release = "Ryan Becker - True Crime Stories (epub)";

            Assert.That(Match(release, fortyEight, fortyEight, twelve)?.IsMatch ?? false, Is.False);
            Assert.That(Match(release, twelve, fortyEight, twelve)?.IsMatch ?? false, Is.False);
        }

        [Test]
        public void should_keep_full_title_release_on_the_right_book_when_short_forms_collide()
        {
            var fortyEight = CreateBook("Ryan Becker", "True Crime Stories: 48 Terrifying True Crime Murder Cases");
            var twelve = CreateBook("Ryan Becker", "True Crime Stories: 12 Terrifying True Crime Murder Cases");
            const string release = "Ryan Becker - True Crime Stories - 12 Terrifying True Crime Murder Cases (epub)";

            Assert.That(Match(release, twelve, fortyEight, twelve)?.IsMatch, Is.True);
            Assert.That(Match(release, fortyEight, fortyEight, twelve)?.IsMatch ?? false, Is.False);
        }

        [TestCase("Ryan Becker - True Crime Stories 12 Terrifying True Crime Murder Cases (epub)", TitleMatchProblemCode.SubtitleMismatch)]
        [TestCase("Ryan Becker - True Crime Stories - Serial Killers Edition (epub)", TitleMatchProblemCode.SubtitleMismatch)]
        [TestCase("Ryan Becker - True Crime Stories Volume 3 (2018) m4b", TitleMatchProblemCode.SubtitleMismatch)]
        public void should_reject_a_different_subtitle_or_volume_after_a_short_title_match(string release, TitleMatchProblemCode expected)
        {
            var book = CreateBook("Ryan Becker", "True Crime Stories: 48 Terrifying True Crime Murder Cases");

            var result = Match(release, book);

            Assert.That(result.IsMatch, Is.False);
            Assert.That(result.Problems.Select(p => p.Code), Does.Contain(expected));
        }

        [Test]
        public void should_reject_subtitle_mismatch_even_when_balanced_strictness_tolerates_adjacent_numbers()
        {
            var book = CreateBook("Ryan Becker", "True Crime Stories: 48 Terrifying True Crime Murder Cases");
            var result = Match("Ryan Becker - True Crime Stories 12 Terrifying True Crime Murder Cases (epub)", book);

            Assert.That(
                NzbDrone.Core.DecisionEngine.Specifications.ReleaseTitleMatchSpecification.IsAcceptedMatch(result, null, NzbDrone.Core.Configuration.BookMatchingStrictness.Balanced),
                Is.False);
        }

        [TestCase("Ali Hazelwood - Not in Love (2024) epub", "Not in Love", "Deep End")]
        [TestCase("Truman Capote - Breakfast at Tiffany's (epub)", "Breakfast at Tiffany's", "In Cold Blood")]
        [TestCase("Ali Hazelwood - Love Hypothesis (2021) [Audiobook]", "The Love Hypothesis", "Love, Theoretically")]
        public void should_not_match_a_different_book_by_the_same_author(string release, string rightTitle, string wrongTitle)
        {
            var right = CreateBook("Author", rightTitle);
            var wrong = CreateBook("Author", wrongTitle);
            right.Author.Name = wrong.Author.Name = release.Split(" - ")[0];

            Assert.That(Match(release, right, right, wrong)?.IsMatch, Is.True);
            Assert.That(Match(release, wrong, right, wrong)?.IsMatch ?? false, Is.False);
        }

        // Book 3129 "The Firm" on the TaleArr test server. Its monitored edition was
        // "The Firm: A Novel (The Firm Series Book 1)" when automatic searches grabbed nothing,
        // and interactive search later approved the sequel and a series listing.
        private static (Book Firm, Book[] Catalog) FirmCatalog(string monitoredTitle, bool withSeries)
        {
            var firm = CreateBook("John Grisham", "The Firm", withSeries ? "The Firm" : null, withSeries ? "1" : null, monitoredTitle);
            var exchange = CreateBook("John Grisham", "The Exchange", withSeries ? "The Firm" : null, withSeries ? "2" : null, "The Exchange: After The Firm");
            return (firm, new[] { firm, exchange });
        }

        [TestCase("The Firm: A Novel (The Firm Series Book 1)", true)]
        [TestCase("The Firm: A Novel (The Firm Series Book 1)", false)]
        [TestCase("The Firm", true)]
        [TestCase("The Firm", false)]
        public void should_match_the_firm_releases_for_either_monitored_edition_title(string monitoredTitle, bool withSeries)
        {
            var (firm, catalog) = FirmCatalog(monitoredTitle, withSeries);

            Assert.That(Match("John Grisham - The Firm (epub)", firm, catalog)?.IsMatch, Is.True);
            Assert.That(Match("John.Grisham.-.The.Firm.1991.RETAIL.EPUB.eBook", firm, catalog)?.IsMatch, Is.True);
            Assert.That(Match("John Grisham The Firm 2010 Retail EPUB eBook-BitBook", firm, catalog)?.IsMatch, Is.True);
        }

        [TestCase("John Grisham - The Exchange: After the Firm (epub)", TitleMatchProblemCode.SiblingTitleContradiction)]
        [TestCase("John Grisham - The Exchange After The Firm (2023) [m4b]", TitleMatchProblemCode.SiblingTitleContradiction)]
        [TestCase("John Grisham - The Firm Series (2)", TitleMatchProblemCode.SeriesNameOnly)]
        [TestCase("John Grisham - The Firm Series 2 - The Exchange (epub)", TitleMatchProblemCode.SeriesNameOnly)]
        public void should_not_match_the_sequel_or_another_series_entry_for_the_firm(string release, TitleMatchProblemCode expected)
        {
            foreach (var monitoredTitle in new[] { "The Firm", "The Firm: A Novel (The Firm Series Book 1)" })
            {
                foreach (var withSeries in new[] { true, false })
                {
                    var (firm, catalog) = FirmCatalog(monitoredTitle, withSeries);
                    var result = Match(release, firm, catalog);

                    Assert.That(result?.IsMatch ?? false, Is.False, $"monitored '{monitoredTitle}', series {withSeries}");
                    Assert.That(result.Problems.Select(p => p.Code), Does.Contain(expected));
                }
            }
        }

        [Test]
        public void should_still_match_a_series_prefix_before_the_title()
        {
            var skyshade = CreateBook("Alex Aster", "Skyshade");
            var lightlark = CreateBook("Alex Aster", "Lightlark");

            Assert.That(Match("Alex Aster - Lightlark Series 03 - Skyshade (m4b)", skyshade, skyshade, lightlark)?.IsMatch, Is.True);
        }

        [Test]
        public void should_list_loose_variants_with_the_text_they_drop()
        {
            var variants = ReleaseTitleMatchScorer.GetTalearrLooseVariants("The Pelican Brief: A Novel", null).ToList();

            Assert.That(variants.Select(v => v.Variant), Does.Contain("The Pelican Brief"));
            Assert.That(variants.Select(v => v.Variant), Does.Contain("Pelican Brief").IgnoreCase);
            Assert.That(variants.Select(v => v.Variant), Does.Contain("pelican brief, the"));
            Assert.That(variants.First(v => v.Variant == "The Pelican Brief").Dropped, Is.EqualTo("A Novel"));
        }
    }
}
