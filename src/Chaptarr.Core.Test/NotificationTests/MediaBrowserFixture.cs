using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.MediaBrowser;
using NzbDrone.Core.ThingiProvider;

namespace Chaptarr.Core.Test.NotificationTests
{
    [TestFixture]
    public class MediaBrowserFixture
    {
        private class FakeProxy : IMediaBrowserProxy
        {
            public List<(List<string> Paths, string Type)> Calls { get; } = new();

            public string GetServerName(MediaBrowserSettings settings) => "Jellyfin";

            public void UpdateFolders(MediaBrowserSettings settings, IEnumerable<string> paths, string updateType)
            {
                Calls.Add((paths.ToList(), updateType));
            }
        }

        private static (MediaBrowser Subject, FakeProxy Proxy) Create(MediaBrowserSettings settings)
        {
            var proxy = new FakeProxy();
            var subject = new MediaBrowser(proxy, LogManager.GetCurrentClassLogger())
            {
                Definition = new NotificationDefinition { Settings = settings }
            };

            return (subject, proxy);
        }

        [Test]
        public void should_rescan_each_book_folder_once_on_import()
        {
            var (subject, proxy) = Create(new MediaBrowserSettings { Host = "jellyfin", ApiKey = "k", UpdateLibrary = true });

            subject.OnReleaseImport(new BookDownloadMessage
            {
                BookFiles = new List<BookFile>
                {
                    new BookFile { Path = "/audiobooks/Jennifer Lynn Barnes/Bad Blood/01.mp3" },
                    new BookFile { Path = "/audiobooks/Jennifer Lynn Barnes/Bad Blood/02.mp3" }
                }
            });

            Assert.That(proxy.Calls.Single().Paths, Is.EqualTo(new[] { "/audiobooks/Jennifer Lynn Barnes/Bad Blood" }));
            Assert.That(proxy.Calls.Single().Type, Is.EqualTo("Created"));
        }

        [Test]
        public void should_map_paths_to_what_the_server_sees()
        {
            var settings = new MediaBrowserSettings { MapFrom = "/audiobooks/", MapTo = "/media/Audiobooks" };

            Assert.That(MediaBrowser.MapPath("/audiobooks/Jennifer Lynn Barnes/Bad Blood", settings), Is.EqualTo("/media/Audiobooks/Jennifer Lynn Barnes/Bad Blood"));
            Assert.That(MediaBrowser.MapPath("/ebooks/Bad Blood", settings), Is.EqualTo("/ebooks/Bad Blood"));
        }

        [Test]
        public void should_do_nothing_when_update_library_is_off()
        {
            var (subject, proxy) = Create(new MediaBrowserSettings { Host = "jellyfin", ApiKey = "k", UpdateLibrary = false });

            subject.OnBookFileDelete(new BookFileDeleteMessage { BookFile = new BookFile { Path = "/audiobooks/a/b/c.mp3" } });

            Assert.That(proxy.Calls, Is.Empty);
        }

        [Test]
        public void test_should_pass_when_the_server_answers()
        {
            var (subject, _) = Create(new MediaBrowserSettings { Host = "jellyfin", ApiKey = "k" });

            Assert.That(subject.Test().IsValid, Is.True);
        }
    }
}
