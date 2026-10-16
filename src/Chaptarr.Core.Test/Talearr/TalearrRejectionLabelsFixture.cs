using System;
using System.Linq;
using Chaptarr.Api.V1.Indexers;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.Parser.Model;

namespace Chaptarr.Core.Test.Talearr
{
    [TestFixture]
    public class TalearrRejectionLabelsFixture
    {
        // Stands in for a specification another TaleArr feature adds later: it is not in the
        // label table, so the label must come from its class name.
        private sealed class UnabridgedPreferenceSpecification : IDecisionEngineSpecification
        {
            public SpecificationPriority Priority => SpecificationPriority.Default;
            public RejectionType Type => RejectionType.Permanent;

            public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
            {
                return Decision.Reject("Release is abridged");
            }
        }

        private sealed class BlocklistLikeSpecification : IDecisionEngineSpecification
        {
            public SpecificationPriority Priority => SpecificationPriority.Database;
            public RejectionType Type => RejectionType.Temporary;

            public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
            {
                return Decision.RejectSoftFilter("Release is blocklisted", "Language");
            }
        }

        [TestCase("Matching", "Wrong book")]
        [TestCase("Title", "Wrong book")]
        [TestCase("Author", "Wrong author")]
        [TestCase("Pack", "Pack")]
        [TestCase("Language", "Language")]
        [TestCase("Quality", "Quality")]
        [TestCase("Release Profile", "Release profile")]
        [TestCase("Abridged", "Abridged")]
        public void category_set_by_the_specification_wins(string category, string expected)
        {
            var rejection = new Rejection("reason", RejectionType.Permanent, false, category, 3) { Source = "AcceptableSizeSpecification" };

            Assert.That(TalearrRejectionLabels.GetLabel(rejection), Is.EqualTo(expected));
        }

        [TestCase("AcceptableSizeSpecification", "Size")]
        [TestCase("MaximumSizeSpecification", "Size")]
        [TestCase("BlocklistSpecification", "Blocklisted")]
        [TestCase("ReleaseLanguageSpecification", "Language")]
        [TestCase("MultiBookReleaseSpecification", "Pack")]
        [TestCase("ReleaseTitleMatchSpecification", "Wrong book")]
        [TestCase("QueueSpecification", "Already queued")]
        [TestCase("HistorySpecification", "Already grabbed")]
        [TestCase("UpgradeDiskSpecification", "Not an upgrade")]
        public void general_rejections_are_labelled_from_their_specification(string source, string expected)
        {
            var rejection = new Rejection("reason") { Source = source };

            Assert.That(rejection.Category, Is.EqualTo("General"));
            Assert.That(TalearrRejectionLabels.GetLabel(rejection), Is.EqualTo(expected));
        }

        [TestCase("UnabridgedPreferenceSpecification", "Unabridged preference")]
        [TestCase("ArchiveContentsSpecification", "Archive contents")]
        [TestCase("MAMFreeleechSpecification", "MAM freeleech")]
        [TestCase("Bundle", "Bundle")]
        public void unknown_specifications_get_a_readable_label_from_their_name(string source, string expected)
        {
            var rejection = new Rejection("reason") { Source = source };

            Assert.That(TalearrRejectionLabels.GetLabel(rejection), Is.EqualTo(expected));
        }

        [TestCase("Release is abridged", "Abridged")]
        [TestCase("Release is blocklisted", "Blocklisted")]
        [TestCase("Release language [German] is not allowed", "Language")]
        [TestCase("Release appears to contain multiple books", "Pack")]
        [TestCase("Something else entirely", TalearrRejectionLabels.DefaultLabel)]
        public void rejections_without_category_or_source_fall_back_to_reason_hints(string reason, string expected)
        {
            Assert.That(TalearrRejectionLabels.GetLabel(new Rejection(reason)), Is.EqualTo(expected));
        }

        [Test]
        public void from_specification_keeps_the_decision_and_records_the_source()
        {
            var rejection = TalearrRejectionLabels.FromSpecification(new BlocklistLikeSpecification(), new BlocklistLikeSpecification().IsSatisfiedBy(null, null));

            Assert.That(rejection.Reason, Is.EqualTo("Release is blocklisted"));
            Assert.That(rejection.Type, Is.EqualTo(RejectionType.Temporary));
            Assert.That(rejection.Category, Is.EqualTo("Language"), "the label must never change the category the filters read");
            Assert.That(rejection.CanBypass, Is.True);
            Assert.That(rejection.Severity, Is.EqualTo(2));
            Assert.That(rejection.Source, Is.EqualTo(nameof(BlocklistLikeSpecification)));
        }

        [Test]
        public void release_resource_carries_label_and_source_for_every_rejection()
        {
            var spec = new UnabridgedPreferenceSpecification();
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo
                {
                    Title = "Some Book (Abridged)",
                    Indexer = "Test",
                    PublishDate = DateTime.UtcNow
                }
            };

            var decision = new DownloadDecision(remoteBook,
                TalearrRejectionLabels.FromSpecification(spec, spec.IsSatisfiedBy(remoteBook, null)),
                new Rejection("Release language [French] is not allowed", RejectionType.Permanent, true, "Language", 2) { Source = "ReleaseLanguageSpecification" },
                new Rejection("Unknown Author", RejectionType.Permanent, false, "Author", 3));

            var resource = decision.ToResource();

            Assert.That(resource.Rejections, Is.EqualTo(new[]
            {
                "Release is abridged",
                "Release language [French] is not allowed",
                "Unknown Author"
            }), "the legacy string list stays as it was");

            Assert.That(resource.RejectionDetails.Select(r => r.Label), Is.EqualTo(new[] { "Unabridged preference", "Language", "Wrong author" }));
            Assert.That(resource.RejectionDetails.Select(r => r.Source), Is.EqualTo(new[] { nameof(UnabridgedPreferenceSpecification), "ReleaseLanguageSpecification", null }));
            Assert.That(resource.RejectionDetails.Select(r => r.Category), Is.EqualTo(new[] { "General", "Language", "Author" }));
        }

        [Test]
        public void interactive_search_filtering_ignores_the_new_label()
        {
            var remoteBook = new RemoteBook { Release = new ReleaseInfo { Title = "x" } };
            var sizeRejection = new Rejection("Too big") { Source = "MaximumSizeSpecification" };
            var decision = new DownloadDecision(remoteBook, sizeRejection);

            var (shouldFilter, _) = ReleaseController.ShouldFilterFromInteractiveSearch(decision);

            Assert.That(TalearrRejectionLabels.GetLabel(sizeRejection), Is.EqualTo("Size"));
            Assert.That(shouldFilter, Is.False, "a profile rejection stays visible in interactive search with its reason");
        }
    }
}
