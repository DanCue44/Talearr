using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Pending;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    // TaleArr: bundles as a last resort. A box set is grabbed only when a search found no single
    // copy of the wanted book, and never from RSS.
    [TestFixture]
    public class BundleFallbackFixture
    {
        private const string BundleTitle = "R.F. Kuang - The Poppy War Box Set (Books 1-3) (epub)";

        private Book _poppyWar;

        [SetUp]
        public void SetUp()
        {
            _poppyWar = new Book { Id = 4050, AuthorId = 12, Title = "The Poppy War", MediaType = BookMediaType.Ebook, EbookMonitored = true };
        }

        // ---- specification ----

        [Test]
        public void spec_should_accept_and_flag_a_bundle_from_a_search_when_bundles_are_not_allowed()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(ReleaseSourceType.Search);

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.True);
            Assert.That(remoteBook.IsBundle, Is.True);
            Assert.That(remoteBook.IsBundleFallback, Is.True);
            Assert.That(remoteBook.PackDetection.Verdict, Is.EqualTo(ReleasePackDetectionVerdict.MultipleBooks));
        }

        [Test]
        public void spec_should_flag_a_bundle_in_interactive_search_even_when_it_does_not_name_the_book()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(ReleaseSourceType.InteractiveSearch, "R.F. Kuang 5 Books Collection Set");

            Assert.That(spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria()).Accepted, Is.True);
            Assert.That(remoteBook.IsBundleFallback, Is.True);
        }

        [Test]
        public void spec_should_reject_an_automatic_bundle_that_does_not_name_the_wanted_book()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(ReleaseSourceType.Search, "R.F. Kuang 5 Books Collection Set");

            var decision = spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria());

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
            Assert.That(remoteBook.IsBundleFallback, Is.False);
        }

        [TestCase(ReleaseSourceType.Rss)]
        [TestCase(ReleaseSourceType.ReleasePush)]
        public void spec_should_keep_rejecting_bundles_outside_searches(ReleaseSourceType source)
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(source);

            var decision = spec.IsSatisfiedBy(remoteBook, null);

            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Category, Is.EqualTo("Pack"));
            Assert.That(remoteBook.IsBundleFallback, Is.False);
        }

        [Test]
        public void spec_should_accept_a_bundle_without_the_last_resort_flag_when_bundles_are_allowed()
        {
            var policy = new FakeGrabPolicyService { Policy = new TalearrGrabPolicy { AllowBundles = true } };
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), policy);
            var remoteBook = Bundle(ReleaseSourceType.Rss);

            Assert.That(spec.IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
            Assert.That(remoteBook.IsBundle, Is.True);
            Assert.That(remoteBook.IsBundleFallback, Is.False);
        }

        [Test]
        public void spec_should_leave_a_wanted_omnibus_unflagged()
        {
            var spec = new MultiBookReleaseSpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(ReleaseSourceType.Search);
            remoteBook.Books = new List<Book> { new Book { Id = 4060, Title = "The Poppy War Trilogy", MediaType = BookMediaType.Ebook } };

            Assert.That(spec.IsSatisfiedBy(remoteBook, new BookSearchCriteria()).Accepted, Is.True);
            Assert.That(remoteBook.IsBundle, Is.False);
            Assert.That(remoteBook.IsBundleFallback, Is.False);
        }

        [Test]
        public void discography_spec_should_follow_the_same_last_resort_rule()
        {
            var spec = new DiscographySpecification(LogManager.GetCurrentClassLogger(), new FakeGrabPolicyService());
            var remoteBook = Bundle(ReleaseSourceType.Search);
            remoteBook.PackDetection = ReleasePackDetection.None();
            remoteBook.ParsedBookInfo = new ParsedBookInfo { Discography = true };

            Assert.That(spec.IsSatisfiedBy(remoteBook, null).Accepted, Is.True);
            Assert.That(remoteBook.IsBundleFallback, Is.True);

            var rss = Bundle(ReleaseSourceType.Rss);
            rss.ParsedBookInfo = new ParsedBookInfo { Discography = true };
            Assert.That(spec.IsSatisfiedBy(rss, null).Accepted, Is.False);
        }

        // ---- ProcessDownloadDecisions ----

        [Test]
        public async Task single_copy_wins_over_a_bundle_for_the_same_book()
        {
            var downloads = new RecordingDownloadService();
            var bundle = BundleDecision(ReleaseSourceType.Search);
            var single = SingleDecision(ReleaseSourceType.Search);

            var processed = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { bundle, single });

            Assert.That(downloads.Grabbed.Select(r => r.Release.Title), Is.EquivalentTo(new[] { single.RemoteBook.Release.Title }));
            Assert.That(processed.Grabbed, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task bundle_is_grabbed_from_a_search_when_it_is_the_only_copy()
        {
            var downloads = new RecordingDownloadService();
            var bundle = BundleDecision(ReleaseSourceType.Search);

            var processed = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { bundle });

            Assert.That(downloads.Grabbed, Has.Count.EqualTo(1));
            Assert.That(downloads.Grabbed[0].Release.Title, Is.EqualTo(BundleTitle));
            Assert.That(processed.Grabbed, Is.EquivalentTo(new[] { bundle }));
        }

        [Test]
        public async Task bundle_from_rss_is_never_grabbed_or_stored_as_pending()
        {
            var downloads = new RecordingDownloadService();
            var pending = new RecordingPendingReleaseService();
            var bundle = BundleDecision(ReleaseSourceType.Rss);

            var processed = await Processor(downloads, pending).ProcessDecisions(new List<DownloadDecision> { bundle });

            Assert.That(downloads.Grabbed, Is.Empty);
            Assert.That(processed.Grabbed, Is.Empty);
            Assert.That(processed.Pending, Is.Empty);
            Assert.That(pending.Added, Is.Empty);
        }

        [Test]
        public async Task delayed_single_copy_keeps_the_bundle_back()
        {
            var downloads = new RecordingDownloadService();
            var pending = new RecordingPendingReleaseService();
            var bundle = BundleDecision(ReleaseSourceType.Search);
            var delayed = SingleDecision(ReleaseSourceType.Search, new Rejection("Waiting for the delay profile", RejectionType.Temporary));

            var processed = await Processor(downloads, pending).ProcessDecisions(new List<DownloadDecision> { bundle, delayed });

            Assert.That(downloads.Grabbed, Is.Empty);
            Assert.That(processed.Pending, Is.EquivalentTo(new[] { delayed }));
            Assert.That(pending.Added.Select(p => p.Item1), Is.EquivalentTo(new[] { delayed }));
        }

        [Test]
        public async Task only_one_bundle_is_grabbed_for_a_book()
        {
            var downloads = new RecordingDownloadService();
            var first = BundleDecision(ReleaseSourceType.Search);
            var second = BundleDecision(ReleaseSourceType.UserInvokedSearch, "The Poppy War Trilogy by R.F. Kuang (epub)");

            var processed = await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { first, second });

            Assert.That(downloads.Grabbed, Has.Count.EqualTo(1));
            Assert.That(processed.Grabbed, Is.EquivalentTo(new[] { first }));
        }

        [Test]
        public async Task bundle_is_still_grabbed_for_a_book_that_no_single_copy_covered()
        {
            var dragonRepublic = new Book { Id = 4051, AuthorId = 12, Title = "The Dragon Republic", MediaType = BookMediaType.Ebook, EbookMonitored = true };
            var babel = new Book { Id = 4070, AuthorId = 12, Title = "Babel", MediaType = BookMediaType.Ebook, EbookMonitored = true };
            var downloads = new RecordingDownloadService();
            var single = SingleDecision(ReleaseSourceType.Search);
            var bundle = BundleDecision(ReleaseSourceType.Search, "R.F. Kuang - The Poppy War / The Dragon Republic Box Set (epub)");
            bundle.RemoteBook.Books.Add(dragonRepublic);
            bundle.RemoteBook.Books.Add(babel);

            await Processor(downloads).ProcessDecisions(new List<DownloadDecision> { single, bundle });

            // The single covers The Poppy War; Babel is not named in the bundle, so only The Dragon Republic is a target.
            Assert.That(downloads.Grabbed, Has.Count.EqualTo(2));
            Assert.That(downloads.Grabbed[1].Books.Select(b => b.Id), Is.EquivalentTo(new[] { dragonRepublic.Id }));
        }

        // ---- history round trip ----

        [Test]
        public void bundle_grab_history_data_round_trips()
        {
            var remoteBook = Bundle(ReleaseSourceType.Search);
            remoteBook.IsBundle = true;
            remoteBook.IsBundleFallback = true;
            var data = new Dictionary<string, string>();

            TalearrBundleFallback.AddHistoryData(remoteBook, data);

            Assert.That(data[TalearrBundleFallback.BundleHistoryKey], Is.EqualTo("true"));
            Assert.That(data[TalearrBundleFallback.BundleTargetsHistoryKey], Is.EqualTo("4050"));
            Assert.That(TalearrBundleFallback.IsBundleHistory(data), Is.True);
            Assert.That(TalearrBundleFallback.IsBundleFallbackHistory(data), Is.True);

            var single = new Dictionary<string, string>();
            TalearrBundleFallback.AddHistoryData(new RemoteBook(), single);
            Assert.That(single, Is.Empty);
            Assert.That(TalearrBundleFallback.IsBundleHistory(single), Is.False);
        }

        [Test]
        public void bundle_extra_reasons_never_fail_a_download()
        {
            var extra = new TrackedDownloadStatusMessage("Babel.epub", new List<string> { TalearrBundleFallback.BundleExtraReason("'Babel' (BookId 4070)") });

            Assert.That(ImportRejectionClassifier.Classify(new[] { extra }), Is.Null);

            var wrongBook = new TrackedDownloadStatusMessage("Mate.m4b", new List<string>
            {
                "Completed download was grabbed for 'Bride' (BookId 3756), but import matched 'Mate' (BookId 3754)."
            });

            Assert.That(ImportRejectionClassifier.Classify(new[] { extra, wrongBook }), Is.Not.Null);
        }

        private RemoteBook Bundle(ReleaseSourceType source, string title = BundleTitle)
        {
            return new RemoteBook
            {
                ReleaseSource = source,
                Release = new ReleaseInfo { Title = title },
                Books = new List<Book> { _poppyWar },
                PackDetection = ReleasePackDetector.Detect(title, _poppyWar, new List<Book> { _poppyWar })
            };
        }

        private DownloadDecision BundleDecision(ReleaseSourceType source, string title = BundleTitle)
        {
            var remoteBook = Decision(source, title).RemoteBook;
            remoteBook.PackDetection = ReleasePackDetector.Detect(title, _poppyWar, new List<Book> { _poppyWar });
            remoteBook.IsBundle = true;
            remoteBook.IsBundleFallback = true;
            return new DownloadDecision(remoteBook);
        }

        private DownloadDecision SingleDecision(ReleaseSourceType source, params Rejection[] rejections)
        {
            return Decision(source, "R F Kuang - The Poppy War (2024 ed) (retail) (epub)", rejections);
        }

        private DownloadDecision Decision(ReleaseSourceType source, string title, params Rejection[] rejections)
        {
            return new DownloadDecision(new RemoteBook
            {
                Author = new Author { Id = 12, Name = "R.F. Kuang" },
                Books = new List<Book> { _poppyWar },
                ReleaseSource = source,
                Release = new ReleaseInfo
                {
                    Title = title,
                    IndexerId = 3,
                    Indexer = "NZBgeek",
                    DownloadProtocol = DownloadProtocol.Usenet,
                    PublishDate = DateTime.UtcNow
                },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.EPUB) }
            }, rejections);
        }

        private static ProcessDownloadDecisions Processor(IDownloadService downloadService, IPendingReleaseService pending = null)
        {
            return new ProcessDownloadDecisions(downloadService,
                new IdentityPrioritizer(),
                pending ?? new RecordingPendingReleaseService(),
                LogManager.GetCurrentClassLogger());
        }

        private sealed class RecordingDownloadService : IDownloadService
        {
            public List<RemoteBook> Grabbed { get; } = new();

            public Task DownloadReport(RemoteBook remoteBook, int? downloadClientId)
            {
                Grabbed.Add(remoteBook);
                return Task.CompletedTask;
            }
        }

        private sealed class IdentityPrioritizer : IPrioritizeDownloadDecision
        {
            public List<DownloadDecision> PrioritizeDecisions(List<DownloadDecision> decisions)
            {
                return decisions;
            }
        }

        private sealed class RecordingPendingReleaseService : IPendingReleaseService
        {
            public List<Tuple<DownloadDecision, PendingReleaseReason>> Added { get; } = new();

            public void Add(DownloadDecision decision, PendingReleaseReason reason) => Added.Add(Tuple.Create(decision, reason));
            public void AddMany(List<Tuple<DownloadDecision, PendingReleaseReason>> decisions) => Added.AddRange(decisions);
            public List<ReleaseInfo> GetPending() => new();
            public List<RemoteBook> GetPendingRemoteBooks(int authorId) => new();
            public List<NzbDrone.Core.Queue.Queue> GetPendingQueue() => new();
            public NzbDrone.Core.Queue.Queue FindPendingQueueItem(int queueId) => null;
            public void RemovePendingQueueItems(int queueId) { }
            public RemoteBook OldestPendingRelease(int authorId, int[] bookIds) => null;
        }
    }
}
