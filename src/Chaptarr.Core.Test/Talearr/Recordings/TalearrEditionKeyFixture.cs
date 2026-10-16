using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrEditionKeyFixture
    {
        [Test]
        public void base_edition_and_its_upstream_clone_share_a_key()
        {
            var baseEdition = FirmEditions.ScottBrickUs();

            // What AddWantedEdition stores on the cloned edition of a copy row.
            var clone = FirmEditions.ScottBrickUs();
            clone.Id = 90001;
            clone.BookId = 9000;
            clone.ForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(baseEdition);

            Assert.That(clone.ForeignEditionId, Is.Not.EqualTo(baseEdition.ForeignEditionId), "upstream really does rewrite it");
            Assert.That(TalearrEditionKey.For(clone), Is.EqualTo(TalearrEditionKey.For(baseEdition)));
            Assert.That(TalearrEditionKey.Same(clone, baseEdition), Is.True);
        }

        [Test]
        public void key_drops_the_media_type_suffix_when_no_provider_field_is_set()
        {
            Assert.That(TalearrEditionKey.For(new Edition { ForeignEditionId = "gr:134610787-audiobook" }), Is.EqualTo("gr:134610787"));
            Assert.That(TalearrEditionKey.For(new Edition()), Is.Null);
            Assert.That(TalearrEditionKey.Same(new Edition(), new Edition()), Is.False);
        }
    }
}
