using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.QualityLadder;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.QualityLadder
{
    [TestFixture]
    public class TalearrQualityLadderUpgradeFixture
    {
        private const int TenHours = 10 * 3600;

        private sealed class FakeLadderSettings : ITalearrQualityLadderSettingsService
        {
            public TalearrQualityLadderSettings Settings { get; set; } = new TalearrQualityLadderSettings();
            public TalearrQualityLadderSettings Get() => Settings.Clone();
            public TalearrQualityLadderSettings Save(TalearrQualityLadderSettings settings) => Settings = settings;
        }

        private sealed class FakeRecordingsSettings : ITalearrRecordingsSettingsService
        {
            public TalearrRecordingsSettings Settings { get; set; } = new TalearrRecordingsSettings();
            public TalearrRecordingsSettings Get() => Settings.Clone();
            public TalearrRecordingsSettings Save(TalearrRecordingsSettings settings) => Settings = settings;
        }

        private FakeLadderSettings _ladderSettings;
        private FakeRecordingsSettings _recordingsSettings;
        private QualityProfile _audioProfile;
        private QualityProfile _ebookProfile;
        private Author _author;
        private Book _book;

        private static long SizeFor(int kbps, int seconds) => (long)kbps * 1000 / 8 * seconds;

        [SetUp]
        public void SetUp()
        {
            _ladderSettings = new FakeLadderSettings();
            _ladderSettings.Settings.AudioUpgrades = true;
            _ladderSettings.Settings.EbookUpgrades = true;
            _recordingsSettings = new FakeRecordingsSettings();

            _audioProfile = new QualityProfile
            {
                Id = 2,
                Name = "Spoken",
                ProfileType = ProfileType.Audiobook,
                UpgradeAllowed = true,
                Cutoff = Quality.M4B.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };

            // Ebook formats grouped: the profile ranks them equal, the Talearr order decides.
            _ebookProfile = new QualityProfile
            {
                Id = 1,
                Name = "eBook",
                ProfileType = ProfileType.Ebook,
                UpgradeAllowed = true,
                Cutoff = 1000,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem
                    {
                        Id = 1000,
                        Name = "Ebooks",
                        Allowed = true,
                        Items = new List<QualityProfileQualityItem>
                        {
                            new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true },
                            new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true }
                        }
                    }
                }
            };

            _author = new Author
            {
                Id = 7,
                Name = "Andy Weir",
                AudiobookQualityProfileId = 2,
                EbookQualityProfileId = 1,
                AudiobookQualityProfile = _audioProfile,
                EbookQualityProfile = _ebookProfile
            };

            _book = new Book { Id = 41, Title = "Project Hail Mary", MediaType = BookMediaType.Audiobook, Author = _author, DurationMinutes = 600 };
            _book.BookFiles = new List<BookFile> { Mp3File(32) };
        }

        private BookFile Mp3File(int kbps, string narrator = "Ray Porter")
        {
            return new BookFile
            {
                Id = 1,
                Path = "/books/Andy Weir/Project Hail Mary/book.mp3",
                Size = SizeFor(kbps, TenHours),
                DurationSeconds = TenHours,
                Narrator = narrator,
                MediaType = "audiobook",
                Quality = new QualityModel(Quality.MP3),
                MediaInfo = new MediaInfoModel { AudioFormat = "MPEG Audio", AudioBitrate = kbps, AudioChannels = 2 }
            };
        }

        private RemoteBook Release(string title, Quality quality, long size, Book book = null, string narrator = null)
        {
            return new RemoteBook
            {
                Author = _author,
                Books = new List<Book> { book ?? _book },
                Release = new ReleaseInfo { Title = title, Size = size, Narrator = narrator },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality) },
                CustomFormats = new List<CustomFormat>()
            };
        }

        private TalearrQualityLadderService Ladder()
        {
            return new TalearrQualityLadderService(_ladderSettings, LogManager.GetCurrentClassLogger(), _recordingsSettings);
        }

        private string Reason(RemoteBook release, QualityProfile profile = null)
        {
            return Ladder().UpgradeReason(release, release.Books[0], profile ?? _audioProfile, 0, 0);
        }

        [Test]
        public void defaults_rank_only_and_never_upgrade()
        {
            var defaults = new TalearrQualityLadderSettings();

            Assert.That(defaults.AudioRanking, Is.True);
            Assert.That(defaults.AudioUpgrades, Is.False);
            Assert.That(defaults.EbookUpgrades, Is.False);
            Assert.That(defaults.MinimumAudioKbps, Is.EqualTo(0));
            Assert.That(defaults.EbookFormats, Is.EqualTo(new[] { "EPUB", "AZW3", "MOBI", "PDF" }));
        }

        [Test]
        public void a_clearly_better_copy_of_the_same_recording_is_an_upgrade()
        {
            var reason = Reason(Release("Andy Weir - Project Hail Mary [128kbps] MP3", Quality.MP3, SizeFor(128, TenHours), narrator: "Ray Porter"));

            Assert.That(reason, Does.StartWith("Talearr: better audio of the same recording"));
        }

        [Test]
        public void bitrate_is_estimated_from_size_against_the_files_runtime()
        {
            Assert.That(Reason(Release("Andy Weir - Project Hail Mary MP3", Quality.MP3, SizeFor(128, TenHours))), Is.Not.Null);
            Assert.That(Reason(Release("Andy Weir - Project Hail Mary MP3", Quality.MP3, SizeFor(40, TenHours))), Is.Null, "40 against 32 kbps is the same tier");
        }

        [Test]
        public void a_different_narrator_is_a_different_recording_and_never_an_upgrade()
        {
            Assert.That(Reason(Release("Andy Weir - Project Hail Mary [128kbps]", Quality.MP3, SizeFor(128, TenHours), narrator: "Somebody Else")), Is.Null);
        }

        [Test]
        public void abridged_and_unabridged_never_replace_each_other()
        {
            _book.BookFiles[0].AudioProductionType = "Unabridged";

            Assert.That(Reason(Release("Andy Weir - Project Hail Mary (Abridged) [128kbps]", Quality.MP3, SizeFor(128, TenHours))), Is.Null);
        }

        [Test]
        public void a_bundle_is_never_a_ladder_upgrade()
        {
            var release = Release("Andy Weir - Collection [128kbps]", Quality.MP3, SizeFor(128, TenHours));
            release.Books.Add(new Book { Id = 42, Title = "Artemis", MediaType = BookMediaType.Audiobook });

            Assert.That(Reason(release), Is.Null);
        }

        [Test]
        public void no_upgrade_when_the_profile_does_not_allow_upgrades()
        {
            _audioProfile.UpgradeAllowed = false;

            Assert.That(Reason(Release("Andy Weir - Project Hail Mary [128kbps]", Quality.MP3, SizeFor(128, TenHours))), Is.Null);
        }

        [Test]
        public void no_upgrade_when_ladder_upgrades_or_the_upgrade_guard_are_off()
        {
            var release = Release("Andy Weir - Project Hail Mary [128kbps]", Quality.MP3, SizeFor(128, TenHours));

            _ladderSettings.Settings.AudioUpgrades = false;
            Assert.That(Reason(release), Is.Null);

            _ladderSettings.Settings.AudioUpgrades = true;
            _recordingsSettings.Settings.UpgradeGuard = false;
            Assert.That(Reason(release), Is.Null);
        }

        [Test]
        public void good_files_are_left_alone()
        {
            _book.BookFiles = new List<BookFile> { Mp3File(128) };

            Assert.That(Reason(Release("Andy Weir - Project Hail Mary [320kbps]", Quality.MP3, SizeFor(320, TenHours))), Is.Null);
        }

        [Test]
        public void a_lower_custom_format_score_is_never_a_ladder_upgrade()
        {
            var release = Release("Andy Weir - Project Hail Mary [128kbps]", Quality.MP3, SizeFor(128, TenHours));

            Assert.That(Ladder().UpgradeReason(release, _book, _audioProfile, 0, 10), Is.Null);
        }

        [Test]
        public void an_ebook_in_a_preferred_format_upgrades_only_when_turned_on()
        {
            var ebook = new Book { Id = 50, Title = "Project Hail Mary", MediaType = BookMediaType.Ebook, Author = _author };
            ebook.BookFiles = new List<BookFile> { new BookFile { Id = 9, Path = "/b/phm.pdf", MediaType = "ebook", Quality = new QualityModel(Quality.PDF) } };

            var release = Release("Andy Weir - Project Hail Mary EPUB", Quality.EPUB, 2_000_000, ebook);
            Assert.That(Ladder().UpgradeReason(release, ebook, _ebookProfile, 0, 0), Does.Contain("EPUB is preferred over PDF"));

            var pdf = Release("Andy Weir - Project Hail Mary PDF", Quality.PDF, 2_000_000, ebook);
            ebook.BookFiles[0].Quality = new QualityModel(Quality.EPUB);
            Assert.That(Ladder().UpgradeReason(pdf, ebook, _ebookProfile, 0, 0), Is.Null, "never down the order");

            ebook.BookFiles[0].Quality = new QualityModel(Quality.PDF);
            _ladderSettings.Settings.EbookUpgrades = false;
            Assert.That(Ladder().UpgradeReason(release, ebook, _ebookProfile, 0, 0), Is.Null, "rank only by default");
        }

        [Test]
        public void the_profile_keeps_the_last_word_on_formats_it_orders_itself()
        {
            var ebook = new Book { Id = 50, Title = "Project Hail Mary", MediaType = BookMediaType.Ebook, Author = _author };
            ebook.BookFiles = new List<BookFile> { new BookFile { Id = 9, Path = "/b/phm.pdf", MediaType = "ebook", Quality = new QualityModel(Quality.PDF) } };
            var ungrouped = new QualityProfile
            {
                UpgradeAllowed = true,
                Cutoff = Quality.EPUB.Id,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.EPUB, Allowed = true },
                    new QualityProfileQualityItem { Quality = Quality.PDF, Allowed = true }
                }
            };

            var release = Release("Andy Weir - Project Hail Mary EPUB", Quality.EPUB, 2_000_000, ebook);
            Assert.That(Ladder().UpgradeReason(release, ebook, ungrouped, 0, 0), Is.Null);
        }

        [Test]
        public void upgrade_disk_specification_accepts_a_ladder_upgrade_and_rejects_without_it()
        {
            var release = Release("Andy Weir - Project Hail Mary [128kbps] MP3", Quality.MP3, SizeFor(128, TenHours));

            Assert.That(DiskSpec(Ladder()).IsSatisfiedBy(release, null).Accepted, Is.True);
            Assert.That(DiskSpec(null).IsSatisfiedBy(release, null).Accepted, Is.False, "upstream behavior without the ladder");

            _ladderSettings.Settings.AudioUpgrades = false;
            Assert.That(DiskSpec(Ladder()).IsSatisfiedBy(release, null).Accepted, Is.False, "off by default keeps upstream behavior");
        }

        [Test]
        public void a_ladder_upgrade_is_not_blocked_by_a_met_quality_cutoff()
        {
            _audioProfile.Cutoff = Quality.MP3.Id;
            var release = Release("Andy Weir - Project Hail Mary [128kbps] MP3", Quality.MP3, SizeFor(128, TenHours));

            Assert.That(CutoffSpec(null).IsSatisfiedBy(release, null).Accepted, Is.False, "upstream: cutoff met");
            Assert.That(CutoffSpec(Ladder()).IsSatisfiedBy(release, null).Accepted, Is.True);

            _book.BookFiles = new List<BookFile> { Mp3File(128) };
            Assert.That(CutoffSpec(Ladder()).IsSatisfiedBy(Release("Andy Weir - Project Hail Mary [320kbps] MP3", Quality.MP3, SizeFor(320, TenHours)), null).Accepted, Is.False, "Good files meet the ladder cutoff");
        }

        [Test]
        public void comparer_prefers_the_better_tier_when_the_profile_ties()
        {
            var low = Decision(Release("Andy Weir - Project Hail Mary [32kbps] M4B", Quality.M4B, SizeFor(80, TenHours)));
            var good = Decision(Release("Andy Weir - Project Hail Mary M4B", Quality.M4B, SizeFor(64, TenHours)));

            var comparer = Comparer();

            Assert.That(comparer.Compare(good, low), Is.GreaterThan(0));
            Assert.That(comparer.Compare(low, good), Is.LessThan(0));
            Assert.That(comparer.CompareWithReasons(good, low).Reasons.Single().Reason, Does.StartWith("Better audio quality tier"));
        }

        [Test]
        public void an_implausibly_small_release_ranks_below_a_lower_format()
        {
            var tiny = Decision(Release("Andy Weir - Project Hail Mary M4B", Quality.M4B, 15L * 1024 * 1024));
            var mp3 = Decision(Release("Andy Weir - Project Hail Mary MP3", Quality.MP3, SizeFor(128, TenHours)));

            Assert.That(Comparer().Compare(mp3, tiny), Is.GreaterThan(0));
        }

        [Test]
        public void audio_ranking_can_be_turned_off()
        {
            _ladderSettings.Settings.AudioRanking = false;
            var tiny = Decision(Release("Andy Weir - Project Hail Mary M4B", Quality.M4B, 15L * 1024 * 1024));
            var mp3 = Decision(Release("Andy Weir - Project Hail Mary MP3", Quality.MP3, SizeFor(128, TenHours)));

            Assert.That(Comparer().Compare(mp3, tiny), Is.LessThan(0), "the profile prefers M4B as before");
        }

        [Test]
        public void comparer_orders_tied_ebook_formats_by_preference()
        {
            var ebook = new Book { Id = 50, Title = "Project Hail Mary", MediaType = BookMediaType.Ebook, Author = _author };
            var pdf = Decision(Release("Andy Weir - Project Hail Mary PDF", Quality.PDF, 2_000_000, ebook));
            var epub = Decision(Release("Andy Weir - Project Hail Mary EPUB", Quality.EPUB, 1_000_000, ebook));

            Assert.That(Comparer().Compare(epub, pdf), Is.GreaterThan(0));

            _ladderSettings.Settings.EbookFormats = new List<string> { "PDF", "EPUB" };
            Assert.That(Comparer().Compare(epub, pdf), Is.LessThan(0));
        }

        [Test]
        public void minimum_bitrate_rejects_only_when_set()
        {
            var spec = new TalearrMinimumBitrateSpecification(Ladder(), LogManager.GetCurrentClassLogger());
            var low = Release("Andy Weir - Project Hail Mary [24kbps]", Quality.MP3, SizeFor(24, TenHours));
            var tiny = Release("Andy Weir - Project Hail Mary", Quality.M4B, 2L * 1024 * 1024);

            Assert.That(spec.IsSatisfiedBy(low, null).Accepted, Is.True, "off by default");
            Assert.That(spec.IsSatisfiedBy(tiny, null).Accepted, Is.True, "off by default");

            _ladderSettings.Settings.MinimumAudioKbps = 32;
            Assert.That(spec.IsSatisfiedBy(low, null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(tiny, null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(Release("Andy Weir - Project Hail Mary", Quality.M4B, SizeFor(64, TenHours)), null).Accepted, Is.True);
        }

        [Test]
        public void import_guard_allows_a_cross_codec_ladder_step_only_when_ladder_upgrades_are_on()
        {
            var existing = new RecordingAudioFacts { FileCount = 20, DurationSeconds = TenHours, BitrateKbps = 32, Channels = 1, Codec = "mp3" };
            var incoming = new RecordingAudioFacts { FileCount = 1, DurationSeconds = TenHours, BitrateKbps = 64, Channels = 1, Codec = "aac", SingleM4b = false };
            var sameQuality = new UpgradeProfileComparison();

            var without = TalearrUpgradeRules.Decide(existing, incoming, sameQuality, TalearrRecordingsSettings.Default);
            var with = TalearrUpgradeRules.Decide(existing, incoming, sameQuality, TalearrRecordingsSettings.Default, ladderUpgrades: true);

            Assert.That(without.Kind, Is.EqualTo(UpgradeVerdictKind.Reject), "upstream guard rule: gains only count within a codec");
            Assert.That(with.Kind, Is.EqualTo(UpgradeVerdictKind.Allow));
            Assert.That(with.Reason, Does.Contain("audio quality ladder"));
        }

        [Test]
        public void import_guard_still_rejects_a_different_recording_with_ladder_upgrades_on()
        {
            var existing = new RecordingAudioFacts { DurationSeconds = TenHours, BitrateKbps = 32, Codec = "mp3", Narrators = new List<string> { "Ray Porter" } };
            var incoming = new RecordingAudioFacts { DurationSeconds = TenHours, BitrateKbps = 64, Codec = "aac", Narrators = new List<string> { "Somebody Else" } };

            var verdict = TalearrUpgradeRules.Decide(existing, incoming, new UpgradeProfileComparison(), TalearrRecordingsSettings.Default, ladderUpgrades: true);

            Assert.That(verdict.Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
            Assert.That(verdict.Rule, Is.EqualTo(TalearrUpgradeRules.RuleSameRecording));

            var abridged = new RecordingAudioFacts { DurationSeconds = TenHours, BitrateKbps = 32, Codec = "mp3", Abridged = AbridgedMarker.Unabridged };
            var abridgedIncoming = new RecordingAudioFacts { DurationSeconds = TenHours, BitrateKbps = 64, Codec = "aac", Abridged = AbridgedMarker.Abridged };
            Assert.That(TalearrUpgradeRules.Decide(abridged, abridgedIncoming, new UpgradeProfileComparison(), TalearrRecordingsSettings.Default, ladderUpgrades: true).Kind, Is.EqualTo(UpgradeVerdictKind.Reject));
        }

        private DownloadDecision Decision(RemoteBook remoteBook)
        {
            return new DownloadDecision(remoteBook);
        }

        private DownloadDecisionComparer Comparer()
        {
            return new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), null, null, LogManager.GetCurrentClassLogger(), qualityLadder: Ladder());
        }

        private static ICustomFormatCalculationService NoFormats()
        {
            var (service, fake) = Fake<ICustomFormatCalculationService>.Create();
            fake.On(nameof(ICustomFormatCalculationService.ParseCustomFormat), _ => new List<CustomFormat>());
            return service;
        }

        private UpgradeDiskSpecification DiskSpec(ITalearrQualityLadder ladder)
        {
            return new UpgradeDiskSpecification(new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()), null, NoFormats(), LogManager.GetCurrentClassLogger(), ladder);
        }

        private CutoffSpecification CutoffSpec(ITalearrQualityLadder ladder)
        {
            return new CutoffSpecification(new UpgradableSpecification(ConfigServiceTestProxy.Create(), LogManager.GetCurrentClassLogger()), NoFormats(), LogManager.GetCurrentClassLogger(), ladder);
        }
    }

    [TestFixture]
    public class TalearrQualityLadderSettingsFixture
    {
        private Dictionary<string, string> _rows;
        private TalearrQualityLadderSettingsService _service;

        [SetUp]
        public void SetUp()
        {
            _rows = new Dictionary<string, string>();

            var (repository, fake) = Fake<IConfigRepository>.Create();
            fake.On(nameof(IConfigRepository.Get), args => _rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _rows[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            _service = new TalearrQualityLadderSettingsService(repository, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void nothing_stored_gives_the_safe_defaults()
        {
            var settings = _service.Get();

            Assert.That(settings.AudioRanking, Is.True);
            Assert.That(settings.AudioUpgrades, Is.False);
            Assert.That(settings.EbookUpgrades, Is.False);
            Assert.That(settings.MinimumAudioKbps, Is.EqualTo(0));
            Assert.That(settings.EbookFormats, Is.EqualTo(TalearrEbookPreference.DefaultOrder));
        }

        [Test]
        public void settings_are_stored_as_talearr_config_rows_and_cleaned()
        {
            _service.Save(new TalearrQualityLadderSettings
            {
                AudioRanking = false,
                AudioUpgrades = true,
                MinimumAudioKbps = 999,
                EbookFormats = new List<string> { "azw3", "epub", "docx" },
                EbookUpgrades = true
            });

            Assert.That(_rows.Keys.All(k => k.StartsWith("talearr.ladder.")), Is.True);
            Assert.That(_rows["talearr.ladder.ebookformats"], Is.EqualTo("AZW3,EPUB"));

            var settings = _service.Get();
            Assert.That(settings.AudioRanking, Is.False);
            Assert.That(settings.AudioUpgrades, Is.True);
            Assert.That(settings.MinimumAudioKbps, Is.EqualTo(TalearrQualityLadderSettings.MaxMinimumAudioKbps));
            Assert.That(settings.EbookFormats, Is.EqualTo(new[] { "AZW3", "EPUB" }));
            Assert.That(settings.EbookUpgrades, Is.True);
        }

        [Test]
        public void unreadable_values_fall_back_to_the_defaults()
        {
            _rows["talearr.ladder.audioupgrades"] = "maybe";
            _rows["talearr.ladder.minaudiokbps"] = "lots";

            Assert.That(_service.Get().AudioUpgrades, Is.False);
            Assert.That(_service.Get().MinimumAudioKbps, Is.EqualTo(0));
        }
    }
}
