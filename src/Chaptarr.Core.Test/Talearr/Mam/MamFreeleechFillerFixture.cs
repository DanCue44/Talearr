using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using Chaptarr.Core.Test.Talearr.Recordings;
using Chaptarr.Core.Test.Talearr.Seeding;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.MamFiller;
using NzbDrone.Core.Talearr.Seeding;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    public interface IFakeFillerClient : IDownloadClient, ITalearrSeedingSource, ITalearrMamFillerClient
    {
    }

    public class InMemoryFillerStore : ITalearrMamFillerStore
    {
        public Dictionary<string, TalearrFillerTorrent> Torrents { get; } = new Dictionary<string, TalearrFillerTorrent>();
        public TalearrMamFillerStatus Status { get; set; }

        public List<TalearrFillerTorrent> All() => Torrents.Values.ToList();

        public void Save(TalearrFillerTorrent torrent) => Torrents[torrent.TorrentId] = torrent;

        public TalearrMamFillerStatus GetStatus() => Status;

        public void SaveStatus(TalearrMamFillerStatus status) => Status = status;
    }

    // TaleArr: MAM freeleech seeding filler. MAM replies are recorded shapes served by the fake
    // HTTP client; nothing touches the network.
    [TestFixture]
    public class MamFreeleechFillerFixture
    {
        private const long MiB = 1024L * 1024;

        private MyAnonaMouseSettings _settings;
        private FakeMamHttpClient _http;
        private FakeMamIndexerFactory _indexers;
        private SeedingFake<IFakeFillerClient> _client;
        private SeedingFake<IMamUnsatisfiedSlotGuard> _slotGuard;
        private List<MamUnsatisfiedSlotReservation> _reservations;
        private InMemoryFillerStore _store;
        private TalearrSeedingReport _report;
        private List<(string Category, string Hash, TimeSpan? SeedTime)> _added;
        private List<RemoteBook> _reserved;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _now = DateTime.UtcNow;
            _settings = new MyAnonaMouseSettings
            {
                MamId = "OK",
                UserClass = "User",
                UnsatisfiedCount = 10,
                UnsatisfiedLimit = 20,
                UnsatisfiedSnapshotUtc = _now.AddMinutes(-10),
                UnsatisfiedStatusRefreshedUtc = _now.AddMinutes(-10),
                FillerEnabled = true,
                FillerDryRun = false
            };

            _http = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out var httpFactory);
            var indexer = MamTestIndexers.Create(_settings, httpFactory, new RecordingSessionMonitor(), id: 6);

            _indexers = FakeMamIndexerFactory.Create(out var indexerFactory);
            _indexers.Providers.Add(indexer);
            _indexers.Definitions.Add((IndexerDefinition)indexer.Definition);

            _added = new List<(string, string, TimeSpan?)>();
            _client = SeedingFake<IFakeFillerClient>.Create(out var downloadClient);
            _client
                .On("get_Protocol", _ => DownloadProtocol.Torrent)
                .On("get_Name", _ => "qBittorrent")
                .On("get_Definition", _ => new DownloadClientDefinition { Id = 2, Name = "qBittorrent (VPN)" })
                .On("GetLibraryCategory", args => (bool)args[0] ? "audiobooks" : "books")
                .On("GetCategoryTorrents", _ => new List<TalearrSeedingClientTorrent>())
                .On("HasTorrent", _ => false)
                .On("AddFillerTorrent", args =>
                {
                    _added.Add(((string)args[3], (string)args[2], (TimeSpan?)args[4]));
                    return null;
                });

            SeedingFake<IProvideDownloadClient>.Create(out var provider)
                .On("GetDownloadClients", _ => new List<IDownloadClient> { downloadClient });

            _reserved = new List<RemoteBook>();
            _slotGuard = SeedingFake<IMamUnsatisfiedSlotGuard>.Create(out _);
            _slotGuard.On("TryReserve", args =>
            {
                _reserved.Add((RemoteBook)args[0]);
                return MamUnsatisfiedSlotAvailability.Accept();
            });

            _reservations = new List<MamUnsatisfiedSlotReservation>();
            SeedingFake<IMamUnsatisfiedSlotReservationRepository>.Create(out var repository)
                .On("All", _ => _reservations);

            _report = new TalearrSeedingReport();
            SeedingFake<ITalearrSeedingService>.Create(out var seeding).On("GetReport", _ => _report);

            SeedingFake<ITorrentFileInfoReader>.Create(out var reader)
                .On("GetHashFromTorrentFile", args => "HASH" + Encoding.UTF8.GetString((byte[])args[0]));

            _store = new InMemoryFillerStore();
            Service = new TalearrMamFillerService(indexerFactory, provider, (IMamUnsatisfiedSlotGuard)(object)_slotGuard, repository, seeding, _store, reader, LogManager.GetCurrentClassLogger())
            {
                UtcNow = () => _now
            };
        }

        private TalearrMamFillerService Service { get; set; }

        private static JObject Torrent(int id, int leechers = 5, int seeders = 3, int mainCat = 13, bool free = true, bool vip = false, bool personal = false, string lang = "ENG", string size = "300.0 MiB")
        {
            return new JObject
            {
                ["id"] = id,
                ["title"] = "Book " + id,
                ["size"] = size,
                ["seeders"] = seeders,
                ["leechers"] = leechers,
                ["main_cat"] = mainCat,
                ["lang_code"] = lang,
                ["free"] = free ? 1 : 0,
                ["personal_freeleech"] = personal ? 1 : 0,
                ["fl_vip"] = 0,
                ["vip"] = vip ? 1 : 0
            };
        }

        private void ReplySearch(params JObject[] torrents)
        {
            _http.Reply(HttpStatusCode.OK, "application/json", new JObject { ["data"] = new JArray(torrents) }.ToString());
        }

        private void ReplyTorrent(string content)
        {
            _http.Reply(HttpStatusCode.OK, "application/x-bittorrent", content);
        }

        // Rules.
        [TestCase(20, 10, 0, 2, 5, 5)]
        [TestCase(20, 10, 0, 2, 20, 8)]
        [TestCase(20, 16, 2, 2, 5, 0)]
        [TestCase(20, 19, 0, 2, 5, 0)]
        [TestCase(20, 10, 3, 2, 5, 5)]
        [TestCase(20, 14, 1, 2, 5, 3)]
        public void slot_math_never_goes_above_limit_minus_margin(int limit, int unsatisfied, int pending, int margin, int maxPerRun, int expected)
        {
            Assert.That(TalearrMamFillerRules.FreeSlots(limit, unsatisfied, pending, margin, maxPerRun), Is.EqualTo(expected));
        }

        [Test]
        public void filters_exclude_vip_non_freeleech_wrong_category_language_size_and_peers()
        {
            var settings = new MyAnonaMouseSettings();
            string Reason(JObject json) => TalearrMamFillerRules.Reject(TalearrMamFillerRules.Parse(json), settings);

            Assert.Multiple(() =>
            {
                Assert.That(Reason(Torrent(1)), Is.Null);
                Assert.That(Reason(Torrent(2, vip: true)), Is.EqualTo("VIP only"));
                Assert.That(Reason(Torrent(3, free: false)), Is.EqualTo("not freeleech"));
                Assert.That(Reason(Torrent(4, free: false, personal: true)), Is.Null, "personal freeleech counts");
                Assert.That(Reason(Torrent(5, mainCat: 15)), Is.EqualTo("not an audiobook or ebook"));
                Assert.That(Reason(Torrent(6, lang: "GER")), Is.EqualTo("not English"));
                Assert.That(Reason(Torrent(7, size: "2.5 GiB")), Is.EqualTo("too large"));
                Assert.That(Reason(Torrent(8, leechers: 0)), Is.EqualTo("too few leechers"));
                Assert.That(Reason(Torrent(9, seeders: 31)), Is.EqualTo("too many seeders"));
                Assert.That(Reason(Torrent(10, mainCat: 14)), Is.Null);
            });
        }

        [Test]
        public void vip_freeleech_only_counts_for_a_vip_account()
        {
            var json = Torrent(1, free: false);
            json["fl_vip"] = 1;

            Assert.That(TalearrMamFillerRules.Reject(TalearrMamFillerRules.Parse(json), new MyAnonaMouseSettings()), Is.EqualTo("not freeleech"));
            Assert.That(TalearrMamFillerRules.Reject(TalearrMamFillerRules.Parse(json), new MyAnonaMouseSettings { IsVip = true }), Is.Null);
        }

        [Test]
        public void ranking_prefers_audiobooks_then_leechers_then_fewer_seeders()
        {
            var candidates = new[]
            {
                Torrent(1, leechers: 9, mainCat: 14),
                Torrent(2, leechers: 2, seeders: 1),
                Torrent(3, leechers: 6, seeders: 9),
                Torrent(4, leechers: 6, seeders: 2)
            }.Select(TalearrMamFillerRules.Parse).ToList();

            Assert.That(TalearrMamFillerRules.Rank(candidates, true).Select(c => c.TorrentId), Is.EqualTo(new[] { "4", "3", "2", "1" }));
            Assert.That(TalearrMamFillerRules.Rank(candidates, false).Select(c => c.TorrentId), Is.EqualTo(new[] { "1", "4", "3", "2" }));
        }

        // Service.
        [Test]
        public void search_asks_mam_for_freeleech_books_and_audiobooks_in_english()
        {
            _settings.FillerDryRun = true;
            ReplySearch(Torrent(1));

            Service.Run(true);

            var request = _http.Requests.Single();
            var body = JObject.Parse(Encoding.UTF8.GetString(request.ContentData));
            Assert.Multiple(() =>
            {
                Assert.That(request.Url.FullUri, Does.EndWith("/tor/js/loadSearchJSONbasic.php"));
                Assert.That((string)body["tor"]["searchType"], Is.EqualTo("fl"));
                Assert.That(body["tor"]["main_cat"].Select(t => (string)t), Is.EqualTo(new[] { "13", "14" }));
                Assert.That(body["tor"]["browse_lang"].Select(t => (string)t), Is.EqualTo(new[] { "1" }));
            });
        }

        [Test]
        public void dry_run_stores_the_preview_and_grabs_nothing()
        {
            _settings.FillerDryRun = true;
            ReplySearch(Torrent(1), Torrent(2, leechers: 9), Torrent(3, vip: true));

            var status = Service.Run(true);

            Assert.Multiple(() =>
            {
                Assert.That(_http.Requests, Has.Count.EqualTo(1), "search only, no download");
                Assert.That(_added, Is.Empty);
                Assert.That(_reserved, Is.Empty);
                Assert.That(status.PreviewIsDryRun, Is.True);
                Assert.That(status.Preview.Select(p => p.TorrentId), Is.EqualTo(new[] { "2", "1" }));
                Assert.That(status.Preview.Any(p => p.Grabbed), Is.False);
                Assert.That(status.LastResult, Does.StartWith("Dry run: would grab 2"));
                Assert.That(_store.Status, Is.SameAs(status));
            });
        }

        [Test]
        public void live_run_grabs_into_the_filler_category_with_the_seed_time_and_no_wedge()
        {
            _settings.FillerMaxGrabsPerRun = 2;
            ReplySearch(Torrent(1), Torrent(2, leechers: 9), Torrent(3, leechers: 1));
            ReplyTorrent("B");
            ReplyTorrent("A");

            var status = Service.Run(true);

            Assert.Multiple(() =>
            {
                Assert.That(_added.Select(a => a.Category), Is.EqualTo(new[] { "mam-seed", "mam-seed" }));
                Assert.That(_added.Select(a => a.Hash), Is.EqualTo(new[] { "HASHB", "HASHA" }));
                Assert.That(_added.All(a => a.SeedTime == TimeSpan.FromDays(30)), Is.True);

                var downloads = _http.Requests.Skip(1).Select(r => r.Url.FullUri).ToList();
                Assert.That(downloads, Is.EqualTo(new[]
                {
                    "https://www.myanonamouse.net/tor/download.php?tid=2",
                    "https://www.myanonamouse.net/tor/download.php?tid=1"
                }));
                Assert.That(downloads.Any(u => u.Contains("fl")), Is.False, "never the wedge parameter");

                Assert.That(_reserved.Select(r => r.Release.Guid), Is.EqualTo(new[] { "MAM-2", "MAM-1" }), "the unsatisfied-slot guard reserves each grab");
                Assert.That(_store.Torrents.Keys, Is.EquivalentTo(new[] { "1", "2" }));
                Assert.That(_store.Torrents["2"].ClientHash, Is.EqualTo("HASHB"));
                Assert.That(status.Preview.All(p => p.Grabbed), Is.True);
            });
        }

        [Test]
        public void grab_path_never_goes_through_the_library_grab_path()
        {
            // The filler has no IDownloadService, history or tracked download dependency at all,
            // and adds to its own category, which TaleArr's queue does not read.
            var parameters = typeof(TalearrMamFillerService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

            Assert.That(parameters, Has.None.EqualTo(typeof(IDownloadService)));
            Assert.That(parameters.Select(p => p.Namespace), Has.None.EqualTo("NzbDrone.Core.History"));
            Assert.That(parameters.Select(p => p.Namespace), Has.None.EqualTo("NzbDrone.Core.Download.TrackedDownloads"));
        }

        [Test]
        public void import_into_library_uses_the_client_category_for_the_media_type()
        {
            _settings.FillerImport = true;
            _settings.FillerMaxGrabsPerRun = 1;
            ReplySearch(Torrent(1));
            ReplyTorrent("A");

            Service.Run(true);

            Assert.That(_added.Single().Category, Is.EqualTo("audiobooks"));
        }

        [Test]
        public void already_grabbed_reserved_or_in_client_torrents_are_skipped()
        {
            _store.Save(new TalearrFillerTorrent { TorrentId = "1", State = TalearrFillerTorrent.StateDone, AddedAt = _now.AddDays(-40) });
            _reservations.Add(new MamUnsatisfiedSlotReservation { IndexerId = 6, TorrentId = "2", ReservedUtc = _now.AddMinutes(-5) });
            _report.Torrents.Add(new TalearrSeedingTorrent { DownloadId = "X", Title = "Book 3", State = "seeding" });
            _settings.FillerDryRun = true;
            ReplySearch(Torrent(1), Torrent(2), Torrent(3), Torrent(4));

            var status = Service.Run(true);

            Assert.That(status.Preview.Select(p => p.TorrentId), Is.EqualTo(new[] { "4" }));
            Assert.That(status.LastResult, Does.Contain("3 already grabbed or in the client"));
        }

        [Test]
        public void no_free_slots_means_no_search()
        {
            _settings.UnsatisfiedCount = 18;

            var status = Service.Run(true);

            Assert.That(_http.Requests, Is.Empty);
            Assert.That(status.FreeSlots, Is.Zero);
            Assert.That(status.LastResult, Does.StartWith("No free slots"));
        }

        [Test]
        public void pending_reservations_count_against_the_free_slots()
        {
            _settings.UnsatisfiedCount = 15;
            _reservations.Add(new MamUnsatisfiedSlotReservation { IndexerId = 6, TorrentId = "77", ReservedUtc = _now });
            _reservations.Add(new MamUnsatisfiedSlotReservation { IndexerId = 6, TorrentId = "78", ReservedUtc = _now });

            var status = Service.Run(true);

            Assert.That(status.FreeSlots, Is.EqualTo(1));
        }

        [Test]
        public void slot_guard_refusal_stops_the_run()
        {
            _slotGuard.On("TryReserve", _ => MamUnsatisfiedSlotAvailability.Reject("MAM safety pause: 15 of 20"));
            ReplySearch(Torrent(1));

            var status = Service.Run(true);

            Assert.That(_added, Is.Empty);
            Assert.That(_http.Requests, Has.Count.EqualTo(1), "no download after the refusal");
            Assert.That(status.LastResult, Does.Contain("stopped: MAM safety pause"));
        }

        [Test]
        public void seed_only_account_stops_before_any_mam_call()
        {
            _settings.UserClass = "Mouse";

            var status = Service.Run(true);

            Assert.That(_http.Requests, Is.Empty);
            Assert.That(status.LastResult, Is.EqualTo(MyAnonaMouseAccountStanding.SeedOnlyMessage));
        }

        [Test]
        public void stale_status_that_cannot_refresh_backs_off_and_stops()
        {
            _settings.UnsatisfiedStatusRefreshedUtc = _now.AddHours(-4);
            _http.Reply(HttpStatusCode.InternalServerError, "text/html", "oops");

            var status = Service.Run(true);

            Assert.That(_http.Requests.Single().Url.FullUri, Does.Contain("jsonLoad.php"));
            Assert.That(status.BackoffUntil, Is.EqualTo(_now.AddHours(12)));
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void never_refreshed_status_with_an_unusable_reply_backs_off()
        {
            _settings.UnsatisfiedStatusRefreshedUtc = null;
            _settings.UserClass = null;
            _http.Reply(HttpStatusCode.OK, "application/json", "{\"classname\":\"\"}");

            var status = Service.Run(true);

            Assert.That(_added, Is.Empty);
            Assert.That(status.BackoffUntil, Is.Not.Null, "an unusable account reply is a MAM error");
        }

        [Test]
        public void mam_torrent_at_risk_stops_the_run()
        {
            _report.Torrents.Add(new TalearrSeedingTorrent { DownloadId = "R", Title = "Risky", Indexer = "MyAnonamouse", AtRisk = true });

            var status = Service.Run(true);

            Assert.That(_http.Requests, Is.Empty);
            Assert.That(status.LastResult, Does.Contain("at risk"));
        }

        [Test]
        public void mam_search_error_backs_off_for_twelve_hours_and_later_runs_wait()
        {
            _http.Reply(HttpStatusCode.ServiceUnavailable, "text/html", "down");

            var first = Service.Run(true);
            Assert.That(first.BackoffUntil, Is.EqualTo(_now.AddHours(12)));

            _now = _now.AddHours(6);
            var second = Service.Run(true);

            Assert.That(_http.Requests, Has.Count.EqualTo(1), "no MAM call while backing off");
            Assert.That(second.LastResult, Does.StartWith("Paused after a MAM error"));

            _now = _now.AddHours(7);
            ReplySearch();
            Service.Run(true);
            Assert.That(_http.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void download_error_backs_off()
        {
            ReplySearch(Torrent(1));
            _http.Reply(HttpStatusCode.Forbidden, "text/html", "no");

            var status = Service.Run(true);

            Assert.That(status.BackoffUntil, Is.Not.Null);
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void scheduled_runs_wait_for_the_interval_but_manual_runs_do_not()
        {
            _settings.FillerDryRun = true;
            ReplySearch(Torrent(1));
            Service.Execute(new MamFreeleechFillerCommand { Trigger = CommandTrigger.Scheduled });

            _now = _now.AddHours(1);
            Service.Execute(new MamFreeleechFillerCommand { Trigger = CommandTrigger.Scheduled });
            Assert.That(_http.Requests, Has.Count.EqualTo(1));

            ReplySearch(Torrent(1));
            Service.Execute(new MamFreeleechFillerCommand { Trigger = CommandTrigger.Manual });
            Assert.That(_http.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void off_by_default_and_does_nothing_when_off()
        {
            Assert.That(new MyAnonaMouseSettings().FillerEnabled, Is.False);
            Assert.That(new MyAnonaMouseSettings().FillerDryRun, Is.True);

            _settings.FillerEnabled = false;
            Service.Run(false);

            Assert.That(_http.Requests, Is.Empty);
        }

        [Test]
        public void seeded_filler_torrents_are_removed_after_the_seed_time()
        {
            _store.Save(new TalearrFillerTorrent { TorrentId = "1", ClientHash = "AAA", Category = "mam-seed", State = TalearrFillerTorrent.StateAdded, AddedAt = _now.AddDays(-31) });
            _store.Save(new TalearrFillerTorrent { TorrentId = "2", ClientHash = "BBB", Category = "mam-seed", State = TalearrFillerTorrent.StateAdded, AddedAt = _now.AddDays(-2) });
            _store.Save(new TalearrFillerTorrent { TorrentId = "3", ClientHash = "CCC", Category = "mam-seed", State = TalearrFillerTorrent.StateAdded, AddedAt = _now.AddDays(-2) });
            var removed = new List<string>();
            _client
                .On("GetCategoryTorrents", _ => new List<TalearrSeedingClientTorrent>
                {
                    new TalearrSeedingClientTorrent { DownloadId = "AAA", SeedingSeconds = 31L * 86400 },
                    new TalearrSeedingClientTorrent { DownloadId = "BBB", SeedingSeconds = 2L * 86400 }
                })
                .On("RemoveFillerTorrent", args =>
                {
                    removed.Add((string)args[0]);
                    return null;
                });
            _settings.UnsatisfiedCount = 20;

            Service.Run(true);

            Assert.That(removed, Is.EqualTo(new[] { "AAA" }));
            Assert.That(_store.Torrents["1"].State, Is.EqualTo(TalearrFillerTorrent.StateDone));
            Assert.That(_store.Torrents["2"].State, Is.EqualTo(TalearrFillerTorrent.StateAdded));
            Assert.That(_store.Torrents["3"].State, Is.EqualTo(TalearrFillerTorrent.StateRemoved));
        }

        // Seeding report: filler rows show indexer MyAnonamouse, a Filler marker and 72 h.
        [Test]
        public void seeding_report_lists_filler_torrents_with_the_mam_requirement()
        {
            var filler = new TalearrSeedingClientTorrent { DownloadId = "FFF", Title = "Book 1", Category = "mam-seed", State = "seeding", SeedingSeconds = 3600, IsMamTracker = false };
            _client
                .On("GetSeedingTorrents", _ => new List<TalearrSeedingClientTorrent>())
                .On("GetCategoryTorrents", args => (string)args[0] == "mam-seed" ? new List<TalearrSeedingClientTorrent> { filler } : new List<TalearrSeedingClientTorrent>());
            SeedingFake<IProvideDownloadClient>.Create(out var provider)
                .On("GetDownloadClients", _ => new List<IDownloadClient> { (IDownloadClient)(object)_client });
            SeedingFake<NzbDrone.Core.History.IHistoryService>.Create(out var history);
            SeedingFake<NzbDrone.Core.Books.IBookService>.Create(out var books);
            SeedingFake<NzbDrone.Core.Books.IAuthorService>.Create(out var authors);
            _indexers.Definitions[0].Name = "MyAnonamouse";
            _indexers.Definitions[0].EnableAutomaticSearch = true;
            _store.Status = new TalearrMamFillerStatus { Enabled = true, Preview = new List<TalearrMamFillerPreviewItem> { new TalearrMamFillerPreviewItem { TorrentId = "9" } } };

            var service = new TalearrSeedingService(provider, history, books, authors, (IIndexerFactory)(object)_indexers, new NzbDrone.Common.Cache.CacheManager(), LogManager.GetCurrentClassLogger(), null, _store);
            var report = service.GetReport();
            var row = report.Torrents.Single();

            Assert.Multiple(() =>
            {
                Assert.That(row.IsFiller, Is.True);
                Assert.That(row.Indexer, Is.EqualTo("MyAnonamouse"));
                Assert.That(row.RequiredSeedingSeconds, Is.EqualTo(72 * 3600));
                Assert.That(row.IsPrivateTracker, Is.True);
                Assert.That(report.Filler.Preview.Single().TorrentId, Is.EqualTo("9"));
            });
        }

        // talearr.db schema 8.
        [Test]
        public void store_round_trips_torrents_and_status_in_talearr_db()
        {
            using var folder = new TalearrTestFolder();
            var store = new TalearrMamFillerStore(folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            var added = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);

            store.Save(new TalearrFillerTorrent { TorrentId = "123", Title = "Book", Size = 5 * MiB, IsAudiobook = true, AddedAt = added, ClientHash = "abc", Category = "mam-seed", State = TalearrFillerTorrent.StateAdded });
            store.Save(new TalearrFillerTorrent { TorrentId = "123", Title = "Book", Size = 5 * MiB, IsAudiobook = true, AddedAt = added, ClientHash = "abc", Category = "mam-seed", State = TalearrFillerTorrent.StateDone, UpdatedAt = added.AddDays(30) });
            store.SaveStatus(new TalearrMamFillerStatus { Enabled = true, LastResult = "ok", BackoffUntil = added, Preview = new List<TalearrMamFillerPreviewItem> { new TalearrMamFillerPreviewItem { TorrentId = "5" } } });

            var row = store.All().Single();
            var status = store.GetStatus();

            Assert.Multiple(() =>
            {
                Assert.That(row.State, Is.EqualTo(TalearrFillerTorrent.StateDone));
                Assert.That(row.ClientHash, Is.EqualTo("ABC"));
                Assert.That(row.IsAudiobook, Is.True);
                Assert.That(row.AddedAt, Is.EqualTo(added));
                Assert.That(status.LastResult, Is.EqualTo("ok"));
                Assert.That(status.BackoffUntil, Is.EqualTo(added));
                Assert.That(status.Preview.Single().TorrentId, Is.EqualTo("5"));
            });
        }
    }
}
