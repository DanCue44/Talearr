using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Talearr.Explain;

namespace Chaptarr.Core.Test.Talearr.Explain
{
    [TestFixture]
    public class TalearrMetadataExplainerFixture
    {
        private TalearrMetadataExplainer _subject;

        [SetUp]
        public void SetUp()
        {
            _subject = new TalearrMetadataExplainer();
        }

        private static Book Ebook() => new Book { Id = 7485, Title = "Caught Up", MediaType = BookMediaType.Ebook };

        private static Edition Edition(int id, string foreignId, int format, string language = "English", bool monitored = false, int votes = 0)
        {
            return new Edition
            {
                Id = id,
                ForeignEditionId = foreignId,
                Title = "Caught Up",
                ReadingFormatId = format,
                Language = language,
                Monitored = monitored,
                Ratings = new Ratings { Votes = votes },
                Images = foreignId == null ? new List<NzbDrone.Core.MediaCover.MediaCover>() : new List<NzbDrone.Core.MediaCover.MediaCover> { new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "https://example/cover.jpg") }
            };
        }

        [Test]
        public void should_flag_an_empty_placeholder_edition()
        {
            var editions = new List<Edition>
            {
                Edition(1, null, 3, monitored: true),
                Edition(2, "hc:edition:32092258-ebook", 3)
            };

            var entry = _subject.Explain(Ebook(), "hc:1581190", editions, "English");

            Assert.That(entry.Code, Is.EqualTo("edition-placeholder"));
            Assert.That(entry.Details[0], Is.EqualTo("Matched to the Hardcover work hc:1581190."));
        }

        [Test]
        public void should_explain_no_selected_edition()
        {
            var entry = _subject.Explain(Ebook(), "hc:1", new List<Edition> { Edition(2, "hc:edition:2", 3) }, "English");

            Assert.That(entry.Outcome, Is.EqualTo(TalearrDecisionOutcomes.Skipped));
            Assert.That(entry.Code, Is.EqualTo("no-edition"));
        }

        [Test]
        public void should_explain_a_native_edition_in_the_preferred_language_with_most_ratings()
        {
            var editions = new List<Edition>
            {
                Edition(1, "hc:edition:1", 3, monitored: true, votes: 900),
                Edition(2, "hc:edition:2", 3, votes: 10),
                Edition(3, "hc:edition:3", 1, votes: 5000),
                Edition(4, "hc:edition:4", 3, language: "German", votes: 20)
            };

            var entry = _subject.Explain(Ebook(), "hc:1", editions, "English");

            Assert.That(entry.Code, Is.EqualTo("edition-native-preferred-language"));
            Assert.That(entry.Details, Has.Some.Contains("has the most ratings (900)"));
            Assert.That(entry.Details, Has.Some.Contains("Not used: 2 other ebook editions and 1 edition in another format (1 of them in other languages)."));
        }

        [Test]
        public void should_explain_a_pinned_edition()
        {
            var pinned = Edition(1, "hc:edition:1", 3, monitored: true);
            pinned.ManualAdd = true;

            var entry = _subject.Explain(Ebook(), "hc:1", new List<Edition> { pinned }, "English");

            Assert.That(entry.Code, Is.EqualTo("edition-pinned"));
        }

        [Test]
        public void should_explain_a_fallback_format()
        {
            var entry = _subject.Explain(Ebook(), "hc:1", new List<Edition> { Edition(1, "hc:edition:1", 1, monitored: true) }, "English");

            Assert.That(entry.Code, Is.EqualTo("edition-fallback-format"));
        }
    }
}
