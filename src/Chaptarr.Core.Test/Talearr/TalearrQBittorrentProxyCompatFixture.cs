using System;
using NUnit.Framework;
using NzbDrone.Core.Download.Clients.QBittorrent;

namespace Chaptarr.Core.Test.Talearr
{
    // Talearr: qBittorrent-compatible debrid proxies (rdt-client, Decypharr) through the stock
    // qBittorrent client (docs/TALEARR_DOWNLOAD_CLIENTS.md).
    [TestFixture]
    public class TalearrQBittorrentProxyCompatFixture
    {
        [TestCase("2.11.4", "2.11.4")]
        [TestCase("v2.8", "2.8")]
        [TestCase("2.8.3-rdt", "2.8.3")]
        [TestCase("2", "2.0")]
        [TestCase(" 2.9.3\n", "2.9.3")]
        [TestCase("", "2.0")]
        [TestCase("unknown", "2.0")]
        public void api_versions_are_read_leniently(string answer, string expected)
        {
            Assert.That(QBittorrentProxyV2.ParseApiVersion(answer), Is.EqualTo(Version.Parse(expected)));
        }

        [Test]
        public void content_path_is_used_when_it_is_a_subfolder()
        {
            var path = QBittorrent.ResolveContentPath(new QBittorrentTorrent { SavePath = "/downloads/books/", ContentPath = "/downloads/books/Dune", Name = "Dune" });

            Assert.That(path.Value.FullPath, Is.EqualTo("/downloads/books/Dune"));
        }

        [Test]
        public void an_empty_content_path_falls_back_to_save_path_and_name()
        {
            var path = QBittorrent.ResolveContentPath(new QBittorrentTorrent { SavePath = "/mnt/debrid/books/", ContentPath = "", Name = "Dune [M4B]" });

            Assert.That(path.Value.FullPath, Is.EqualTo("/mnt/debrid/books/Dune [M4B]"));
        }

        [TestCase("/downloads/books/", "/downloads/books/")]
        [TestCase("/downloads/books/", "/downloads/books")]
        [TestCase("/downloads/books", "/downloads/books/")]
        public void the_base_folder_is_never_the_download(string savePath, string contentPath)
        {
            Assert.That(QBittorrent.ResolveContentPath(new QBittorrentTorrent { SavePath = savePath, ContentPath = contentPath, Name = "Dune" }), Is.Null);
        }

        [Test]
        public void nothing_to_go_on_gives_no_path()
        {
            Assert.That(QBittorrent.ResolveContentPath(new QBittorrentTorrent { SavePath = null, ContentPath = null, Name = "Dune" }), Is.Null);
        }
    }
}
