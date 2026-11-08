using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Talearr.Metadata
{
    // The provider lists Herald of Justice in First Peacemaker without a number, while the book's
    // own record says First Peacemaker #6.
    [TestFixture]
    public class SeriesPositionFromBookFixture
    {
        private static Book Herald() => new Book { Title = "Herald of Justice", SeriesName = "First Peacemaker", SeriesPosition = "6" };

        [Test]
        public void an_unnumbered_slot_takes_the_books_own_number()
        {
            Assert.That(RefreshSeriesService.PositionOrBookOwn(null, Herald(), "First Peacemaker"), Is.EqualTo("6"));
            Assert.That(RefreshSeriesService.PositionOrBookOwn("", Herald(), "The First Peacemaker"), Is.EqualTo("6"));
        }

        [Test]
        public void the_providers_number_wins_when_it_has_one()
        {
            Assert.That(RefreshSeriesService.PositionOrBookOwn("7", Herald(), "First Peacemaker"), Is.EqualTo("7"));
        }

        [Test]
        public void a_number_for_another_series_is_not_used()
        {
            Assert.That(RefreshSeriesService.PositionOrBookOwn(null, Herald(), "Peacemaker Wars"), Is.Null);
            Assert.That(RefreshSeriesService.PositionOrBookOwn(null, new Book { SeriesName = "First Peacemaker" }, "First Peacemaker"), Is.Null);
        }
    }
}
