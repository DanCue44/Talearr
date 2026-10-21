using System.Collections.Generic;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Indexers
{
    // Chaptarr #321: MAM labels abridged readings in tags, not in titles. Shape from Carl Sagan's
    // Contact, where the abridged Jodie Foster reading is torrent 562102.
    [TestFixture]
    public class MyAnonaMouseJsonParserAbridgedFixture
    {
        private static TorrentInfo Parse(string tags)
        {
            var payload = new JObject
            {
                ["data"] = new JArray
                {
                    new JObject
                    {
                        ["id"] = 562102,
                        ["title"] = "Contact",
                        ["dl"] = "hash",
                        ["size"] = "180.2 MiB",
                        ["seeders"] = 5,
                        ["leechers"] = 0,
                        ["added"] = "2016-03-08 17:55:01",
                        ["tags"] = tags,
                        ["author_info"] = JsonConvert.SerializeObject(new Dictionary<string, string> { { "1", "Carl Sagan" } }),
                        ["category"] = 108,
                        ["main_cat"] = 13,
                        ["mediatype"] = 1,
                        ["catname"] = "Audiobooks - Science Fiction"
                    }
                }
            };

            var request = new HttpRequest("https://example.com/api");
            var response = new HttpResponse(request, new HttpHeader { ContentType = "application/json" }, payload.ToString(Formatting.None), HttpStatusCode.OK);
            var parser = new MyAnonaMouseJsonParser(new MyAnonaMouseSettings { BaseUrl = "https://www.myanonamouse.net" });

            return parser.ParseResponse(new IndexerResponse(new IndexerRequest(request), response)).OfType<TorrentInfo>().Single();
        }

        [Test]
        public void should_flag_abridged_from_tags()
        {
            var torrent = Parse("abridged, 64kpbs, library sourced, retail");

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Abridged), Is.True);
            Assert.That(AbridgedReleaseDetector.DetectRelease(torrent), Is.EqualTo(AbridgedMarker.Abridged));
        }

        [TestCase("unabridged, 64kbps")]
        [TestCase("64kbps, retail")]
        [TestCase(null)]
        public void should_not_flag_unabridged_or_untagged(string tags)
        {
            var torrent = Parse(tags);

            Assert.That(torrent.IndexerFlags.HasFlag(IndexerFlags.Abridged), Is.False);
        }

        [Test]
        public void title_marker_still_counts_without_the_flag()
        {
            var release = new ReleaseInfo { Title = "Contact (Abridged)" };

            Assert.That(AbridgedReleaseDetector.DetectRelease(release), Is.EqualTo(AbridgedMarker.Abridged));
        }
    }
}
