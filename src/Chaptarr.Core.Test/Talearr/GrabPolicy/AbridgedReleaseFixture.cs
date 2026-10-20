using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    [TestFixture]
    public class AbridgedReleaseFixture
    {
        [TestCase("Steve Jobs (Abridged) - Walter Isaacson [MP3]")]
        [TestCase("Walter.Isaacson.Steve.Jobs.ABRIDGED.2011.MP3")]
        [TestCase("The Firm [Abridged Edition] John Grisham")]
        [TestCase("Abridged - The Pelican Brief")]
        [TestCase("John Grisham - The Firm_abridged_64kbps")]
        public void should_detect_abridged_release_titles(string title)
        {
            Assert.That(AbridgedReleaseDetector.Detect(title), Is.EqualTo(AbridgedMarker.Abridged));
        }

        // Real release names from the Talearr test server history.
        [TestCase("Animal Farm - George Orwell   (Unabridged)")]
        [TestCase("Walter Isaacson - Steve Jobs UNABRIDGED AAC")]
        [TestCase("The Firm (Un-abridged) John Grisham")]
        [TestCase("The.Firm.Un.Abridged.M4B")]
        public void should_detect_unabridged_release_titles(string title)
        {
            Assert.That(AbridgedReleaseDetector.Detect(title), Is.EqualTo(AbridgedMarker.Unabridged));
        }

        [TestCase("Nineteen Eighty-Four (1984) by George Orwell Read by Samuel West ...")]
        [TestCase("Walter Isaacson - Steve Jobs (Biography, AAC")]
        [TestCase("Truman Capote - In Cold Blood - epub [TKRG]")]
        [TestCase("")]
        [TestCase(null)]
        public void should_not_mark_titles_without_a_marker(string title)
        {
            Assert.That(AbridgedReleaseDetector.Detect(title), Is.EqualTo(AbridgedMarker.Unknown));
        }

        [Test]
        public void should_treat_monitored_edition_titled_abridged_as_wanting_abridged()
        {
            var book = AudiobookWithEditions(new Edition { Title = "Steve Jobs (Abridged)", Monitored = true });

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(book), Is.True);
        }

        [Test]
        public void should_use_runtime_to_spot_an_abridged_monitored_edition()
        {
            // Steve Jobs editions on the live library: 25.3h unabridged, 9.3h abridged.
            var book = AudiobookWithEditions(
                new Edition { Title = "Steve Jobs", DurationSeconds = 91080, Language = "eng" },
                new Edition { Title = "Steve Jobs", DurationSeconds = 33480, Language = "eng", Monitored = true });

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(book), Is.True);
        }

        [Test]
        public void should_not_flag_the_full_length_monitored_edition()
        {
            var book = AudiobookWithEditions(
                new Edition { Title = "Steve Jobs", DurationSeconds = 91080, Language = "eng", Monitored = true },
                new Edition { Title = "Steve Jobs", DurationSeconds = 33480, Language = "eng" },
                new Edition { Title = "Steve Jobs", DurationSeconds = 93660, Language = "eng" });

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(book), Is.False);
        }

        [Test]
        public void should_not_compare_runtimes_across_languages()
        {
            var book = AudiobookWithEditions(
                new Edition { Title = "Steve Jobs. La biografia", DurationSeconds = 97320, Language = "spa" },
                new Edition { Title = "Steve Jobs", DurationSeconds = 50000, Language = "eng", Monitored = true });

            Assert.That(AbridgedReleaseDetector.IsAbridgedBook(book), Is.False);
        }

        [Test]
        public void spec_should_reject_abridged_audiobook_by_default()
        {
            var spec = Spec(AbridgedAudiobookPolicy.UnabridgedOnly);
            var decision = spec.IsSatisfiedBy(AudiobookRelease("Steve Jobs (Abridged) [MP3]"), new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo(AbridgedReleaseSpecification.Category));
            Assert.That(decision.CanBypass, Is.True, "interactive search can still grab it on purpose");
        }

        [Test]
        public void spec_should_reject_abridged_by_default_without_a_policy_service()
        {
            var spec = new AbridgedReleaseSpecification(LogManager.GetCurrentClassLogger());

            Assert.That(spec.IsSatisfiedBy(AudiobookRelease("Steve Jobs (Abridged) [MP3]"), null).Accepted, Is.False);
        }

        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase(AbridgedAudiobookPolicy.Any)]
        public void spec_should_accept_abridged_when_the_user_opted_in(AbridgedAudiobookPolicy policy)
        {
            var spec = Spec(policy);

            Assert.That(spec.IsSatisfiedBy(AudiobookRelease("Steve Jobs (Abridged) [MP3]"), null).Accepted, Is.True);
        }

        [Test]
        public void spec_should_accept_unabridged_and_unmarked_releases()
        {
            var spec = Spec(AbridgedAudiobookPolicy.UnabridgedOnly);

            Assert.That(spec.IsSatisfiedBy(AudiobookRelease("Animal Farm - George Orwell   (Unabridged)"), null).Accepted, Is.True);
            Assert.That(spec.IsSatisfiedBy(AudiobookRelease("Walter Isaacson - Steve Jobs (Biography, AAC"), null).Accepted, Is.True);
        }

        [Test]
        public void spec_should_accept_abridged_when_the_wanted_edition_is_abridged()
        {
            var spec = Spec(AbridgedAudiobookPolicy.UnabridgedOnly);
            var remoteBook = AudiobookRelease("Steve Jobs (Abridged) [MP3]");
            remoteBook.Books[0].Editions = new List<Edition>
            {
                new Edition { Title = "Steve Jobs", DurationSeconds = 91080 },
                new Edition { Title = "Steve Jobs", DurationSeconds = 33480, Monitored = true }
            };

            Assert.That(spec.IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
        }

        [Test]
        public void spec_should_ignore_ebooks()
        {
            var spec = Spec(AbridgedAudiobookPolicy.UnabridgedOnly);
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Reader's Digest Abridged - The Firm (epub)" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) },
                Books = new List<Book> { new Book { Title = "The Firm", MediaType = BookMediaType.Ebook } }
            };

            Assert.That(spec.IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
        }

        [Test]
        public void comparer_should_rank_abridged_below_unabridged_when_preferring_unabridged()
        {
            var comparer = Comparer(AbridgedAudiobookPolicy.PreferUnabridged);
            var full = Decision("Steve Jobs - Walter Isaacson [M4B]");
            var abridged = Decision("Steve Jobs (Abridged) - Walter Isaacson [M4B]");

            Assert.That(comparer.Compare(full, abridged), Is.GreaterThan(0));
            Assert.That(comparer.Compare(abridged, full), Is.LessThan(0));
            Assert.That(comparer.CompareWithReasons(full, abridged).Reasons[0].Reason, Does.Contain("Unabridged"));
        }

        [Test]
        public void comparer_should_not_rank_on_abridged_when_policy_is_any()
        {
            var comparer = Comparer(AbridgedAudiobookPolicy.Any);
            var full = Decision("Steve Jobs - Walter Isaacson [M4B]");
            var abridged = Decision("Steve Jobs (Abridged) - Walter Isaacson [M4B]");

            Assert.That(comparer.Compare(full, abridged), Is.EqualTo(0));
        }

        private static AbridgedReleaseSpecification Spec(AbridgedAudiobookPolicy policy)
        {
            var service = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AbridgedPolicy = policy } };
            return new AbridgedReleaseSpecification(LogManager.GetCurrentClassLogger(), service);
        }

        private static DownloadDecisionComparer Comparer(AbridgedAudiobookPolicy policy)
        {
            return new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), DelayProfileServiceProxy.Create(), null, LogManager.GetCurrentClassLogger(),
                new TalearrGrabPolicy { AbridgedPolicy = policy });
        }

        private static DownloadDecision Decision(string title)
        {
            var profile = new QualityProfile
            {
                Id = 2,
                Name = "Audiobook",
                ProfileType = ProfileType.Audiobook,
                Items = new List<QualityProfileQualityItem>
                {
                    new QualityProfileQualityItem { Quality = Quality.M4B, Allowed = true }
                }
            };

            var author = new Author
            {
                Id = 1,
                Name = "Walter Isaacson",
                AudiobookQualityProfileId = profile.Id,
                AudiobookQualityProfile = profile
            };

            var remoteBook = AudiobookRelease(title);
            remoteBook.Author = author;
            remoteBook.Books[0].Author = author;
            return new DownloadDecision(remoteBook);
        }

        private static RemoteBook AudiobookRelease(string title)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title, Size = 500_000_000 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                Books = new List<Book>
                {
                    new Book { Id = 2813, Title = "Steve Jobs", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() }
                }
            };
        }

        private static Book AudiobookWithEditions(params Edition[] editions)
        {
            return new Book { Title = "Steve Jobs", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>(editions) };
        }
    }
}
