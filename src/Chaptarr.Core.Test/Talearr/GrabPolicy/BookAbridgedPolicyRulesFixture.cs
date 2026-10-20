using System;
using System.Collections.Generic;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    // A per-book override store without talearr.db: overrides by work key (BaseBookId).
    internal sealed class FakeBookGrabPolicyService : ITalearrBookGrabPolicyService
    {
        public Dictionary<string, AbridgedAudiobookPolicy> Overrides { get; } = new Dictionary<string, AbridgedAudiobookPolicy>(StringComparer.OrdinalIgnoreCase);

        public AbridgedAudiobookPolicy? GetOverride(Book row)
        {
            if (row == null || row.MediaType != BookMediaType.Audiobook || TalearrRecordingGroupService.IsCopySlug(row.TitleSlug))
            {
                return null;
            }

            return Overrides.TryGetValue(TalearrBookGrabPolicyService.WorkKey(row), out var policy) ? policy : null;
        }

        public BookGrabPolicyResult Get(int bookId)
        {
            throw new NotImplementedException();
        }

        public BookGrabPolicyResult Set(int bookId, AbridgedAudiobookPolicy? policy)
        {
            throw new NotImplementedException();
        }
    }

    // Talearr: the per-book abridged policy in the decision engine. Every policy value is checked
    // as a per-book override and as the global policy (the fallback when a book has none).
    [TestFixture]
    public class BookAbridgedPolicyRulesFixture
    {
        private const string AbridgedTitle = "Steve Jobs (Abridged) - Walter Isaacson [MP3]";
        private const string UnabridgedTitle = "Walter Isaacson - Steve Jobs UNABRIDGED [M4B]";
        private const string UnmarkedTitle = "Walter Isaacson - Steve Jobs [M4B]";
        private const string WorkKey = "hc:2813";

        public enum Release
        {
            Abridged,
            Unabridged,
            Unmarked
        }

        private FakeBookGrabPolicyService _books;

        [SetUp]
        public void SetUp()
        {
            _books = new FakeBookGrabPolicyService();
        }

        // Per-book override, the global policy set to the opposite so the override must win.
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Abridged, false)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Unmarked, true)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Unabridged, false)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Unmarked, false)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Unmarked, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Unmarked, true)]
        public void per_book_override_decides(AbridgedAudiobookPolicy bookPolicy, Release release, bool accepted)
        {
            _books.Overrides[WorkKey] = bookPolicy;
            var global = bookPolicy == AbridgedAudiobookPolicy.AbridgedOnly ? AbridgedAudiobookPolicy.UnabridgedOnly : AbridgedAudiobookPolicy.AbridgedOnly;

            var decision = Spec(global).IsSatisfiedBy(AudiobookRelease(release), null);

            Assert.That(decision.Accepted, Is.EqualTo(accepted));
            if (!accepted)
            {
                Assert.That(decision.Category, Is.EqualTo(AbridgedReleaseSpecification.Category));
                Assert.That(decision.CanBypass, Is.True, "a soft filter, still grabbable from interactive search");
                Assert.That(decision.Reason, Does.Contain("for this book"));
            }
        }

        // No override: the global policy decides, exactly as before (plus abridgedOnly).
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Abridged, false)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, Release.Unmarked, true)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Unabridged, false)]
        [TestCase(AbridgedAudiobookPolicy.AbridgedOnly, Release.Unmarked, false)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged, Release.Unmarked, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Abridged, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Unabridged, true)]
        [TestCase(AbridgedAudiobookPolicy.Any, Release.Unmarked, true)]
        public void global_policy_decides_without_an_override(AbridgedAudiobookPolicy global, Release release, bool accepted)
        {
            _books.Overrides["hc:other-book"] = AbridgedAudiobookPolicy.Any;

            var decision = Spec(global).IsSatisfiedBy(AudiobookRelease(release), null);

            Assert.That(decision.Accepted, Is.EqualTo(accepted));
            if (!accepted)
            {
                Assert.That(decision.CanBypass, Is.True);
                Assert.That(decision.Reason, Does.Not.Contain("for this book"));
            }
        }

        [Test]
        public void global_policy_decides_without_a_book_policy_service()
        {
            var spec = new AbridgedReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AbridgedPolicy = AbridgedAudiobookPolicy.AbridgedOnly } });

            Assert.That(spec.IsSatisfiedBy(AudiobookRelease(Release.Abridged), null).Accepted, Is.True);
            Assert.That(spec.IsSatisfiedBy(AudiobookRelease(Release.Unabridged), null).Accepted, Is.False);
        }

        [Test]
        public void abridged_only_accepts_an_unmarked_release_when_the_monitored_edition_is_abridged()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var release = AudiobookRelease(Release.Unmarked, AbridgedEditions());

            Assert.That(Spec(AbridgedAudiobookPolicy.UnabridgedOnly).IsSatisfiedBy(release, null).Accepted, Is.True);
        }

        [Test]
        public void global_unabridged_only_keeps_the_wanted_edition_is_abridged_exception()
        {
            var release = AudiobookRelease(Release.Abridged, AbridgedEditions());

            Assert.That(Spec(AbridgedAudiobookPolicy.UnabridgedOnly).IsSatisfiedBy(release, null).Accepted, Is.True);
        }

        [Test]
        public void per_book_unabridged_only_is_strict_even_when_the_monitored_edition_is_abridged()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.UnabridgedOnly;
            var release = AudiobookRelease(Release.Abridged, AbridgedEditions());

            Assert.That(Spec(AbridgedAudiobookPolicy.UnabridgedOnly).IsSatisfiedBy(release, null).Accepted, Is.False);
        }

        [Test]
        public void ebooks_are_never_judged()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = "Steve Jobs - Walter Isaacson (epub)" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) },
                Books = new List<Book> { new Book { Id = 2814, BaseBookId = WorkKey, Title = "Steve Jobs", MediaType = BookMediaType.Ebook } }
            };

            Assert.That(Spec(AbridgedAudiobookPolicy.AbridgedOnly).IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
        }

        [Test]
        public void an_override_follows_the_work_not_the_row_id()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var release = AudiobookRelease(Release.Unabridged);
            release.Books[0].Id = 99001;

            Assert.That(Spec(AbridgedAudiobookPolicy.UnabridgedOnly).IsSatisfiedBy(release, null).Accepted, Is.False);
        }

        [Test]
        public void abridged_copy_keeps_searching_abridged_and_ignores_the_override()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.UnabridgedOnly;
            var release = AudiobookRelease(Release.Abridged, AbridgedEditions());
            release.Books[0].TitleSlug = "steve-jobs_wanted_2";

            Assert.That(Spec(AbridgedAudiobookPolicy.UnabridgedOnly).IsSatisfiedBy(release, null).Accepted, Is.True);
        }

        [Test]
        public void unabridged_copy_searches_unabridged_under_a_global_abridged_only()
        {
            var editions = new List<Edition> { new Edition { Id = 1, Title = "Steve Jobs (Unabridged)", Monitored = true } };

            var abridged = AudiobookRelease(Release.Abridged, editions);
            abridged.Books[0].TitleSlug = "steve-jobs_copy_2";
            var unmarked = AudiobookRelease(Release.Unmarked, editions);
            unmarked.Books[0].TitleSlug = "steve-jobs_copy_2";

            var spec = Spec(AbridgedAudiobookPolicy.AbridgedOnly, new FakeResolver(AbridgedMarker.Unabridged));
            Assert.That(spec.IsSatisfiedBy(abridged, null).Accepted, Is.False);
            Assert.That(spec.IsSatisfiedBy(unmarked, null).Accepted, Is.True);
        }

        [Test]
        public void copy_of_unknown_abridgement_takes_either_under_a_global_abridged_only()
        {
            var release = AudiobookRelease(Release.Unabridged);
            release.Books[0].TitleSlug = "steve-jobs_wanted_3";

            Assert.That(Spec(AbridgedAudiobookPolicy.AbridgedOnly, new FakeResolver(AbridgedMarker.Unknown)).IsSatisfiedBy(release, null).Accepted, Is.True);
        }

        [Test]
        public void resolver_answer_counts_for_the_wanted_edition()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var spec = Spec(AbridgedAudiobookPolicy.UnabridgedOnly, new FakeResolver(AbridgedMarker.Abridged));

            Assert.That(spec.IsSatisfiedBy(AudiobookRelease(Release.Unmarked), null).Accepted, Is.True, "Audible says the monitored edition is abridged");
        }

        [Test]
        public void comparer_ranks_abridged_first_for_an_abridged_only_book()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var comparer = Comparer(AbridgedAudiobookPolicy.UnabridgedOnly);
            var abridged = Decision(AbridgedTitle);
            var unmarked = Decision(UnmarkedTitle);
            var unabridged = Decision(UnabridgedTitle);

            Assert.That(comparer.Compare(abridged, unmarked), Is.GreaterThan(0));
            Assert.That(comparer.Compare(unmarked, unabridged), Is.GreaterThan(0));
            Assert.That(comparer.Compare(unabridged, abridged), Is.LessThan(0));
            Assert.That(comparer.CompareWithReasons(abridged, unmarked).Reasons[0].Reason, Does.Contain("Abridged audiobook preferred"));
        }

        [Test]
        public void comparer_ranks_abridged_first_under_a_global_abridged_only()
        {
            var comparer = Comparer(AbridgedAudiobookPolicy.AbridgedOnly);

            Assert.That(comparer.Compare(Decision(AbridgedTitle), Decision(UnmarkedTitle)), Is.GreaterThan(0));
        }

        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly)]
        [TestCase(AbridgedAudiobookPolicy.PreferUnabridged)]
        public void comparer_ranks_abridged_last_for_a_book_that_prefers_unabridged(AbridgedAudiobookPolicy bookPolicy)
        {
            _books.Overrides[WorkKey] = bookPolicy;
            var comparer = Comparer(AbridgedAudiobookPolicy.AbridgedOnly);

            Assert.That(comparer.Compare(Decision(UnmarkedTitle), Decision(AbridgedTitle)), Is.GreaterThan(0));
            Assert.That(comparer.CompareWithReasons(Decision(UnmarkedTitle), Decision(AbridgedTitle)).Reasons[0].Reason, Does.Contain("Unabridged"));
        }

        [Test]
        public void comparer_ignores_abridgement_for_a_book_set_to_any()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.Any;
            var comparer = Comparer(AbridgedAudiobookPolicy.AbridgedOnly);

            Assert.That(comparer.Compare(Decision(UnabridgedTitle), Decision(AbridgedTitle)), Is.EqualTo(0));
        }

        [Test]
        public void comparer_keeps_the_strict_book_choice_over_an_abridged_monitored_edition()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.UnabridgedOnly;
            var comparer = Comparer(AbridgedAudiobookPolicy.UnabridgedOnly);
            var abridged = Decision(AbridgedTitle, AbridgedEditions());
            var unmarked = Decision(UnmarkedTitle, AbridgedEditions());

            Assert.That(comparer.Compare(unmarked, abridged), Is.GreaterThan(0));
        }

        [Test]
        public void comparer_keeps_the_global_wanted_edition_flip()
        {
            var comparer = Comparer(AbridgedAudiobookPolicy.PreferUnabridged);
            var abridged = Decision(AbridgedTitle, AbridgedEditions());
            var unmarked = Decision(UnmarkedTitle, AbridgedEditions());

            Assert.That(comparer.Compare(abridged, unmarked), Is.GreaterThan(0));
        }

        [Test]
        public void prioritization_service_passes_the_book_policies_to_the_comparer()
        {
            _books.Overrides[WorkKey] = AbridgedAudiobookPolicy.AbridgedOnly;
            var service = new DownloadDecisionPriorizationService(ConfigServiceTestProxy.Create(), DelayProfileServiceProxy.Create(), null, LogManager.GetCurrentClassLogger(),
                new FakeGrabPolicyService(), null, _books);

            var ranked = service.PrioritizeDecisions(new List<DownloadDecision> { Decision(UnmarkedTitle), Decision(AbridgedTitle) });

            Assert.That(ranked[0].RemoteBook.Release.Title, Is.EqualTo(AbridgedTitle));
        }

        private AbridgedReleaseSpecification Spec(AbridgedAudiobookPolicy global, ITalearrAbridgedResolver resolver = null)
        {
            var service = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AbridgedPolicy = global } };
            return new AbridgedReleaseSpecification(LogManager.GetCurrentClassLogger(), service, resolver, _books);
        }

        private DownloadDecisionComparer Comparer(AbridgedAudiobookPolicy global)
        {
            return new DownloadDecisionComparer(ConfigServiceTestProxy.Create(), DelayProfileServiceProxy.Create(), null, LogManager.GetCurrentClassLogger(),
                new TalearrGrabPolicy { AbridgedPolicy = global }, null, _books);
        }

        private static List<Edition> AbridgedEditions()
        {
            // Steve Jobs on the live library: 25.3h unabridged, 9.3h abridged (monitored).
            return new List<Edition>
            {
                new Edition { Id = 1, Title = "Steve Jobs", DurationSeconds = 91080, Language = "eng" },
                new Edition { Id = 2, Title = "Steve Jobs", DurationSeconds = 33480, Language = "eng", Monitored = true }
            };
        }

        private static RemoteBook AudiobookRelease(Release release, List<Edition> editions = null)
        {
            var title = release switch
            {
                Release.Abridged => AbridgedTitle,
                Release.Unabridged => UnabridgedTitle,
                _ => UnmarkedTitle
            };

            return AudiobookRelease(title, editions);
        }

        private static RemoteBook AudiobookRelease(string title, List<Edition> editions = null)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title, Size = 500_000_000 },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                Books = new List<Book>
                {
                    new Book { Id = 2813, BaseBookId = WorkKey, TitleSlug = "steve-jobs", Title = "Steve Jobs", MediaType = BookMediaType.Audiobook, Editions = editions ?? new List<Edition>() }
                }
            };
        }

        private static DownloadDecision Decision(string title, List<Edition> editions = null)
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

            var remoteBook = AudiobookRelease(title, editions);
            remoteBook.Author = author;
            remoteBook.Books[0].Author = author;
            return new DownloadDecision(remoteBook);
        }

        private sealed class FakeResolver : ITalearrAbridgedResolver
        {
            private readonly AbridgedMarker _marker;

            public FakeResolver(AbridgedMarker marker)
            {
                _marker = marker;
            }

            public AbridgedResolution Resolve(Book book)
            {
                return new AbridgedResolution(_marker, AbridgedSources.Audible, "test");
            }

            public AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<NzbDrone.Core.MediaFiles.BookFile> files = null)
            {
                return Resolve(null);
            }
        }
    }
}
