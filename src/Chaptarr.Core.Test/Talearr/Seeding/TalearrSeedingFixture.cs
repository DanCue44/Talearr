using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Talearr.Seeding;

namespace Chaptarr.Core.Test.Talearr.Seeding
{
    [TestFixture]
    public class TalearrSeedingFixture
    {
        private const long Hours72 = 72 * 3600;

        [TestCase("uploading", "seeding")]
        [TestCase("stalledUP", "seeding")]
        [TestCase("forcedUP", "seeding")]
        [TestCase("queuedUP", "queued")]
        [TestCase("pausedUP", "paused")]
        [TestCase("stoppedUP", "paused")]
        [TestCase("stoppedDL", "paused")]
        [TestCase("checkingUP", "checking")]
        [TestCase("error", "error")]
        [TestCase("missingFiles", "error")]
        [TestCase("stalledDL", "stalled")]
        [TestCase("downloading", "downloading")]
        [TestCase("metaDL", "downloading")]
        public void should_normalize_qbittorrent_states(string state, string expected)
        {
            Assert.That(TalearrSeedingRules.NormalizeQBittorrentState(state), Is.EqualTo(expected));
        }

        [Test]
        public void should_read_seeding_details_from_qbittorrent_torrent_info()
        {
            var torrent = SeedingTestData.ParseQBittorrent(SeedingTestData.MamTorrentJson);

            var item = QBittorrent.ToSeedingTorrent(torrent, new QBittorrentPreferences(), false);

            Assert.That(item.DownloadId, Is.EqualTo("7955EFD68558D09BAA87E573FEC0BBFDE10C9B34"));
            Assert.That(item.State, Is.EqualTo("seeding"));
            Assert.That(item.Size, Is.EqualTo(215237502));
            Assert.That(item.Uploaded, Is.EqualTo(53809375));
            Assert.That(item.SeedingSeconds, Is.EqualTo(81));
            Assert.That(item.SeedLimitSeconds, Is.EqualTo(43200L * 60));
            Assert.That(item.IsPrivate, Is.True);
            Assert.That(item.IsMamTracker, Is.True);
            Assert.That(item.AddedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1792263075).UtcDateTime));
            Assert.That(item.CompletedAt, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1792263093).UtcDateTime));
            Assert.That(item.Category, Is.EqualTo("books"));
        }

        [Test]
        public void should_treat_missing_qbittorrent_fields_as_unknown()
        {
            var torrent = SeedingTestData.ParseQBittorrent(@"{""hash"":""ab"",""name"":""x"",""state"":""pausedUP"",""completion_on"":-1,""tracker"":""""}");

            var item = QBittorrent.ToSeedingTorrent(torrent, new QBittorrentPreferences(), true);

            Assert.That(item.SeedingSeconds, Is.Null);
            Assert.That(item.Uploaded, Is.Null);
            Assert.That(item.IsPrivate, Is.Null);
            Assert.That(item.IsMamTracker, Is.False);
            Assert.That(item.CompletedAt, Is.Null);
            Assert.That(item.SeedLimitSeconds, Is.Null);
            Assert.That(item.RequireHistoryMatch, Is.True);
        }

        [Test]
        public void should_resolve_effective_seed_limit()
        {
            Assert.That(QBittorrent.GetSeedLimitSeconds(new QBittorrentTorrent { MaxSeedingTime = -1 }, null), Is.Null);
            Assert.That(QBittorrent.GetSeedLimitSeconds(new QBittorrentTorrent { SeedingTimeLimit = 120 }, null), Is.EqualTo(7200));
            Assert.That(QBittorrent.GetSeedLimitSeconds(new QBittorrentTorrent { SeedingTimeLimit = -2 }, new QBittorrentPreferences { MaxSeedingTimeEnabled = true, MaxSeedingTime = 60 }), Is.EqualTo(3600));
            Assert.That(QBittorrent.GetSeedLimitSeconds(new QBittorrentTorrent { SeedingTimeLimit = -2 }, new QBittorrentPreferences()), Is.Null);
        }

        [Test]
        public void should_require_72_hours_for_mam_and_use_indexer_seed_time_as_goal()
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", "seeding", 3600), "qBittorrent (VPN)", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, null, null);

            Assert.That(row.RequiredSeedingSeconds, Is.EqualTo(Hours72));
            Assert.That(row.SeedGoalSeconds, Is.EqualTo(720 * 3600));
            Assert.That(row.RequirementMet, Is.False);
            Assert.That(row.IsPrivateTracker, Is.True);
            Assert.That(row.AtRisk, Is.False);
            Assert.That(row.RiskReason, Is.Null);
        }

        [Test]
        public void should_flag_mam_torrent_paused_before_72_hours()
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", "paused", 3600), "qB", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, null, null);

            Assert.That(row.AtRisk, Is.True);
            Assert.That(row.RiskReason, Is.EqualTo("Paused before 72 h of seeding"));
        }

        [Test]
        public void should_not_flag_paused_torrent_that_met_the_requirement()
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", "paused", Hours72 + 1), "qB", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, null, null);

            Assert.That(row.RequirementMet, Is.True);
            Assert.That(row.AtRisk, Is.False);
        }

        [TestCase("queued", null, null, "Queued, not seeding")]
        [TestCase("error", null, "the download is missing files", "Download client error: the download is missing files")]
        [TestCase("error", null, null, "Download client reports an error")]
        [TestCase("seeding", "Unrecognized host/IP", null, "Tracker error: Unrecognized host/IP")]
        [TestCase("stalled", "timed out", null, "Tracker error: timed out")]
        public void should_flag_private_risks(string state, string trackerError, string clientMessage, string reason)
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", state, 60), "qB", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, trackerError, clientMessage);

            Assert.That(row.AtRisk, Is.True);
            Assert.That(row.RiskReason, Is.EqualTo(reason));
        }

        [TestCase("downloading")]
        [TestCase("checking")]
        [TestCase("seeding")]
        public void should_not_flag_healthy_or_unfinished_private_torrents(string state)
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", state, 60), "qB", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, null, null);

            Assert.That(row.AtRisk, Is.False);
        }

        [Test]
        public void should_never_flag_public_torrents()
        {
            var indexer = new TalearrSeedingIndexerInfo { Name = "Knaben (Prowlarr)" };
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", "paused", 10, false, false), "qB", null, indexer);
            TalearrSeedingRules.ApplyRisk(row, "dead tracker", null);

            Assert.That(row.IsPrivateTracker, Is.False);
            Assert.That(row.RequiredSeedingSeconds, Is.Null);
            Assert.That(row.RequirementMet, Is.True);
            Assert.That(row.AtRisk, Is.False);
            Assert.That(TalearrSeedingRules.NeedsTrackerCheck(row), Is.False);
        }

        [Test]
        public void should_use_indexer_seed_time_then_client_limit_for_other_private_trackers()
        {
            var withSeedTime = new TalearrSeedingIndexerInfo { Name = "Other", SeedTimeSeconds = 7200 };
            var torrent = SeedingTestData.Torrent("A", "seeding", 60, true, false);

            Assert.That(TalearrSeedingRules.BuildRow(torrent, "qB", null, withSeedTime).RequiredSeedingSeconds, Is.EqualTo(7200));
            Assert.That(TalearrSeedingRules.BuildRow(torrent, "qB", null, null).RequiredSeedingSeconds, Is.EqualTo(30 * 24 * 3600));
        }

        [Test]
        public void should_leave_requirement_unknown_without_client_seeding_time()
        {
            var row = TalearrSeedingRules.BuildRow(SeedingTestData.Torrent("A", "paused", null), "Deluge", null, SeedingTestData.Mam());
            TalearrSeedingRules.ApplyRisk(row, null, null);

            Assert.That(row.RequirementMet, Is.Null);
            Assert.That(row.AtRisk, Is.False);
        }

        [Test]
        public void should_read_tracker_errors_and_ignore_pseudo_trackers()
        {
            Assert.That(TalearrSeedingRules.ReadTrackerError(new[] { ("** [DHT] **", 4, "x"), ("https://t/announce", 4, "Unrecognized host/IP") }), Is.EqualTo("Unrecognized host/IP"));
            Assert.That(TalearrSeedingRules.ReadTrackerError(new[] { ("https://a/announce", 4, "down"), ("https://b/announce", 2, string.Empty) }), Is.Null);
            Assert.That(TalearrSeedingRules.ReadTrackerError(new[] { ("https://a/announce", 1, string.Empty) }), Is.Null);
            Assert.That(TalearrSeedingRules.ReadTrackerError(new[] { ("https://a/announce", 6, string.Empty) }), Is.EqualTo("tracker not working"));
            Assert.That(TalearrSeedingRules.ReadTrackerError(new[] { ("** [PeX] **", 4, "x") }), Is.Null);
        }

        [Test]
        public void should_build_mam_block_from_persisted_status()
        {
            var settings = new MyAnonaMouseSettings
            {
                UserClass = "User",
                IsVip = false,
                UnsatisfiedCount = 10,
                UnsatisfiedLimit = 20,
                FreeleechWedges = 5,
                BonusPoints = 7300,
                UnsatisfiedStatusRefreshedUtc = new DateTime(2026, 10, 17, 19, 55, 0)
            };

            var mam = TalearrSeedingRules.BuildMamStatus(settings);

            Assert.That(mam.UserClass, Is.EqualTo("User"));
            Assert.That(mam.Unsatisfied, Is.EqualTo(10));
            Assert.That(mam.UnsatisfiedLimit, Is.EqualTo(20));
            Assert.That(mam.Wedges, Is.EqualTo(5));
            Assert.That(mam.BonusPoints, Is.EqualTo(7300));
            Assert.That(mam.CheckedAt.Value.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(TalearrSeedingRules.BuildMamStatus(null), Is.Null);
        }

        [Test]
        public void should_list_at_risk_private_torrents_in_health_message()
        {
            var rows = new List<TalearrSeedingTorrent>
            {
                new TalearrSeedingTorrent { DownloadId = "B", BookTitle = "Deep End", IsPrivateTracker = true, AtRisk = true, RiskReason = "Paused before 72 h of seeding" },
                new TalearrSeedingTorrent { DownloadId = "C", Title = "Public", IsPrivateTracker = false, AtRisk = false },
                new TalearrSeedingTorrent { DownloadId = "A", Title = "Other.Release", IsPrivateTracker = true, AtRisk = true, RiskReason = "Queued, not seeding" }
            };

            Assert.That(TalearrSeedingRiskCheck.GetMessage(rows), Is.EqualTo("2 private-tracker torrents are at risk of a hit and run: 'Other.Release' (Queued, not seeding), " +
                "'Deep End' (Paused before 72 h of seeding). See Activity > Seeding."));
            Assert.That(TalearrSeedingRiskCheck.GetMessage(rows.Skip(1).Take(1)), Is.Null);
        }

        [Test]
        public void should_say_and_more_beyond_three_at_risk_torrents()
        {
            var rows = Enumerable.Range(1, 5)
                .Select(i => new TalearrSeedingTorrent { DownloadId = i.ToString(), Title = "T" + i, IsPrivateTracker = true, AtRisk = true, RiskReason = "Queued, not seeding" })
                .ToList();

            Assert.That(TalearrSeedingRiskCheck.GetMessage(rows), Does.Contain("and 2 more"));
        }

        // Service: fake qBittorrent-like client, history, books and indexers.
        private ITalearrSeedingService CreateService(DateTime now, out SeedingFake<IFakeSeedingClient> clientFake, out SeedingFake<IHistoryService> historyFake)
        {
            clientFake = SeedingFake<IFakeSeedingClient>.Create(out var client);
            clientFake
                .On("get_Protocol", _ => DownloadProtocol.Torrent)
                .On("get_Name", _ => "qBittorrent")
                .On("get_Definition", _ => new DownloadClientDefinition { Id = 2, Name = "qBittorrent (VPN)" })
                .On("GetSeedingTorrents", _ => new List<TalearrSeedingClientTorrent>
                {
                    SeedingTestData.Torrent("AAA", "seeding", 3600),
                    SeedingTestData.Torrent("BBB", "paused", 600),
                    SeedingTestData.Torrent("CCC", "seeding", 10, false, false)
                })
                .On("GetTrackerError", _ => null);

            SeedingFake<IProvideDownloadClient>.Create(out var provider)
                .On("GetDownloadClients", _ => new List<IDownloadClient> { client });

            var grabs = new List<EntityHistory>
            {
                SeedingTestData.Grab("AAA", 3757, 16, "MyAnonamouse", "qBittorrent (VPN)", now.AddHours(-2)),
                SeedingTestData.Grab("BBB", 3758, 16, "MyAnonamouse", "qBittorrent (VPN)", now.AddHours(-30))
            };
            var recent = new List<EntityHistory>
            {
                SeedingTestData.Grab("DDD", 3759, 16, "MyAnonamouse", "qBittorrent (VPN)", now.AddHours(-5)),
                SeedingTestData.Grab("EEE", 3759, 16, "MyAnonamouse", "qBittorrent (VPN)", now.AddHours(-80)),
                SeedingTestData.Grab("FFF", 3759, 16, "MyAnonamouse", "Other client", now.AddHours(-5)),
                SeedingTestData.Grab("GGG", 3759, 16, "Knaben (Prowlarr)", "qBittorrent (VPN)", now.AddHours(-5))
            };

            historyFake = SeedingFake<IHistoryService>.Create(out var history);
            historyFake
                .On("FindByDownloadIds", _ => grabs)
                .On("Since", _ => recent);

            SeedingFake<IBookService>.Create(out var books)
                .On("GetExistingBooks", _ => SeedingTestData.Books(
                    (3757, 16, "Deep End", BookMediaType.Audiobook),
                    (3758, 16, "Love, Theoretically", BookMediaType.Ebook),
                    (3759, 16, "Not Yet", BookMediaType.Audiobook)));

            SeedingFake<IAuthorService>.Create(out var authors)
                .On("GetAuthor", args => (int)args[0] == 16 ? new Author { Id = 16, Name = "Ali Hazelwood" } : null);

            SeedingFake<IIndexerFactory>.Create(out var indexers)
                .On("All", _ => new List<IndexerDefinition>
                {
                    new IndexerDefinition
                    {
                        Id = 6,
                        Name = "MyAnonamouse",
                        EnableRss = true,
                        Settings = new MyAnonaMouseSettings
                        {
                            SeedTimeHours = 720,
                            UserClass = "Mouse",
                            UnsatisfiedCount = 10,
                            UnsatisfiedLimit = 20,
                            FreeleechWedges = 5,
                            BonusPoints = 7300,
                            UnsatisfiedStatusRefreshedUtc = now.AddMinutes(-5)
                        }
                    }
                });

            return new TalearrSeedingService(provider, history, books, authors, indexers, new CacheManager(), LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_map_torrents_to_grabs_and_flag_missing_mam_grabs()
        {
            var now = DateTime.UtcNow;
            var report = CreateService(now, out _, out _).GetReport();

            Assert.That(report.Torrents.Select(t => t.DownloadId), Is.EquivalentTo(new[] { "AAA", "BBB", "CCC", "DDD" }));

            var mapped = report.Torrents.Single(t => t.DownloadId == "AAA");
            Assert.That(mapped.BookId, Is.EqualTo(3757));
            Assert.That(mapped.BookTitle, Is.EqualTo("Deep End"));
            Assert.That(mapped.AuthorName, Is.EqualTo("Ali Hazelwood"));
            Assert.That(mapped.MediaType, Is.EqualTo("audiobook"));
            Assert.That(mapped.Indexer, Is.EqualTo("MyAnonamouse"));
            Assert.That(mapped.DownloadClient, Is.EqualTo("qBittorrent (VPN)"));
            Assert.That(mapped.RequiredSeedingSeconds, Is.EqualTo(Hours72));
            Assert.That(mapped.SeedGoalSeconds, Is.EqualTo(720 * 3600));
            Assert.That(mapped.AtRisk, Is.False);

            var paused = report.Torrents.Single(t => t.DownloadId == "BBB");
            Assert.That(paused.MediaType, Is.EqualTo("ebook"));
            Assert.That(paused.AtRisk, Is.True);

            var unmapped = report.Torrents.Single(t => t.DownloadId == "CCC");
            Assert.That(unmapped.BookId, Is.Null);
            Assert.That(unmapped.Indexer, Is.Null);
            Assert.That(unmapped.IsPrivateTracker, Is.False);

            var missing = report.Torrents.Single(t => t.DownloadId == "DDD");
            Assert.That(missing.State, Is.EqualTo("missing"));
            Assert.That(missing.AtRisk, Is.True);
            Assert.That(missing.RiskReason, Does.StartWith("Not in the download client before 72 h"));

            Assert.That(report.Torrents.Take(2).All(t => t.AtRisk), Is.True);

            Assert.That(report.Summary.Total, Is.EqualTo(4));
            Assert.That(report.Summary.Seeding, Is.EqualTo(2));
            Assert.That(report.Summary.AtRisk, Is.EqualTo(2));
            Assert.That(report.Summary.PrivateUnsatisfied, Is.EqualTo(3));

            Assert.That(report.Mam.UserClass, Is.EqualTo("Mouse"));
            Assert.That(report.Mam.Unsatisfied, Is.EqualTo(10));
        }

        [Test]
        public void should_check_trackers_only_for_unmet_private_seeding_torrents()
        {
            var service = CreateService(DateTime.UtcNow, out var client, out _);
            client.On("GetTrackerError", args => (string)args[0] == "AAA" ? "Unrecognized host/IP" : null);

            var report = service.GetReport();

            Assert.That(client.CallCount("GetTrackerError"), Is.EqualTo(1));
            Assert.That(report.Torrents.Single(t => t.DownloadId == "AAA").RiskReason, Is.EqualTo("Tracker error: Unrecognized host/IP"));
        }

        [Test]
        public void should_cache_client_reads()
        {
            var service = CreateService(DateTime.UtcNow, out var client, out _);

            service.GetReport();
            service.GetReport();
            Assert.That(client.CallCount("GetSeedingTorrents"), Is.EqualTo(1));

            service.GetReport(TimeSpan.Zero);
            Assert.That(client.CallCount("GetSeedingTorrents"), Is.EqualTo(2));
        }

        [Test]
        public void should_keep_report_when_a_client_fails()
        {
            var service = CreateService(DateTime.UtcNow, out var client, out _);
            client.On("GetSeedingTorrents", _ => throw new InvalidOperationException("offline"));

            var report = service.GetReport();

            // Missing grabs are only judged for clients that answered.
            Assert.That(report.Torrents, Is.Empty);
            Assert.That(report.Summary.Total, Is.EqualTo(0));
        }

        [Test]
        public void should_serialize_contract_shape_with_nulls_present()
        {
            var report = new TalearrSeedingReport
            {
                GeneratedAt = new DateTime(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc),
                Torrents = new List<TalearrSeedingTorrent>
                {
                    new TalearrSeedingTorrent { DownloadId = "abc123", State = "seeding" }
                }
            };

            using var doc = JsonDocument.Parse(STJson.ToJson(report));
            var root = doc.RootElement;

            Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "generatedAt", "torrents", "summary", "mam", "filler" }));
            Assert.That(root.GetProperty("generatedAt").GetString(), Is.EqualTo("2026-10-17T20:00:00Z"));
            Assert.That(root.GetProperty("mam").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(root.GetProperty("summary").EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "total", "seeding", "atRisk", "privateUnsatisfied" }));

            var torrent = root.GetProperty("torrents")[0];
            Assert.That(torrent.EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
            {
                "downloadId", "title", "bookId", "bookTitle", "authorName", "mediaType", "indexer",
                "isPrivateTracker", "downloadClient", "state", "size", "uploaded", "ratio", "seedingSeconds",
                "requiredSeedingSeconds", "seedGoalSeconds", "requirementMet", "atRisk", "riskReason",
                "addedAt", "completedAt", "isFiller"
            }));
            Assert.That(torrent.GetProperty("bookId").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(torrent.GetProperty("riskReason").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(torrent.GetProperty("atRisk").GetBoolean(), Is.False);

            report.Mam = TalearrSeedingRules.BuildMamStatus(new MyAnonaMouseSettings());
            using var withMam = JsonDocument.Parse(STJson.ToJson(report));
            Assert.That(withMam.RootElement.GetProperty("mam").EnumerateObject().Select(p => p.Name), Is.EquivalentTo(new[]
            {
                "userClass", "isVip", "unsatisfied", "unsatisfiedLimit", "wedges", "bonusPoints", "checkedAt",
                "vipUntil", "ratio", "restricted", "bonusAutoSpend", "bonusDryRun", "bonusPurchases",
                "memberSince", "uploadedBytes", "downloadedBytes", "daysUntilEligible", "uploadedRemainingBytes", "ratioOk"
            }));
        }
    }
}
