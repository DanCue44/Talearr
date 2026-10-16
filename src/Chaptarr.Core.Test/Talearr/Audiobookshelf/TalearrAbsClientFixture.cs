using System.Linq;
using System.Net;
using System.Net.Http;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.Talearr.Audiobookshelf;
using FakeHttp = Chaptarr.Core.Test.Talearr.Recordings.FakeAudibleHttp;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    [TestFixture]
    public class TalearrAbsClientFixture
    {
        private static AudioBookShelfSettings Settings()
        {
            return new AudioBookShelfSettings { Host = "audiobookshelf", Port = 13378, ApiKey = "abs-key" };
        }

        [Test]
        public void should_read_items_with_get_requests_and_the_api_key()
        {
            var (http, fake) = FakeHttp.Create();
            fake.On($"/api/libraries/{AbsFixtures.AudiobookLibraryId}/items", AbsFixtures.AudiobookItems);

            var items = new TalearrAbsClient(http, LogManager.GetCurrentClassLogger()).GetLibraryItems(Settings(), AbsFixtures.AudiobookLibraryId);

            Assert.That(items.Select(i => i.Id), Is.EqualTo(new[] { "li-steve-jobs", "li-animal-farm-cosham", "li-firm-abridged", "li-gone" }));

            var request = fake.Requests.Single();
            Assert.Multiple(() =>
            {
                Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
                Assert.That(request.Url.FullUri, Does.StartWith("http://audiobookshelf:13378/api/libraries/"));
                Assert.That(request.Url.FullUri, Does.Contain("minified=1").And.Contain("limit=200").And.Contain("page=0"));
                Assert.That(request.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer abs-key"));
            });
        }

        [Test]
        public void should_follow_pages_until_the_total_is_reached()
        {
            var (http, fake) = FakeHttp.Create();
            fake.On("page=0", AbsFixtures.ItemsPage(0, 200, 250, AbsFixtures.EbookLibraryId));
            fake.On("page=1", AbsFixtures.ItemsPage(1, 50, 250, AbsFixtures.EbookLibraryId));

            var items = new TalearrAbsClient(http, LogManager.GetCurrentClassLogger()).GetLibraryItems(Settings(), AbsFixtures.EbookLibraryId);

            Assert.That(items.Count, Is.EqualTo(250));
            Assert.That(fake.Requests.Count, Is.EqualTo(2));
            Assert.That(fake.Requests.All(r => r.Method == HttpMethod.Get), Is.True);
        }

        [Test]
        public void should_throw_on_an_http_error_so_the_sync_keeps_the_previous_copy()
        {
            var (http, fake) = FakeHttp.Create();
            fake.On("/items", "Unauthorized", HttpStatusCode.Unauthorized);

            Assert.Throws<HttpException>(() => new TalearrAbsClient(http, LogManager.GetCurrentClassLogger()).GetLibraryItems(Settings(), "lib"));
        }

        [Test]
        public void test_should_pass_when_the_mapped_libraries_can_be_listed()
        {
            var (http, fake) = FakeHttp.Create();
            fake.On("/items", AbsFixtures.ItemsPage(0, 1, 1, AbsFixtures.EbookLibraryId));
            fake.On("/api/libraries", AbsFixtures.Libraries);

            var settings = Settings();
            settings.SetLibraryMappings(new System.Collections.Generic.List<AudioBookShelfLibraryMapping>
            {
                new AudioBookShelfLibraryMapping { RootFolderId = 3, MediaType = "ebook", LibraryId = AbsFixtures.EbookLibraryId, LibraryFolderId = AbsFixtures.EbookFolderId, LibraryFolderPath = "/books" }
            });

            Assert.That(TalearrAbsClient.TestLibraryRead(http, settings), Is.Null);
            Assert.That(fake.Requests.Count(r => r.Url.FullUri.Contains("/items")), Is.EqualTo(1), "only the mapped library");
            Assert.That(fake.Requests.All(r => r.Method == HttpMethod.Get), Is.True);
        }

        [Test]
        public void test_should_explain_a_library_the_key_cannot_read()
        {
            var (http, fake) = FakeHttp.Create();
            fake.On("/items", "Forbidden", HttpStatusCode.Forbidden);
            fake.On("/api/libraries", AbsFixtures.Libraries);

            var failure = TalearrAbsClient.TestLibraryRead(http, Settings());

            Assert.That(failure, Is.Not.Null);
            Assert.That(failure.ErrorMessage, Does.Contain("Audiobooks").And.Contain("403"));
        }

        [Test]
        public void test_should_do_nothing_when_sync_is_off()
        {
            var (http, fake) = FakeHttp.Create();
            var settings = Settings();
            settings.TalearrSyncLibrary = false;

            Assert.That(TalearrAbsClient.TestLibraryRead(http, settings), Is.Null);
            Assert.That(fake.Requests, Is.Empty);
        }
    }
}
