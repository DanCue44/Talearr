using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrAbridgedResolverFixture
    {
        private TalearrTestFolder _folder;
        private TalearrRecordingsStore _store;
        private Dictionary<int, List<BookFile>> _files;
        private TalearrAbridgedResolver _resolver;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = _folder.CreateStore();
            _files = new Dictionary<int, List<BookFile>>();

            var (repository, _) = ConfigRepositoryProxy.Create();
            var settings = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());
            var facts = new TalearrAudibleFactsService(null, _store, settings, LogManager.GetCurrentClassLogger());

            var (mediaFiles, fake) = InterfaceFake<IMediaFileService>.Create();
            fake.On(nameof(IMediaFileService.GetFilesByBook), args => _files.TryGetValue((int)args[0], out var list) ? list : new List<BookFile>());

            _resolver = new TalearrAbridgedResolver(facts, mediaFiles, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private void Link(Edition edition, string asin, string formatType, int? minutes, string narrator = "Scott Brick")
        {
            _store.UpsertProducts(new[]
            {
                new AudibleProduct
                {
                    Marketplace = "us",
                    Asin = asin,
                    Status = AudibleProductStatus.Ok,
                    Title = "The Firm",
                    FormatType = formatType,
                    RuntimeMinutes = minutes,
                    Narrators = new List<string> { narrator },
                    FetchedAt = DateTime.UtcNow
                }
            });
            _store.UpsertLink(new EditionAudibleLink { ForeignEditionId = TalearrEditionKey.For(edition), Marketplace = "us", Asin = asin, Method = EditionAudibleLink.MethodSearch, Confidence = 0.9, CheckedAt = DateTime.UtcNow });
        }

        private static Book Row(int id, params Edition[] editions)
        {
            return new Book { Id = id, Title = "The Firm", MediaType = BookMediaType.Audiobook, Editions = editions.ToList() };
        }

        // Base row: Scott Brick unabridged monitored. Abridged copy row: same editions cloned, the
        // D.W. Moffett reading monitored (what AddWantedEdition creates).
        private (Book Unabridged, Book AbridgedCopy) FirmRows()
        {
            var baseUs = FirmEditions.ScottBrickUs();
            var baseMoffett = FirmEditions.MoffettAbridged();
            var unabridged = Row(3401, baseUs, baseMoffett);

            var copyUs = FirmEditions.ScottBrickUs();
            copyUs.Id = 90001;
            copyUs.Monitored = false;
            var copyMoffett = FirmEditions.MoffettAbridged();
            copyMoffett.Id = 90002;
            copyMoffett.Monitored = true;
            var copy = Row(9000, copyUs, copyMoffett);
            copy.TitleSlug = "the-firm_2_wanted_28705";

            return (unabridged, copy);
        }

        [Test]
        public void audible_format_type_wins()
        {
            var (unabridged, copy) = FirmRows();
            Link(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030);
            Link(FirmEditions.MoffettAbridged(), "B000TEST01", "abridged", 360, "D.W. Moffett");

            var baseResult = _resolver.Resolve(unabridged);
            var copyResult = _resolver.Resolve(copy);

            Assert.That(baseResult.Marker, Is.EqualTo(AbridgedMarker.Unabridged));
            Assert.That(baseResult.Source, Is.EqualTo(AbridgedSources.Audible));
            Assert.That(copyResult.Marker, Is.EqualTo(AbridgedMarker.Abridged));
            Assert.That(copyResult.Source, Is.EqualTo(AbridgedSources.Audible));
            Assert.That(copyResult.Reason, Does.Contain("B000TEST01"));
        }

        [Test]
        public void explicit_edition_text_comes_second()
        {
            var titled = FirmEditions.TitledAbridged();
            titled.Monitored = true;

            var other = FirmEditions.ScottBrickUs();
            other.Monitored = false;

            var result = _resolver.Resolve(Row(1, titled, other));

            Assert.That(result.Marker, Is.EqualTo(AbridgedMarker.Abridged));
            Assert.That(result.Source, Is.EqualTo(AbridgedSources.Metadata));
        }

        [Test]
        public void audible_runtimes_fill_the_gaps_for_the_runtime_rule()
        {
            // Neither edition has a runtime upstream; Audible knows both, but not the format of
            // the short one.
            var full = FirmEditions.ScottBrickUs();
            full.DurationSeconds = 0;
            full.Monitored = false;
            var shortOne = FirmEditions.MoffettAbridged();
            shortOne.Monitored = true;
            Link(full, "B002V5BSLC", "unabridged", 1030);
            Link(shortOne, "B000TEST02", null, 400, "D.W. Moffett");

            var result = _resolver.Resolve(Row(2, full, shortOne));

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(Row(2, full, shortOne)), Is.False, "metadata alone cannot tell");
            Assert.That(result.Marker, Is.EqualTo(AbridgedMarker.Abridged));
            Assert.That(result.Source, Is.EqualTo(AbridgedSources.Runtime));
        }

        [Test]
        public void a_full_length_edition_next_to_a_known_unabridged_one_is_unabridged()
        {
            var known = FirmEditions.ScottBrickUs();
            known.Monitored = false;
            var other = FirmEditions.ScottBrickUk();
            other.Monitored = true;
            Link(known, "B002V5BSLC", "unabridged", 1030);

            var result = _resolver.Resolve(Row(3, known, other));

            Assert.That(result.Marker, Is.EqualTo(AbridgedMarker.Unabridged));
            Assert.That(result.Source, Is.EqualTo(AbridgedSources.Runtime));
        }

        [Test]
        public void files_are_measured_against_the_unabridged_runtime()
        {
            var known = FirmEditions.ScottBrickUs();
            known.Monitored = false;
            var unknown = FirmEditions.MoffettAbridged();
            unknown.Monitored = true;
            Link(known, "B002V5BSLC", "unabridged", 1030);
            _files[4] = new List<BookFile> { new BookFile { DurationSeconds = 3 * 3600 }, new BookFile { DurationSeconds = 3 * 3600 } };

            var result = _resolver.Resolve(Row(4, known, unknown));

            Assert.That(result.Marker, Is.EqualTo(AbridgedMarker.Abridged));
            Assert.That(result.Source, Is.EqualTo(AbridgedSources.Files));
        }

        [Test]
        public void unknown_says_why()
        {
            var result = _resolver.Resolve(Row(5, FirmEditions.MoffettAbridged()));

            Assert.That(result.Marker, Is.EqualTo(AbridgedMarker.Unknown));
            Assert.That(result.Source, Is.EqualTo(AbridgedSources.None));
            Assert.That(result.Reason, Does.Contain("no Audible match"));
        }

        [Test]
        public void ebooks_are_never_judged()
        {
            Assert.That(_resolver.Resolve(new Book { Title = "The Firm (Abridged)", MediaType = BookMediaType.Ebook }).Marker, Is.EqualTo(AbridgedMarker.Unknown));
        }

        [Test]
        public void wanted_abridged_copy_allows_abridged_releases()
        {
            var (_, copy) = FirmRows();
            Link(FirmEditions.MoffettAbridged(), "B000TEST01", "abridged", 360, "D.W. Moffett");
            var release = AudiobookRelease("John Grisham - The Firm (Abridged) read by D.W. Moffett [MP3]", copy);

            var withoutRecordings = Spec(null).IsSatisfiedBy(release, null);
            var withRecordings = Spec(_resolver).IsSatisfiedBy(release, null);

            Assert.That(withoutRecordings.Accepted, Is.False, "metadata alone cannot see that the copy is abridged");
            Assert.That(withRecordings.Accepted, Is.True);
        }

        [Test]
        public void unabridged_row_still_rejects_abridged_releases()
        {
            var (unabridged, _) = FirmRows();
            Link(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030);

            var decision = Spec(_resolver).IsSatisfiedBy(AudiobookRelease("The Firm (Abridged) [MP3]", unabridged), null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo(AbridgedReleaseSpecification.Category));
        }

        [Test]
        public void abridged_copy_ranks_abridged_releases_first()
        {
            var (_, copy) = FirmRows();
            Link(FirmEditions.MoffettAbridged(), "B000TEST01", "abridged", 360, "D.W. Moffett");
            var comparer = Comparer(AbridgedAudiobookPolicy.PreferUnabridged, _resolver);
            var abridged = Decision("John Grisham - The Firm (Abridged) [MP3]", copy);
            var full = Decision("John Grisham - The Firm (Unabridged) [MP3]", copy);

            Assert.That(comparer.Compare(abridged, full), Is.GreaterThan(0));
            Assert.That(new[] { full, abridged }.OrderByDescending(d => d, comparer).First(), Is.SameAs(abridged));
        }

        [Test]
        public void unabridged_copy_ranks_abridged_releases_last()
        {
            var (unabridged, _) = FirmRows();
            Link(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030);
            var comparer = Comparer(AbridgedAudiobookPolicy.PreferUnabridged, _resolver);
            var abridged = Decision("John Grisham - The Firm (Abridged) [MP3]", unabridged);
            var full = Decision("John Grisham - The Firm [MP3]", unabridged);

            Assert.That(new[] { abridged, full }.OrderByDescending(d => d, comparer).Last(), Is.SameAs(abridged));
        }

        [Test]
        public void a_failing_resolver_falls_back_to_metadata_rules()
        {
            var (resolver, fake) = InterfaceFake<ITalearrAbridgedResolver>.Create();
            fake.On(nameof(ITalearrAbridgedResolver.Resolve), _ => throw new InvalidOperationException("talearr.db gone"));
            var titled = FirmEditions.TitledAbridged();
            titled.Monitored = true;

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(Row(6, titled), resolver), Is.True);
        }

        private static AbridgedReleaseSpecification Spec(ITalearrAbridgedResolver resolver)
        {
            var policy = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AbridgedPolicy = AbridgedAudiobookPolicy.UnabridgedOnly } };
            return new AbridgedReleaseSpecification(LogManager.GetCurrentClassLogger(), policy, resolver);
        }

        private static DownloadDecisionComparer Comparer(AbridgedAudiobookPolicy policy, ITalearrAbridgedResolver resolver)
        {
            return new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), DelayProfileServiceProxy.Create(), null, LogManager.GetCurrentClassLogger(),
                new TalearrGrabPolicy { AbridgedPolicy = policy }, resolver);
        }

        private static RemoteBook AudiobookRelease(string title, Book book)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title, Size = 500_000_000 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) },
                Books = new List<Book> { book }
            };
        }

        private static DownloadDecision Decision(string title, Book book)
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem> { new QualityProfileQualityItem { Quality = Quality.MP3, Allowed = true } }
            };
            var author = new Author { Id = 1, Name = "John Grisham", AudiobookQualityProfileId = profile.Id, AudiobookQualityProfile = profile };

            var remoteBook = AudiobookRelease(title, book);
            remoteBook.Author = author;
            book.Author = author;
            return new DownloadDecision(remoteBook);
        }
    }
}
