using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Metadata;
using NzbDrone.Core.Talearr.Comics.Search;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicsFallbackAndCategoryFixture
    {
        [Test]
        public void should_read_a_recorded_mangadex_search()
        {
            var json = File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Talearr", "Comics", "Fixtures", "mangadex-search-witch-hat-atelier.json"));
            var results = MangaDexClient.Parse(json);

            var atelier = results.First();
            Assert.That(atelier.Titles, Does.Contain("Tongari Boushi no Atelier"));
            Assert.That(atelier.ContentRating, Is.EqualTo("safe"));
            Assert.That(atelier.Demographic, Is.EqualTo("seinen"));
            Assert.That(atelier.OriginalLanguage, Is.EqualTo("ja"));
            Assert.That(atelier.Tags, Does.Contain("Fantasy"));
            Assert.That(MangaDexClient.ToAgeRating(atelier), Is.EqualTo(ComicAgeRatings.Teen));

            var kitchen = results.Single(m => m.Titles.Contains("Witch Hat Atelier Kitchen"));
            Assert.That(kitchen.EnglishTitle, Is.EqualTo("Witch Hat Atelier Kitchen"));
            Assert.That(kitchen.Titles.Any(t => t.Any(c => c >= 0x2E80)), Is.False, "only Latin titles are kept for matching");
        }

        [TestCase("safe", new string[0], ComicAgeRatings.Teen)]
        [TestCase("safe", new[] { "Gore" }, ComicAgeRatings.Mature17)]
        [TestCase("suggestive", new string[0], ComicAgeRatings.Mature17)]
        [TestCase("erotica", new string[0], ComicAgeRatings.AdultsOnly)]
        [TestCase("pornographic", new string[0], ComicAgeRatings.AdultsOnly)]
        [TestCase(null, new string[0], ComicAgeRatings.Unknown)]
        public void should_map_mangadex_ratings_conservatively(string contentRating, string[] tags, string expected)
        {
            var manga = new MangaDexManga { ContentRating = contentRating, Tags = tags.ToList() };

            Assert.That(MangaDexClient.ToAgeRating(manga), Is.EqualTo(expected));
        }

        [Test]
        public void should_never_rate_from_mangadex_as_everyone()
        {
            var ratings = new[] { "safe", "suggestive", "erotica", "pornographic" }
                .Select(r => MangaDexClient.ToAgeRating(new MangaDexManga { ContentRating = r, Demographic = "shoujo" }));

            Assert.That(ratings, Has.None.EqualTo(ComicAgeRatings.Everyone).And.None.EqualTo(ComicAgeRatings.EarlyChildhood));
        }

        [Test]
        public void should_send_comics_to_the_comics_category_and_fall_back_to_ebooks()
        {
            var comic = new RemoteBook { IsComic = true, Books = new List<Book> { new Book { MediaType = BookMediaType.Ebook } } };
            var audiobook = new RemoteBook { Books = new List<Book> { new Book { MediaType = BookMediaType.Audiobook } } };

            Assert.That(comic.PickCategory("talearr-ebooks", "talearr-audiobooks", "comics"), Is.EqualTo("comics"));
            Assert.That(comic.PickCategory("talearr-ebooks", "talearr-audiobooks", ""), Is.EqualTo("talearr-ebooks"));
            Assert.That(audiobook.PickCategory("talearr-ebooks", "talearr-audiobooks", "comics"), Is.EqualTo("talearr-audiobooks"));
        }

        [Test]
        public void should_default_the_comics_category_for_existing_clients()
        {
            Assert.That(new QBittorrentSettings().ComicsCategory, Is.EqualTo("comics"));
        }

        [TestCase("comics", "comics", "talearr-ebooks", true)]
        [TestCase("Comics", "comics", "talearr-ebooks", true)]
        [TestCase("talearr-ebooks", "comics", "talearr-ebooks", false)]
        [TestCase("comics", "", "talearr-ebooks", false)]
        [TestCase("talearr-ebooks", "talearr-ebooks", "talearr-ebooks", false)]
        public void should_hand_the_comics_category_to_comics(string itemCategory, string comicsCategory, string ebookCategory, bool claimed)
        {
            var settings = new QBittorrentSettings { ComicsCategory = comicsCategory, EbookCategory = ebookCategory };
            var item = new DownloadClientItem { DownloadId = "x", Category = itemCategory };

            Assert.That(ComicDownloadTracker.InComicsCategory(settings, item), Is.EqualTo(claimed));
        }
    }
}
