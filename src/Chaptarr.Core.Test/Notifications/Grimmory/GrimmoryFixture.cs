using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Grimmory;

namespace Chaptarr.Core.Test.Notifications.Grimmory
{
    [TestFixture]
    public class GrimmoryFixture
    {
        private const string Libraries = @"[
            { ""id"": 1, ""name"": ""Ebooks"", ""watch"": false, ""paths"": [ { ""id"": 10, ""libraryId"": 1, ""path"": ""/library/ebooks"" } ] },
            { ""id"": 2, ""name"": ""Audiobooks"", ""watch"": true, ""paths"": [ { ""id"": 20, ""libraryId"": 2, ""path"": ""/library/audiobooks"" } ] }
        ]";

        private static GrimmorySettings Settings(params int[] libraryIds)
        {
            return new GrimmorySettings
            {
                Url = "http://grimmory:6060/",
                Username = "talearr",
                Password = "secret-" + System.Guid.NewGuid().ToString("N"),
                LibraryIds = libraryIds.ToList(),
                TalearrPath = "/books",
                GrimmoryPath = "/library"
            };
        }

        [Test]
        public void should_rescan_the_library_that_holds_the_imported_book()
        {
            var (subject, fake) = CreateSubject(Settings());

            subject.OnReleaseImport(new BookDownloadMessage
            {
                Book = new Book { Title = "Bad Blood" },
                BookFiles = new List<BookFile> { new BookFile { Path = "/books/ebooks/Jennifer Lynn Barnes/Bad Blood/Bad Blood.epub" } },
                OldFiles = new List<BookFile>()
            });

            var refreshes = fake.Requests.Where(r => r.Method == System.Net.Http.HttpMethod.Put).Select(r => r.Url.FullUri).ToList();
            Assert.That(refreshes, Is.EqualTo(new[] { "http://grimmory:6060/api/v1/libraries/1/refresh" }));
        }

        [Test]
        public void should_send_the_bearer_token_from_login()
        {
            var (subject, fake) = CreateSubject(Settings(2));

            subject.OnBookFileDelete(new BookFileDeleteMessage { BookFile = new BookFile { Path = "/books/audiobooks/A/B/01.mp3" } });

            var login = fake.Requests.First();
            Assert.That(login.Url.FullUri, Is.EqualTo("http://grimmory:6060/api/v1/auth/login"));
            Assert.That(Encoding.UTF8.GetString(login.ContentData), Does.Contain("\"username\": \"talearr\""));

            var refresh = fake.Requests.Last();
            Assert.That(refresh.Url.FullUri, Is.EqualTo("http://grimmory:6060/api/v1/libraries/2/refresh"));
            Assert.That(refresh.Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer access-1"));
        }

        [Test]
        public void should_renew_the_token_after_a_401_and_retry()
        {
            var expired = true;
            var (subject, fake) = CreateSubject(Settings(1), request =>
            {
                if (request.Url.FullUri.EndsWith("/refresh") && request.Url.FullUri.Contains("/libraries/") && expired)
                {
                    expired = false;
                    return (HttpStatusCode.Unauthorized, string.Empty);
                }

                return null;
            });

            subject.OnBookRetag(new BookRetagMessage { BookFile = new BookFile { Path = "/books/ebooks/a.epub" } });

            var uris = fake.Requests.Select(r => r.Url.FullUri).ToList();
            Assert.That(uris, Does.Contain("http://grimmory:6060/api/v1/auth/refresh"));
            Assert.That(fake.Requests.Last().Headers.GetSingleValue("Authorization"), Is.EqualTo("Bearer access-2"));
        }

        [Test]
        public void should_skip_formats_that_are_turned_off()
        {
            var settings = Settings();
            settings.Ebooks = false;
            var (subject, fake) = CreateSubject(settings);

            subject.OnBookFileDelete(new BookFileDeleteMessage { BookFile = new BookFile { Path = "/books/ebooks/a.epub" } });

            Assert.That(fake.Requests, Is.Empty);
        }

        [Test]
        public void should_not_rescan_when_no_library_holds_the_file()
        {
            var (subject, fake) = CreateSubject(Settings());

            subject.OnBookFileDelete(new BookFileDeleteMessage { BookFile = new BookFile { Path = "/elsewhere/a.epub" } });

            Assert.That(fake.Requests.Any(r => r.Method == System.Net.Http.HttpMethod.Put), Is.False);
        }

        [Test]
        public void test_should_report_bad_credentials()
        {
            var (subject, _) = CreateSubject(Settings(), request =>
                request.Url.FullUri.EndsWith("/auth/login") ? (HttpStatusCode.Unauthorized, string.Empty) : null);

            var result = subject.Test();

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors.Single().ErrorMessage, Does.Contain("username or password"));
        }

        [Test]
        public void test_should_report_a_chosen_library_that_is_gone()
        {
            var (subject, _) = CreateSubject(Settings(7));

            var result = subject.Test();

            Assert.That(result.Errors.Single().ErrorMessage, Does.Contain("no library with id 7"));
        }

        [Test]
        public void should_list_libraries_for_the_picker()
        {
            var (subject, _) = CreateSubject(Settings());

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(subject.RequestAction("getLibraries", new Dictionary<string, string>()));

            Assert.That(json, Does.Contain("\"Name\":\"Audiobooks\""));
            Assert.That(json, Does.Contain("\"Value\":1"));
        }

        [TestCase("/books/ebooks/a.epub", "/library/ebooks/a.epub")]
        [TestCase("/books", "/library")]
        [TestCase("/bookshelf/a.epub", "/bookshelf/a.epub")]
        [TestCase("C:\\books\\x.epub", "C:/books/x.epub")]
        public void should_map_talearr_paths_to_grimmory_paths(string path, string expected)
        {
            Assert.That(NzbDrone.Core.Notifications.Grimmory.Grimmory.MapPath(Settings(), path), Is.EqualTo(expected));
        }

        private static (NzbDrone.Core.Notifications.Grimmory.Grimmory Subject, FakeHttpClient Fake) CreateSubject(
            GrimmorySettings settings,
            System.Func<NzbDrone.Common.Http.HttpRequest, (HttpStatusCode, string)?> overrides = null)
        {
            var accessCount = 0;
            var (client, fake) = FakeHttpClient.Create(request =>
            {
                var custom = overrides?.Invoke(request);
                if (custom.HasValue)
                {
                    return custom.Value;
                }

                var uri = request.Url.FullUri;

                if (uri.EndsWith("/auth/login") || uri.EndsWith("/auth/refresh"))
                {
                    accessCount++;
                    return (HttpStatusCode.OK, $"{{\"accessToken\":\"access-{accessCount}\",\"refreshToken\":\"refresh-{accessCount}\",\"expires\":0}}");
                }

                if (uri.EndsWith("/api/v1/libraries"))
                {
                    return (HttpStatusCode.OK, Libraries);
                }

                return (HttpStatusCode.NoContent, string.Empty);
            });

            var subject = new NzbDrone.Core.Notifications.Grimmory.Grimmory(new GrimmoryProxy(client, LogManager.GetCurrentClassLogger()), LogManager.GetCurrentClassLogger())
            {
                Definition = new NotificationDefinition { Id = 1, Name = "Grimmory", Settings = settings }
            };

            return (subject, fake);
        }
    }
}
