using System.Collections.Generic;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;
using CoverImage = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.ApiV1
{
    // Talearr: Caught Up (ebook 7485) had a monitored placeholder edition without a cover while two
    // real Hardcover editions had one.
    [TestFixture]
    public class BookResourceCoverFallbackFixture
    {
        private static List<CoverImage> Cover(string url) =>
            new List<CoverImage> { new CoverImage { CoverType = MediaCoverTypes.Cover, Url = url } };

        private static Book CaughtUp(List<CoverImage> placeholderImages = null, List<CoverImage> bookImages = null)
        {
            return new Book
            {
                Id = 7485,
                Title = "Caught Up",
                MediaType = BookMediaType.Ebook,
                Images = bookImages ?? new List<CoverImage>(),
                Editions = new List<Edition>
                {
                    new Edition { Id = 43497, Title = "Caught Up", ReadingFormatId = 3, Monitored = true, Images = placeholderImages },
                    new Edition { Id = 43509, Title = "Caught Up", ReadingFormatId = 3, ForeignEditionId = "hc:edition:32092258-ebook", Images = Cover("/popular.jpg"), Ratings = new Ratings { Votes = 163 } },
                    new Edition { Id = 43510, Title = "Caught Up", ReadingFormatId = 3, ForeignEditionId = "hc:edition:32094883-ebook", Images = Cover("/other.jpg"), Ratings = new Ratings { Votes = 7 } },
                    new Edition { Id = 1, Title = "Caught Up", ReadingFormatId = 2, Images = Cover("/audio.jpg"), Ratings = new Ratings { Votes = 9000 } }
                }
            };
        }

        [Test]
        public void should_use_the_monitored_edition_cover_when_it_has_one()
        {
            var images = BookResourceMapper.SelectCoverImages(CaughtUp(Cover("/mine.jpg")), CaughtUp(Cover("/mine.jpg")).Editions[0]);

            Assert.That(images[0].Url, Is.EqualTo("/mine.jpg"));
        }

        [Test]
        public void should_fall_back_to_best_rated_edition_of_the_same_format()
        {
            var book = CaughtUp();

            Assert.That(BookResourceMapper.SelectCoverImages(book, book.Editions[0])[0].Url, Is.EqualTo("/popular.jpg"));
        }

        [Test]
        public void should_fall_back_to_book_images_when_no_edition_has_a_cover()
        {
            var book = new Book
            {
                MediaType = BookMediaType.Ebook,
                Images = Cover("/book.jpg"),
                Editions = new List<Edition> { new Edition { Id = 5, Monitored = true } }
            };

            Assert.That(BookResourceMapper.SelectCoverImages(book, book.Editions[0])[0].Url, Is.EqualTo("/book.jpg"));
        }
    }
}
