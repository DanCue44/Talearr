using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.Metadata.Source;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrSourceGapFillFixture
    {
        private static V5Book Book(string title, string subtitle = null)
        {
            return new V5Book { Id = $"hc:{title.GetHashCode()}", Title = title, Subtitle = subtitle, Editions = new List<V5Edition> { new V5Edition() } };
        }

        [Test]
        public void normalize_drops_case_punctuation_accents_and_leading_articles()
        {
            Assert.That(TalearrSourceGapFill.Normalize("The Café: A Story!"), Is.EqualTo("cafe a story"));
            Assert.That(TalearrSourceGapFill.Normalize("Belle Gunness : The True Story"), Is.EqualTo("belle gunness the true story"));
            Assert.That(TalearrSourceGapFill.Normalize("Don't Look"), Is.EqualTo("dont look"));
        }

        [Test]
        public void undated_books_take_the_open_library_year_by_title()
        {
            var full = Book("Belle Gunness: The True Story of the Slaying Mother");
            var main = Book("Jerry Brudos: The True Story of The Shoe Fetish Slayer");
            var unknown = Book("Not On Open Library");

            var works = new List<OpenLibraryWorkDoc>
            {
                new OpenLibraryWorkDoc { Title = "Belle Gunness : The True Story of the Slaying Mother", FirstPublishYear = 2016 },
                new OpenLibraryWorkDoc { Title = "Jerry Brudos", FirstPublishYear = 2015 }
            };

            var filled = TalearrSourceGapFill.FillFromWorks(new[] { full, main, unknown }, works);

            Assert.That(filled, Is.EqualTo(2));
            Assert.That(full.PublicationYear, Is.EqualTo(2016));
            Assert.That(main.PublicationYear, Is.EqualTo(2015), "the part before the subtitle matches");
            Assert.That(unknown.PublicationYear, Is.Null);
        }

        [Test]
        public void ambiguous_titles_are_left_alone()
        {
            var book = Book("True Crime Stories");
            var works = new List<OpenLibraryWorkDoc>
            {
                new OpenLibraryWorkDoc { Title = "True Crime Stories", FirstPublishYear = 2016 },
                new OpenLibraryWorkDoc { Title = "True crime stories", FirstPublishYear = 2019 }
            };

            Assert.That(TalearrSourceGapFill.FillFromWorks(new[] { book }, works), Is.EqualTo(0));
            Assert.That(book.PublicationYear, Is.Null);
        }

        [Test]
        public void books_with_any_date_are_not_looked_up()
        {
            var dated = Book("Dated");
            dated.Editions.Single().PublicationDate = new System.DateTime(2015, 1, 1);
            var response = new V5AuthorResponse { Author = new V5AuthorData { Name = "Someone" }, Books = new List<V5Book> { dated } };

            // No Open Library client: a lookup would throw.
            var gapFill = new TalearrSourceGapFill(null, NLog.LogManager.GetCurrentClassLogger());

            Assert.That(gapFill.FillMissingDates(response), Is.EqualTo(0));
        }
    }
}
