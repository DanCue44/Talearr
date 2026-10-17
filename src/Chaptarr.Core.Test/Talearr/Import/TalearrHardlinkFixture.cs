using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Import;

namespace Chaptarr.Core.Test.Talearr.Import
{
    [TestFixture]
    public class TalearrTransferScopeFixture
    {
        [TestCase(null, TalearrTransferChoice.Default)]
        [TestCase("", TalearrTransferChoice.Default)]
        [TestCase("hardlink", TalearrTransferChoice.Hardlink)]
        [TestCase("HardLink", TalearrTransferChoice.Hardlink)]
        [TestCase(" copy ", TalearrTransferChoice.Copy)]
        [TestCase("move", TalearrTransferChoice.Default)]
        public void should_parse_the_api_value(string value, TalearrTransferChoice expected)
        {
            Assert.That(TalearrTransferScope.Parse(value), Is.EqualTo(expected));
        }

        [Test]
        public void default_should_follow_the_hardlink_setting()
        {
            Assert.That(TalearrTransferScope.CopyMode(true), Is.EqualTo(TransferMode.HardLinkOrCopy));
            Assert.That(TalearrTransferScope.CopyMode(false), Is.EqualTo(TransferMode.Copy));
        }

        [Test]
        public void explicit_choice_should_override_the_setting_inside_the_scope_only()
        {
            using (TalearrTransferScope.Begin(TalearrTransferChoice.Copy))
            {
                Assert.That(TalearrTransferScope.CopyMode(true), Is.EqualTo(TransferMode.Copy));

                using (TalearrTransferScope.Begin(TalearrTransferChoice.Hardlink))
                {
                    Assert.That(TalearrTransferScope.CopyMode(false), Is.EqualTo(TransferMode.HardLinkOrCopy));
                }

                Assert.That(TalearrTransferScope.Current, Is.EqualTo(TalearrTransferChoice.Copy));
            }

            Assert.That(TalearrTransferScope.Current, Is.EqualTo(TalearrTransferChoice.Default));
            Assert.That(TalearrTransferScope.CopyMode(true), Is.EqualTo(TransferMode.HardLinkOrCopy));
        }

        [Test]
        public async Task scope_should_flow_into_awaited_work_and_not_leak_to_other_flows()
        {
            TalearrTransferChoice seenInside;
            using (TalearrTransferScope.Begin(TalearrTransferChoice.Copy))
            {
                seenInside = await Task.Run(() => TalearrTransferScope.Current);
            }

            var seenElsewhere = await Task.Run(() => TalearrTransferScope.Current);

            Assert.That(seenInside, Is.EqualTo(TalearrTransferChoice.Copy));
            Assert.That(seenElsewhere, Is.EqualTo(TalearrTransferChoice.Default));
        }
    }

    [TestFixture]
    public class TalearrHardlinkMonitorFixture
    {
        private RecordingEventAggregator _events;
        private TalearrHardlinkMonitor _monitor;

        [SetUp]
        public void SetUp()
        {
            var aggregator = DispatchProxy.Create<IEventAggregator, RecordingEventAggregator>();
            _events = (RecordingEventAggregator)(object)aggregator;
            _monitor = new TalearrHardlinkMonitor(aggregator, LogManager.GetCurrentClassLogger());
        }

        private void Fallback(int times, string source = "/mnt/user/downloads/a.epub", string target = "/mnt/user/books/a.epub")
        {
            for (var i = 0; i < times; i++)
            {
                _monitor.Record(source, target, TransferMode.HardLinkOrCopy, TransferMode.Copy);
            }
        }

        [Test]
        public void should_warn_after_repeated_fallbacks_and_name_the_unraid_shares()
        {
            Fallback(TalearrHardlinkMonitor.WarningThreshold);

            var status = _monitor.GetStatus();
            var message = TalearrHardlinkMonitor.GetMessage(status);

            Assert.That(status.ConsecutiveFallbacks, Is.EqualTo(3));
            Assert.That(message, Does.Contain("could not hardlink"));
            Assert.That(message, Does.Contain("'downloads' and 'books' are different shares"));
            Assert.That(_events.Published, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_not_warn_below_the_threshold()
        {
            Fallback(TalearrHardlinkMonitor.WarningThreshold - 1);

            Assert.That(TalearrHardlinkMonitor.GetMessage(_monitor.GetStatus()), Is.Null);
            Assert.That(_events.Published, Is.Empty);
        }

        [Test]
        public void a_successful_hardlink_should_clear_the_warning()
        {
            Fallback(4);
            _monitor.Record("/data/torrents/a.m4b", "/data/books/a.m4b", TransferMode.HardLinkOrCopy, TransferMode.HardLink);

            var status = _monitor.GetStatus();

            Assert.That(status.ConsecutiveFallbacks, Is.EqualTo(0));
            Assert.That(status.TotalFallbacks, Is.EqualTo(4));
            Assert.That(status.TotalHardlinks, Is.EqualTo(1));
            Assert.That(TalearrHardlinkMonitor.GetMessage(status), Is.Null);
            Assert.That(_events.Published, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_ignore_transfers_that_did_not_ask_for_a_hardlink()
        {
            _monitor.Record("/a", "/b", TransferMode.Copy, TransferMode.Copy);
            _monitor.Record("/a", "/b", TransferMode.Move, TransferMode.Move);

            Assert.That(_monitor.GetStatus().TotalFallbacks, Is.EqualTo(0));
        }

        [Test]
        public void should_not_blame_shares_when_paths_are_not_unraid_user_shares()
        {
            Fallback(3, "/downloads/a.epub", "/books/a.epub");

            var message = TalearrHardlinkMonitor.GetMessage(_monitor.GetStatus());

            Assert.That(message, Does.Contain("same container volume"));
            Assert.That(message, Does.Not.Contain("Unraid"));
        }

        [TestCase("/mnt/user/media/books/a.epub", "media")]
        [TestCase("/mnt/user0/data/x", "data")]
        [TestCase("/mnt/disk1/media/a", null)]
        [TestCase("/data/media", null)]
        [TestCase(null, null)]
        public void should_read_the_unraid_share(string path, string expected)
        {
            Assert.That(TalearrHardlinkMonitor.GetUnraidShare(path), Is.EqualTo(expected));
        }

        public class RecordingEventAggregator : DispatchProxy
        {
            public List<object> Published { get; } = new List<object>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IEventAggregator.PublishEvent))
                {
                    Published.Add(args[0]);
                    return null;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }

    [TestFixture]
    public class TalearrSeedingSafeImportFixture
    {
        [Test]
        public void should_name_the_post_import_category_as_the_reason()
        {
            var item = new NzbDrone.Core.Download.DownloadClientItem
            {
                DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo
                {
                    Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent,
                    Type = "qBittorrent",
                    HasPostImportCategory = true
                }
            };

            var mode = TalearrSeedingSafeImport.Apply(NzbDrone.Core.MediaFiles.BookImport.ImportMode.Move, item, true, out var reason);

            Assert.That(mode, Is.EqualTo(NzbDrone.Core.MediaFiles.BookImport.ImportMode.Copy));
            Assert.That(reason, Does.Contain("post-import category"));
        }

        [Test]
        public void should_do_nothing_when_turned_off()
        {
            var item = new NzbDrone.Core.Download.DownloadClientItem
            {
                DownloadClientInfo = new NzbDrone.Core.Download.DownloadClientItemClientInfo { Protocol = NzbDrone.Core.Indexers.DownloadProtocol.Torrent }
            };

            Assert.That(TalearrSeedingSafeImport.Apply(NzbDrone.Core.MediaFiles.BookImport.ImportMode.Move, item, false, out _),
                Is.EqualTo(NzbDrone.Core.MediaFiles.BookImport.ImportMode.Move));
        }
    }
}
