using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Import;

namespace Chaptarr.Core.Test.Talearr.Matching
{
    // Talearr: wrong matches seen on the live instance during a Wanted > Search All Missing
    // (2026-10-17). Release names are copied verbatim from its history.
    [TestFixture]
    public class WrongMatchRegressionFixture
    {
        private const string MarketingBundle = "Boost Your Marketing Game with This All-in-One Bundle";
        private const string CraigRice = "Craig Rice - 45 Murderers- A Collection of True Crime Stories (retail) (epub)";
        private const string Twelve = "Jennifer Lynn Barnes - [The Naturals 04.5] - Twelve (retail) (epub)";
        private const string TwelveNzbGeek = "Jennifer Lynn Barnes - [The Naturals 4.5] - Twelve (retail) (epub)";

        private Author _barnes;
        private Author _becker;
        private Book _naturals;
        private Book _allIn;
        private Book _twelve;
        private Book _trueCrime;

        [SetUp]
        public void SetUp()
        {
            _barnes = new Author { Id = 20, Name = "Jennifer Lynn Barnes" };
            _becker = new Author { Id = 5, Name = "Ryan Becker" };
            _naturals = CreateBook(7697, _barnes, "The Naturals", "The Naturals", "1");
            _allIn = CreateBook(7699, _barnes, "All In", "The Naturals", "3");
            _twelve = CreateBook(7702, _barnes, "Twelve", "The Naturals", "4.5");
            _trueCrime = CreateBook(3653, _becker, "True Crime Stories: 48 Terrifying True Crime Murder Cases");
        }

        // ---- 1. "All In" grabbed as "Boost Your Marketing Game with This All-in-One Bundle" ----

        [Test]
        public void marketing_bundle_is_not_a_last_resort_bundle_for_all_in()
        {
            var remoteBook = Remote(MarketingBundle, _barnes, _allIn, ReleaseSourceType.Search);

            Assert.That(remoteBook.PackDetection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(TalearrBundleDetector.ReleaseMentionsTitle(MarketingBundle, "All In"), Is.False);
            Assert.That(TalearrBundleFallback.CanFallBack(remoteBook), Is.False);
            Assert.That(TalearrBundleFallback.BundleNamesBook(remoteBook, _allIn), Is.False);
        }

        [Test]
        public void marketing_bundle_is_rejected_by_the_title_spec_even_when_bundles_are_allowed()
        {
            var remoteBook = Remote(MarketingBundle, _barnes, _allIn, ReleaseSourceType.Search);

            var decision = Spec().IsSatisfiedBy(remoteBook, Criteria(_barnes, _allIn));

            Assert.That(decision.Accepted, Is.False);
        }

        [Test]
        public void marketing_bundle_is_not_a_title_match_for_all_in_even_with_the_author_hint()
        {
            var result = ReleaseTitleMatchScorer.FindBestMatch(MarketingBundle, _barnes.Name, new[] { _allIn }, _barnes.Name, new[] { _naturals, _allIn, _twelve });

            Assert.That(result?.IsMatch, Is.Not.True);
        }

        [TestCase("All In", "Jennifer Lynn Barnes - All-in-One Study Guide (epub)")]
        [TestCase("Deep End", "Ali Hazelwood - Off-the-Deep-End Workbook (epub)")]
        [TestCase("Not in Love", "Ali Hazelwood - Not-in-Love-with-You Playlist (epub)")]
        [TestCase("Death Row", "John Grisham - Death-Row-in-a-Day Guide (epub)")]
        public void short_title_inside_a_hyphenated_compound_does_not_match(string title, string release)
        {
            var authorName = release.Substring(0, release.IndexOf(" - ", StringComparison.Ordinal));
            var book = CreateBook(1, new Author { Name = authorName }, title);

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, authorName, new[] { book }, null, new[] { book });

            Assert.That(result?.IsMatch, Is.Not.True);
            Assert.That(result?.Problems.Select(p => p.Code), Has.Member(TitleMatchProblemCode.HyphenatedCompound));
        }

        [TestCase("All In", "Jennifer Lynn Barnes - All In (The Naturals 3) (retail) (epub)")]
        [TestCase("Two Can Play", "Ali Hazelwood - Two Can Play (2025) (epub)")]
        [TestCase("Bride", "Ali Hazelwood - Bride (retail) (epub)")]
        [TestCase("Deep End", "Ali.Hazelwood.-.Deep.End.2025.RETAIL.EPUB.eBook-CTO")]
        public void short_title_with_its_author_still_matches(string title, string release)
        {
            var authorName = release.StartsWith("Ali", StringComparison.Ordinal) ? "Ali Hazelwood" : "Jennifer Lynn Barnes";
            var book = CreateBook(1, new Author { Name = authorName }, title);

            var result = ReleaseTitleMatchScorer.FindBestMatch(release, authorName, new[] { book }, null, new[] { book });

            Assert.That(result?.IsMatch, Is.True, string.Join(", ", result?.Problems.Select(p => p.Code + ":" + p.Value) ?? Array.Empty<string>()));
        }

        [Test]
        public void short_title_bundle_must_name_the_author()
        {
            var named = Remote("Jennifer Lynn Barnes - The Naturals Box Set (All In; Bad Blood) (epub)", _barnes, _allIn, ReleaseSourceType.Search);
            var unnamed = Remote("The Naturals Box Set (All In; Bad Blood) (epub)", _barnes, _allIn, ReleaseSourceType.Search);

            Assert.That(TalearrBundleFallback.BundleNamesBook(named, _allIn), Is.True);
            Assert.That(TalearrBundleFallback.WhyBundleDoesNotNameBook(unnamed, _allIn), Does.Contain("short title"));
        }

        [Test]
        public void long_title_bundle_without_author_is_still_a_last_resort()
        {
            var book = CreateBook(4050, new Author { Name = "R.F. Kuang" }, "The Poppy War Trilogy Companion Guide");
            var remoteBook = Remote("The Poppy War Trilogy Companion Guide Box Set (epub)", book.Author, book, ReleaseSourceType.Search);

            Assert.That(TalearrBundleFallback.BundleNamesBook(remoteBook, book), Is.True);
        }

        // ---- 2. Ryan Becker's "True Crime Stories" grabbed and imported as Craig Rice ----

        [Test]
        public void craig_rice_collection_is_not_a_last_resort_bundle_for_ryan_becker()
        {
            var remoteBook = Remote(CraigRice, _becker, _trueCrime, ReleaseSourceType.UserInvokedSearch);

            Assert.That(remoteBook.PackDetection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
            Assert.That(TalearrBundleFallback.CanFallBack(remoteBook), Is.False);
            Assert.That(TalearrBundleFallback.WhyBundleDoesNotNameBook(remoteBook, _trueCrime), Is.EqualTo("Bundle names a different number: 45 (wanted 48)"));
        }

        [Test]
        public void craig_rice_release_is_rejected_for_another_author()
        {
            var remoteBook = new RemoteBook { Release = new ReleaseInfo { Title = CraigRice }, ReleaseSource = ReleaseSourceType.UserInvokedSearch };

            var decision = Spec().IsSatisfiedBy(remoteBook, Criteria(_becker, _trueCrime));

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Release is by Craig Rice, not Ryan Becker"));
        }

        [Test]
        public void craig_rice_collection_without_the_number_is_rejected_for_the_author()
        {
            var book = CreateBook(3653, _becker, "True Crime Stories: Terrifying True Crime Murder Cases");
            var remoteBook = Remote("Craig Rice - Murderers- A Collection of True Crime Stories (retail) (epub)", _becker, book, ReleaseSourceType.Search);

            Assert.That(TalearrBundleFallback.WhyBundleDoesNotNameBook(remoteBook, book), Is.EqualTo("Bundle is by Craig Rice, not Ryan Becker"));
        }

        [Test]
        public void number_in_front_of_a_short_form_counts_against_the_match()
        {
            var result = ReleaseTitleMatchScorer.FindBestMatch(CraigRice, _becker.Name, new[] { _trueCrime }, _becker.Name, new[] { _trueCrime });

            Assert.That(result?.IsMatch, Is.Not.True);
        }

        [Test]
        public void ryan_becker_short_form_release_still_matches()
        {
            var result = ReleaseTitleMatchScorer.FindBestMatch("Ryan Becker - True Crime Stories (epub)", _becker.Name, new[] { _trueCrime }, _becker.Name, new[] { _trueCrime });

            Assert.That(result?.IsMatch, Is.True);
        }

        [Test]
        public void import_rejects_a_file_that_credits_another_author()
        {
            var item = EbookFile("/downloads/completed/Books/" + CraigRice + "/" + CraigRice + ".epub", "Craig Rice");

            var other = TalearrImportAuthorCheck.FindOtherAuthor(item, _becker, _trueCrime, CraigRice);

            Assert.That(other, Is.EqualTo("Craig Rice"));
        }

        [Test]
        public void import_reads_the_file_name_when_the_ebook_has_no_author_tag()
        {
            var item = EbookFile("/downloads/completed/Books/x/" + CraigRice + ".epub");

            Assert.That(TalearrImportAuthorCheck.FindOtherAuthor(item, _becker, _trueCrime, "x"), Is.EqualTo("Craig Rice"));
        }

        [TestCase("Ryan Becker")]
        [TestCase("Becker, Ryan")]
        [TestCase("Ryan Becker; Nancy Alyssa Veysey")]
        public void import_keeps_a_file_by_the_expected_author(string tag)
        {
            var item = EbookFile("/downloads/completed/Books/True Crime Stories/True Crime Stories.epub", tag);

            Assert.That(TalearrImportAuthorCheck.FindOtherAuthor(item, _becker, _trueCrime, "True Crime Stories 48 Cases (epub)"), Is.Null);
        }

        [Test]
        public void import_keeps_a_file_by_a_pen_name()
        {
            var author = new Author { Name = "Robert Galbraith", Pseudonyms = new List<string> { "J.K. Rowling" } };
            var book = CreateBook(1, author, "The Cuckoo's Calling");
            var item = EbookFile("/downloads/J.K. Rowling - The Cuckoo's Calling.epub", "J.K. Rowling");

            Assert.That(TalearrImportAuthorCheck.FindOtherAuthor(item, author, book, "J.K. Rowling - The Cuckoo's Calling (epub)"), Is.Null);
        }

        [Test]
        public void wrong_author_import_rejection_fails_the_download_like_a_wrong_book()
        {
            var message = TalearrImportAuthorCheck.BuildRejection("Craig Rice", _becker, "'True Crime Stories: 48 Terrifying True Crime Murder Cases' (BookId 3653, work hc:1)");
            var status = new TrackedDownloadStatusMessage(CraigRice + ".epub", new List<string> { message });

            var reason = ImportRejectionClassifier.Classify(new[] { status });

            Assert.That(reason, Is.EqualTo("release is by Craig Rice, not Ryan Becker, so it is not the wanted book"));
        }

        // ---- 3. "The Naturals" (#1) grabbed as "[The Naturals 04.5] - Twelve" ----

        [TestCase(Twelve)]
        [TestCase(TwelveNzbGeek)]
        public void other_position_in_the_series_does_not_match_the_series_named_book(string release)
        {
            var result = ReleaseTitleMatchScorer.FindBestMatch(release, _barnes.Name, new[] { _naturals }, null, new[] { _naturals, _allIn, _twelve });

            Assert.That(result?.IsMatch, Is.Not.True);
            Assert.That(result?.Problems.Select(p => p.Code), Has.Member(TitleMatchProblemCode.SeriesNameOnly));
            Assert.That(ReleaseTitleMatchSpecification.IsAcceptedMatch(result, null, NzbDrone.Core.Configuration.BookMatchingStrictness.Balanced), Is.False);
        }

        [TestCase(Twelve)]
        [TestCase(TwelveNzbGeek)]
        public void other_position_in_the_series_is_rejected_by_the_title_spec(string release)
        {
            var remoteBook = new RemoteBook { Release = new ReleaseInfo { Title = release }, ReleaseSource = ReleaseSourceType.UserInvokedSearch };

            var decision = Spec().IsSatisfiedBy(remoteBook, Criteria(_barnes, _naturals, _allIn, _twelve));

            Assert.That(decision.Accepted, Is.False);
        }

        [TestCase("Jennifer Lynn Barnes - [The Naturals 01] - The Naturals (retail) (epub)")]
        [TestCase("Jennifer Lynn Barnes - The Naturals (2013) (epub)")]
        public void series_named_book_still_matches_its_own_release(string release)
        {
            var result = ReleaseTitleMatchScorer.FindBestMatch(release, _barnes.Name, new[] { _naturals }, null, new[] { _naturals, _allIn, _twelve });

            Assert.That(result?.IsMatch, Is.True, string.Join(", ", result?.Problems.Select(p => p.Code + ":" + p.Value) ?? Array.Empty<string>()));
        }

        private static ReleaseTitleMatchSpecification Spec()
        {
            return new ReleaseTitleMatchSpecification(LogManager.GetCurrentClassLogger());
        }

        private BookSearchCriteria Criteria(Author author, Book target, params Book[] catalog)
        {
            return new BookSearchCriteria
            {
                Author = author,
                Books = new List<Book> { target },
                AuthorCatalog = catalog.Length == 0 ? new List<Book> { target } : catalog.ToList()
            };
        }

        private static RemoteBook Remote(string title, Author author, Book book, ReleaseSourceType source)
        {
            return new RemoteBook
            {
                Release = new ReleaseInfo { Title = title },
                Author = author,
                Books = new List<Book> { book },
                ReleaseSource = source,
                PackDetection = ReleasePackDetector.Detect(title, book, new List<Book> { book })
            };
        }

        private static LocalBook EbookFile(string path, string authorTag = null)
        {
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (authorTag != null)
            {
                tags["author"] = new List<string> { authorTag };
            }

            return new LocalBook { Path = path, RawTags = new RawFileTags { AllTags = tags } };
        }

        private static Book CreateBook(int id, Author author, string title, string seriesName = null, string seriesPosition = null)
        {
            return new Book
            {
                Id = id,
                Title = title,
                Author = author,
                AuthorId = author.Id,
                SeriesName = seriesName,
                SeriesPosition = seriesPosition,
                MediaType = BookMediaType.Ebook,
                EbookMonitored = true,
                Editions = new List<Edition> { new Edition { Id = id, Title = title, Monitored = true } }
            };
        }
    }
}
