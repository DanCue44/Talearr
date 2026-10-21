using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using CoverImage = NzbDrone.Core.MediaCover.MediaCover;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.Books
{
    [TestFixture]
    public class EditionSelectorStubEditionFixture
    {
        private static EditionSelector CreateSut()
        {
            return new EditionSelector(LogManager.GetCurrentClassLogger());
        }

        private static List<Edition> CaughtUpEditions(bool stubManual = false)
        {
            var cover = new List<CoverImage> { new CoverImage { CoverType = MediaCoverTypes.Cover, Url = "/cover.jpg" } };

            return new List<Edition>
            {
                new Edition { Id = 43497, BookId = 7485, Title = "Caught Up", ReadingFormatId = 3, IsEbook = true, Isbn13 = "9788702461701", Monitored = true, ManualAdd = stubManual },
                new Edition { Id = 43509, BookId = 7485, Title = "Caught Up", ReadingFormatId = 3, IsEbook = true, Language = "eng", ForeignEditionId = "hc:edition:32092258-ebook", Images = cover, Ratings = new Ratings { Votes = 163 } },
                new Edition { Id = 43510, BookId = 7485, Title = "Caught Up", ReadingFormatId = 3, IsEbook = true, Language = "eng", ForeignEditionId = "hc:edition:32094883-ebook", Images = cover, Ratings = new Ratings { Votes = 7 } }
            };
        }

        [Test]
        public void should_replace_monitored_placeholder_with_real_edition()
        {
            var sut = CreateSut();
            var editions = CaughtUpEditions();

            Assert.That(sut.ReleaseStubMonitoredEdition(editions, new Dictionary<int, int>(), BookMediaType.Ebook), Is.True);
            sut.EnsureSingleMonitoredEdition(editions, new Dictionary<int, int>(), BookMediaType.Ebook);

            var monitored = editions.Single(e => e.Monitored);
            Assert.That(monitored.ForeignEditionId, Is.Not.Null.And.Not.Empty);
            Assert.That(monitored.Images, Is.Not.Empty);
        }

        [Test]
        public void should_keep_placeholder_that_owns_files()
        {
            var sut = CreateSut();
            var editions = CaughtUpEditions();

            Assert.That(sut.ReleaseStubMonitoredEdition(editions, new Dictionary<int, int> { [43497] = 1 }, BookMediaType.Ebook), Is.False);
            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(43497));
        }

        [Test]
        public void should_keep_placeholder_picked_by_hand()
        {
            var sut = CreateSut();
            var editions = CaughtUpEditions(stubManual: true);

            Assert.That(sut.ReleaseStubMonitoredEdition(editions, new Dictionary<int, int>(), BookMediaType.Ebook), Is.False);
        }

        [Test]
        public void should_keep_placeholder_when_no_real_edition_of_the_format_exists()
        {
            var sut = CreateSut();
            var editions = CaughtUpEditions().Take(1).ToList();
            editions.Add(new Edition { Id = 9, ReadingFormatId = 2, ForeignEditionId = "az:B0-audiobook", Images = new List<CoverImage> { new CoverImage() } });

            Assert.That(sut.ReleaseStubMonitoredEdition(editions, new Dictionary<int, int>(), BookMediaType.Ebook), Is.False);
        }
    }
}
