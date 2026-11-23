using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Titles;

namespace Chaptarr.Core.Test.Talearr.Titles
{
    [TestFixture]
    public class TalearrSearchTitlesFixture
    {
        private Dictionary<int, string> _displayTitles;
        private Author _author;
        private Book _book1;
        private Book _book2;

        [SetUp]
        public void SetUp()
        {
            _displayTitles = new Dictionary<int, string>();
            TalearrSearchTitles.Register(book => _displayTitles.TryGetValue(book.Id, out var title) ? title : null);

            _author = new Author { Id = 7, Name = "Tao Wong" };
            _book1 = new Book { Id = 1, AuthorId = 7, Title = "Viridian Gate Online: Cataclysm", SeriesName = "The Viridian Gate Archives", SeriesPosition = "1", MediaType = BookMediaType.Audiobook };
            _book2 = new Book { Id = 2, AuthorId = 7, Title = "Viridian Gate Online: Crimson Alliance", SeriesName = "The Viridian Gate Archives", SeriesPosition = "2", MediaType = BookMediaType.Audiobook };
        }

        [TearDown]
        public void TearDown()
        {
            TalearrSearchTitles.Register(null);
        }

        [TestCase("Cataclysm", true)]
        [TestCase("The Lich Priest", true)]
        [TestCase("Dune", true)]
        [TestCase("It", false)]
        [TestCase("Fox", false)]
        [TestCase("  ", false)]
        [TestCase(null, false)]
        public void should_skip_a_single_short_word(string title, bool expected)
        {
            Assert.That(TalearrSearchTitles.IsUsable(title), Is.EqualTo(expected));
        }

        [Test]
        public void should_search_the_display_title_when_it_searches_differently()
        {
            _displayTitles[1] = "Cataclysm";

            Assert.That(TalearrSearchTitles.ForSearch(_book1, _book1.Title), Is.EqualTo("Cataclysm"));
            Assert.That(TalearrSearchTitles.ForSearch(_book2, _book2.Title), Is.Null);
        }

        [Test]
        public void should_not_search_a_display_title_that_searches_the_same()
        {
            // A book search sends only the main title, so this is the same query.
            _book1.Title = "Leviathan Wakes: The Expanse, Book 1";
            _displayTitles[1] = "Leviathan Wakes";

            Assert.That(TalearrSearchTitles.ForSearch(_book1, _book1.Title), Is.Null);
            Assert.That(TalearrSearchTitles.ForMatching(_book1), Is.EqualTo("Leviathan Wakes"));
        }

        [Test]
        public void should_have_no_second_title_without_a_resolver_or_when_it_fails()
        {
            _displayTitles[1] = "Cataclysm";
            TalearrSearchTitles.Register(null);
            Assert.That(TalearrSearchTitles.ForMatching(_book1), Is.Null);

            TalearrSearchTitles.Register(_ => throw new System.InvalidOperationException());
            Assert.That(TalearrSearchTitles.ForMatching(_book1), Is.Null);
            Assert.That(TalearrSearchTitles.ForSearch(_book1, _book1.Title), Is.Null);
        }

        [Test]
        public void should_build_an_author_only_display_title_search_for_interactive_search()
        {
            _displayTitles[1] = "Cataclysm";
            var original = new BookSearchCriteria
            {
                Author = _author,
                Books = new List<Book> { _book1 },
                BookTitle = _book1.Title,
                BookYear = 2018,
                InteractiveSearch = true
            };

            var spec = ReleaseSearchService.GetDisplayTitleSearch(original, _book1, true, new List<DownloadDecision>());

            Assert.That(spec, Is.Not.Null);
            Assert.That(spec.BookTitle, Is.EqualTo("Cataclysm"));
            Assert.That(spec.RequireAuthor, Is.True);
            Assert.That(spec.Books, Is.SameAs(original.Books));
            Assert.That(spec.BookYear, Is.EqualTo(2018));
            Assert.That(original.BookTitle, Is.EqualTo("Viridian Gate Online: Cataclysm"));
            Assert.That(original.RequireAuthor, Is.False);
        }

        [Test]
        public void should_search_the_display_title_automatically_only_when_the_metadata_title_found_nothing()
        {
            _displayTitles[1] = "Cataclysm";
            var original = new BookSearchCriteria { Author = _author, Books = new List<Book> { _book1 }, BookTitle = _book1.Title };

            var approved = new DownloadDecision(new RemoteBook { Release = new ReleaseInfo { Title = "x" } });
            var rejected = new DownloadDecision(new RemoteBook { Release = new ReleaseInfo { Title = "y" } }, new Rejection("wrong book"));

            Assert.That(ReleaseSearchService.GetDisplayTitleSearch(original, _book1, false, new List<DownloadDecision> { approved }), Is.Null);
            Assert.That(ReleaseSearchService.GetDisplayTitleSearch(original, _book1, false, new List<DownloadDecision> { rejected }), Is.Not.Null);
            Assert.That(ReleaseSearchService.GetDisplayTitleSearch(original, _book1, false, new List<DownloadDecision>()), Is.Not.Null);
        }

        [Test]
        public void should_match_a_release_named_after_the_display_title()
        {
            var books = new List<Book> { _book1, _book2 };
            const string release = "Tao Wong - Cataclysm (2018) [m4b]";

            var without = ReleaseTitleMatchScorer.FindBestMatch(release, _author.Name, books);
            Assert.That(without?.IsMatch == true && without.Book == _book1, Is.False);

            _displayTitles[1] = "Cataclysm";
            var with = ReleaseTitleMatchScorer.FindBestMatch(release, _author.Name, books);

            Assert.That(with, Is.Not.Null);
            Assert.That(with.IsMatch, Is.True);
            Assert.That(with.Book, Is.SameAs(_book1));
        }

        [Test]
        public void should_still_match_a_release_named_after_the_metadata_title()
        {
            _displayTitles[1] = "Cataclysm";
            var books = new List<Book> { _book1, _book2 };

            var match = ReleaseTitleMatchScorer.FindBestMatch("Tao Wong - Viridian Gate Online Cataclysm [m4b]", _author.Name, books);

            Assert.That(match?.IsMatch, Is.True);
            Assert.That(match.Book, Is.SameAs(_book1));
        }

        [Test]
        public void should_not_match_another_book_through_the_display_title()
        {
            _displayTitles[1] = "Cataclysm";
            var books = new List<Book> { _book1, _book2 };

            var match = ReleaseTitleMatchScorer.FindBestMatch("Tao Wong - Crimson Alliance [m4b]", _author.Name, books);

            Assert.That(match?.Book, Is.Not.SameAs(_book1));
        }
    }
}
