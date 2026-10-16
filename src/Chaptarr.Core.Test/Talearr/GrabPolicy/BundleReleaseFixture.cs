using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    [TestFixture]
    public class BundleReleaseFixture
    {
        // Real release names that came up for single-book searches on the TaleArr test server.
        [TestCase("1984 - The Complete Novels - George Orwell", "1984")]
        [TestCase("The Poppy War Trilogy by R.F", "The Poppy War")]
        [TestCase("R.F. Kuang 5 Books Collection Set: The Poppy War, The Dragon Republic, The Burning God, Babel & Yellowface", "The Poppy War")]
        [TestCase("John Grisham - Three Classic Thrillers (The Firm; The Appeal; The Chamber) (v5.0) (epub)", "The Firm")]
        [TestCase("The Poppy War Series by R.F. Kuang 2 Books Collector's Edition Set", "The Poppy War")]
        [TestCase("R.F. Kuang - The Poppy War Box Set (Books 1-3)", "The Poppy War")]
        [TestCase("Orwell - Animal Farm and 1984 Collection [epub]", "Animal Farm")]
        [TestCase("Agatha Christie - 3-in-1 Poirot Omnibus", "Murder on the Orient Express")]
        public void should_detect_bundles(string title, string wantedTitle)
        {
            var result = ReleasePackDetector.Detect(title, new Book { Id = 1, Title = wantedTitle }, new List<Book>());

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks), $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_detect_a_list_of_catalog_titles()
        {
            var catalog = IsaacsonCatalog();
            var target = catalog.Single(book => book.Title == "Steve Jobs");

            var result = ReleasePackDetector.Detect(
                "Walter Isaacson - Great Innovators (Benjamin Franklin; Einstein; Steve Jobs) (epub)",
                target,
                catalog);

            Assert.That(result.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(result.PackType, Is.EqualTo("catalog-title-list"));
        }

        [TestCase("R F Kuang - The Poppy War (2024 ed) (retail) (epub)", "The Poppy War")]
        [TestCase("John Grisham The Firm 2010 Retail EPUB eBook-BitBook", "The Firm")]
        [TestCase("The.Exchange-After.The.Firm.by.John.Grisham.2023.epub", "The Firm")]
        [TestCase("Walter Isaacson - Steve Jobs (Biography, AAC", "Steve Jobs")]
        [TestCase("Nineteen Eighty-Four (1984) by George Orwell Read by Samuel West ...", "1984")]
        [TestCase("Animal Farm - George Orwell   (Unabridged)", "Animal Farm")]
        [TestCase("Ali.Hazelwood.-.Bride.Series.-.02.-.Mate.[AUDIOBOOK]-xpost", "Mate")]
        [TestCase("Michael Parenti - 1997 - Blackshirts and Reds - Rational Fascism ...", "Blackshirts and Reds")]
        [TestCase("Navessa.Allen-Caught.Up.Into.Darkness.2.2025.RETAIL.EPUB", "Caught Up")]
        public void should_not_flag_single_books(string title, string wantedTitle)
        {
            var catalog = IsaacsonCatalog();
            var result = ReleasePackDetector.Detect(title, new Book { Id = 99, Title = wantedTitle }, catalog);

            Assert.That(result.Verdict, Is.Not.EqualTo(ReleasePackDetectionVerdict.MultipleBooks), $"Type={result.PackType}; Match={result.MatchedValue}");
        }

        [Test]
        public void should_not_flag_a_collection_when_the_wanted_book_is_a_collection()
        {
            var result = ReleasePackDetector.Detect(
                "Alice Munro - The Collected Stories (epub)",
                new Book { Id = 1, Title = "Collected Stories" },
                new List<Book>());

            Assert.That(result.Verdict, Is.Not.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
        }

        [Test]
        public void spec_should_reject_bundles_by_default()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());

            var decision = spec.IsSatisfiedBy(Bundle("The Poppy War"), new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
        }

        [Test]
        public void spec_should_accept_bundles_when_opted_in()
        {
            var policy = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AllowBundles = true } };
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), policy);

            Assert.That(spec.IsSatisfiedBy(Bundle("The Poppy War"), new BookSearchCriteria()).Accepted, Is.True);
        }

        [Test]
        public void spec_should_accept_a_bundle_release_when_the_wanted_book_is_an_omnibus()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());

            var decision = spec.IsSatisfiedBy(
                Bundle("The Complete Poppy War Trilogy: The Poppy War, The Dragon Republic, The Burning God"),
                new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
        }

        [Test]
        public void discography_spec_should_follow_the_bundle_setting()
        {
            var remoteBook = Bundle("The Poppy War");
            remoteBook.ParsedBookInfo = new ParsedBookInfo { Discography = true };

            var blocked = new DiscographySpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var allowed = new DiscographySpecification(LogManager.GetCurrentClassLogger(),
                new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AllowBundles = true } });

            Assert.That(blocked.IsSatisfiedBy(remoteBook, null).Accepted, Is.False);
            Assert.That(allowed.IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
        }

        private static RemoteBook Bundle(string wantedTitle)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = "The Poppy War Trilogy by R.F" },
                Books = new List<Book> { new Book { Id = 4050, Title = wantedTitle, MediaType = BookMediaType.Audiobook } },
                PackDetection = new ReleasePackDetection
                {
                    Verdict = ReleasePackDetectionVerdict.MultipleBooks,
                    PackType = "named-pack",
                    MatchedValue = "Trilogy"
                }
            };
        }

        private static List<Book> IsaacsonCatalog()
        {
            return new List<Book>
            {
                new Book { Id = 2749, Title = "Steve Jobs" },
                new Book { Id = 2750, Title = "Benjamin Franklin: An American Life" },
                new Book { Id = 2751, Title = "Einstein: His Life and Universe" },
                new Book { Id = 2752, Title = "Leonardo da Vinci" },
                new Book { Id = 2753, Title = "The Innovators" }
            };
        }
    }
}
