using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Profiles.Qualities;
using Q = NzbDrone.Core.Qualities.Quality;

namespace Chaptarr.Core.Test.Profiles.Quality
{
    [TestFixture]
    public class QualityProfilePreferEpubFixture
    {
        private static List<QualityProfileQualityItem> ByWeight()
        {
            // The order GetDefaultProfile builds from the quality weights: worst first.
            return Q.DefaultQualityDefinitions
                .OrderBy(d => d.GroupWeight)
                .Select(d => new QualityProfileQualityItem { Quality = d.Quality, Allowed = true })
                .ToList();
        }

        [Test]
        public void epub_becomes_the_best_ebook_format_in_new_profiles()
        {
            var items = ByWeight();

            QualityProfileService.PreferEpub(items);

            var ebooks = items.Select(i => i.Quality).Where(q => q == Q.PDF || q == Q.MOBI || q == Q.EPUB || q == Q.AZW3).ToList();
            Assert.That(ebooks, Is.EqualTo(new[] { Q.PDF, Q.MOBI, Q.AZW3, Q.EPUB }));

            var profile = new QualityProfile { Items = items };
            Assert.That(profile.GetIndex(Q.EPUB).CompareTo(profile.GetIndex(Q.AZW3)), Is.GreaterThan(0));
        }

        private static bool IsAudio(Q quality)
        {
            return quality == Q.UnknownAudio || quality == Q.FLAC || quality == Q.MP3 || quality == Q.M4B;
        }

        [Test]
        public void audio_qualities_keep_their_order()
        {
            var before = ByWeight().Select(i => i.Quality).Where(IsAudio).ToList();
            var items = ByWeight();

            QualityProfileService.PreferEpub(items);

            Assert.That(items.Select(i => i.Quality).Where(IsAudio), Is.EqualTo(before));
        }

        [Test]
        public void a_profile_that_already_ranks_epub_first_is_unchanged()
        {
            var items = ByWeight();
            QualityProfileService.PreferEpub(items);
            var once = items.Select(i => i.Quality).ToList();

            QualityProfileService.PreferEpub(items);

            Assert.That(items.Select(i => i.Quality), Is.EqualTo(once));
        }
    }
}
