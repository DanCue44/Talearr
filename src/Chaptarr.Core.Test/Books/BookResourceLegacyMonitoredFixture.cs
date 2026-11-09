using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    // The book Edit modal sends the whole book back with only Monitored changed. Its
    // unchanged audiobookMonitored/ebookMonitored used to win, so unmonitoring never stuck.
    [TestFixture]
    public class BookResourceLegacyMonitoredFixture
    {
        private static Book StoredAudiobook(bool monitored)
        {
            return new Book { MediaType = BookMediaType.Audiobook, AudiobookMonitored = monitored };
        }

        [TestCase(true)]
        [TestCase(false)]
        public void a_changed_monitored_wins_over_an_unchanged_side_flag(bool stored)
        {
            var resource = new BookResource { Monitored = !stored, AudiobookMonitored = stored, EbookMonitored = false };

            var model = resource.ToModel(StoredAudiobook(stored), null);

            Assert.That(model.AudiobookMonitored, Is.EqualTo(!stored));
            Assert.That(model.EbookMonitored, Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void a_changed_side_flag_still_wins(bool stored)
        {
            var resource = new BookResource { Monitored = stored, AudiobookMonitored = !stored };

            var model = resource.ToModel(StoredAudiobook(stored), null);

            Assert.That(model.AudiobookMonitored, Is.EqualTo(!stored));
        }

        [Test]
        public void an_unchanged_book_stays_monitored()
        {
            var resource = new BookResource { Monitored = true, AudiobookMonitored = true };

            var model = resource.ToModel(StoredAudiobook(true), null);

            Assert.That(model.AudiobookMonitored, Is.True);
        }

        [Test]
        public void the_other_side_follows_only_its_own_flag()
        {
            var book = new Book { MediaType = BookMediaType.Ebook, EbookMonitored = true, AudiobookMonitored = true };
            var resource = new BookResource { Monitored = false, EbookMonitored = true, AudiobookMonitored = true };

            var model = resource.ToModel(book, null);

            Assert.That(model.EbookMonitored, Is.False);
            Assert.That(model.AudiobookMonitored, Is.True);
        }
    }
}
