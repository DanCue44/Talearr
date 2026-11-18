using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    // Talearr: an unmarked release too big to be an abridged reading, and "prefer abridged".
    [TestFixture]
    public class AbridgedSizeAndPreferAbridgedFixture
    {
        // The Client: the unabridged reading runs 860 min (14 h 20 min), so 56 kbps is 361 MB.
        private sealed class Resolver : ITalearrAbridgedResolver
        {
            public AbridgedMarker Wanted { get; set; } = AbridgedMarker.Abridged;
            public int? Minutes { get; set; } = 860;

            public AbridgedResolution Resolve(Book book) => new AbridgedResolution(Wanted, AbridgedSources.Metadata, "test");

            public AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<BookFile> files = null) =>
                new AbridgedResolution(Wanted, AbridgedSources.Metadata, "test");

            public int? UnabridgedRuntimeMinutes(Book book) => Minutes;
        }

        private static RemoteBook Release(string title, long size)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title, Size = size },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                Books = new List<Book>
                {
                    new Book { Id = 3363, BaseBookId = "hc:385403", TitleSlug = "the-client_2", Title = "The Client", MediaType = BookMediaType.Audiobook }
                }
            };
        }

        private static AbridgedJudgement Judge(AbridgedAudiobookPolicy policy, string source, RemoteBook release, Resolver resolver)
        {
            return TalearrAbridgedRules.Judge(release, new AbridgedTarget(policy, source), resolver);
        }

        [Test]
        public void an_unmarked_release_the_size_of_the_unabridged_reading_is_not_the_abridged_one()
        {
            var judgement = Judge(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedPolicySources.Book, Release("The Client", 421_925_927), new Resolver());

            Assert.That(judgement.Accepted, Is.False);
            Assert.That(judgement.Reason, Is.EqualTo("Looks like the unabridged recording (421 MB for a book whose unabridged reading runs 14 h 20 min) for this book"));
        }

        [Test]
        public void a_pinned_abridged_row_under_the_global_policy_also_rejects_it()
        {
            // Tonight's case: the row wanted the abridged edition, the global policy is unabridged only.
            var judgement = Judge(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedPolicySources.Global, Release("The Client", 421_925_927), new Resolver());

            Assert.That(judgement.Accepted, Is.False);
        }

        [Test]
        public void a_small_unmarked_release_is_still_taken()
        {
            var judgement = Judge(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedPolicySources.Book, Release("The Client", 150_000_000), new Resolver());

            Assert.That(judgement.Accepted, Is.True);
        }

        [Test]
        public void without_a_known_runtime_nothing_changes()
        {
            var judgement = Judge(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedPolicySources.Book, Release("The Client", 421_925_927), new Resolver { Minutes = null });

            Assert.That(judgement.Accepted, Is.True, "the monitored edition is abridged, so an unmarked release is accepted as before");
        }

        [Test]
        public void a_release_marked_abridged_is_never_judged_by_size()
        {
            var judgement = Judge(AbridgedAudiobookPolicy.AbridgedOnly, AbridgedPolicySources.Book, Release("The Client (Abridged)", 900_000_000), new Resolver());

            Assert.That(judgement.Accepted, Is.True);
        }

        [TestCase(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedPolicySources.Book)]
        [TestCase(AbridgedAudiobookPolicy.PreferAbridged, AbridgedPolicySources.Book)]
        [TestCase(AbridgedAudiobookPolicy.Any, AbridgedPolicySources.Global)]
        public void rows_that_take_the_unabridged_reading_are_not_judged_by_size(AbridgedAudiobookPolicy policy, string source)
        {
            var judgement = Judge(policy, source, Release("The Client", 421_925_927), new Resolver());

            Assert.That(judgement.Accepted, Is.True);
        }

        [Test]
        public void a_row_that_wants_the_unabridged_reading_takes_a_big_release()
        {
            var judgement = Judge(AbridgedAudiobookPolicy.UnabridgedOnly, AbridgedPolicySources.Global, Release("The Client", 421_925_927), new Resolver { Wanted = AbridgedMarker.Unabridged });

            Assert.That(judgement.Accepted, Is.True);
        }

        [TestCase("The Client (Abridged)", true)]
        [TestCase("The Client UNABRIDGED", true)]
        [TestCase("The Client", true)]
        public void prefer_abridged_accepts_every_reading(string title, bool accepted)
        {
            var judgement = Judge(AbridgedAudiobookPolicy.PreferAbridged, AbridgedPolicySources.Book, Release(title, 100_000_000), new Resolver());

            Assert.That(judgement.Accepted, Is.EqualTo(accepted));
        }

        [Test]
        public void prefer_abridged_ranks_the_abridged_reading_first()
        {
            var target = new AbridgedTarget(AbridgedAudiobookPolicy.PreferAbridged, AbridgedPolicySources.Book);
            var resolver = new Resolver();

            Assert.Multiple(() =>
            {
                Assert.That(TalearrAbridgedRules.Rank(Release("The Client (Abridged)", 1), target, resolver), Is.EqualTo(1));
                Assert.That(TalearrAbridgedRules.Rank(Release("The Client", 1), target, resolver), Is.EqualTo(0));
                Assert.That(TalearrAbridgedRules.Rank(Release("The Client UNABRIDGED", 1), target, resolver), Is.EqualTo(-1));
                Assert.That(TalearrAbridgedRules.Describe(AbridgedAudiobookPolicy.PreferAbridged), Is.EqualTo("prefer abridged"));
            });
        }

        [TestCase("preferAbridged", AbridgedAudiobookPolicy.PreferAbridged)]
        [TestCase("preferabridged", AbridgedAudiobookPolicy.PreferAbridged)]
        [TestCase("preferUnabridged", AbridgedAudiobookPolicy.PreferUnabridged)]
        [TestCase("abridgedOnly", AbridgedAudiobookPolicy.AbridgedOnly)]
        [TestCase("unabridgedOnly", AbridgedAudiobookPolicy.UnabridgedOnly)]
        public void the_api_spelling_parses(string value, AbridgedAudiobookPolicy expected)
        {
            Assert.That(TalearrBookGrabPolicyService.TryParseApiValue(value, out var policy), Is.True);
            Assert.That(policy, Is.EqualTo(expected));
            Assert.That(TalearrBookGrabPolicyService.ToApiValue(expected), Is.EqualTo(char.ToLowerInvariant(expected.ToString()[0]) + expected.ToString().Substring(1)));
        }

        [Test]
        public void prefer_abridged_is_stored_after_the_existing_values()
        {
            Assert.That((int)AbridgedAudiobookPolicy.PreferAbridged, Is.EqualTo(4));
            Assert.That(TalearrBookGrabPolicyStore.Format(AbridgedAudiobookPolicy.PreferAbridged), Is.EqualTo("preferabridged"));
        }
    }
}
