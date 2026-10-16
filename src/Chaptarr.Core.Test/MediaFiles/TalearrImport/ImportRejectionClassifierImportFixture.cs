using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles.TalearrImport;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    /// <summary>
    /// The grab policy fails a download only for rejections ImportRejectionClassifier recognises.
    /// These pin the two TaleArr import rejections to it: wrong file language always fails,
    /// archives fail only when the release itself is unusable.
    /// </summary>
    [TestFixture]
    public class ImportRejectionClassifierImportFixture
    {
        private const string Title = "John.Grisham.The.Pelican.Brief.2021.SWEDiSH.RETAiL.ePub.eBOOK-DECiPHER-xpost";

        private const string LanguageReason =
            "File language is Swedish (swe) (from EPUB dc:language), but English (eng) is wanted (editions of 'The Pelican Brief'). " +
            "TaleArr rejected this release so another one can be tried.";

        private static TrackedDownloadStatusMessage[] Status(params string[] messages)
        {
            return new[] { new TrackedDownloadStatusMessage(Title, new List<string>(messages)) };
        }

        [Test]
        public void should_fail_a_release_in_the_wrong_language()
        {
            var reason = ImportRejectionClassifier.Classify(Status("WRONG_LANGUAGE", LanguageReason));

            Assert.That(reason, Is.EqualTo("release is in Swedish (swe), not English (eng)"));
        }

        [Test]
        public void should_fail_a_wrong_language_reason_with_the_import_prefix()
        {
            Assert.That(ImportRejectionClassifier.Classify(Status("Rejected: " + LanguageReason)), Does.StartWith("release is in Swedish"));
        }

        [Test]
        public void should_fail_on_language_from_the_release_name_with_several_wanted_languages()
        {
            var reason = ImportRejectionClassifier.Classify(Status(
                "File language is Danish (dan) (from release name), but English (eng) or German (deu) is wanted (metadata profile 'Ebook'). " +
                "TaleArr rejected this release so another one can be tried."));

            Assert.That(reason, Is.EqualTo("release is in Danish (dan), not English (eng) or German (deu)"));
        }

        [Test]
        public void should_fail_archives_that_are_a_release_problem()
        {
            var message = $"{ArchiveUnpackResult.FailurePrefix} /downloads/Book: bomb.zip expands to more than 256 MiB from 4 KiB, which looks like a zip bomb. {ArchiveUnpackResult.ReleaseProblemSuffix}";

            Assert.That(ImportRejectionClassifier.Classify(Status(message)), Is.EqualTo("release archives are unusable"));
        }

        [Test]
        public void should_leave_transient_archive_failures_in_the_queue()
        {
            var message = $"{ArchiveUnpackResult.FailurePrefix} /downloads/Book: not enough free space to unpack (10 bytes free, at least 2 MiB needed). {ArchiveUnpackResult.TransientSuffix}";

            Assert.That(ImportRejectionClassifier.Classify(Status(message)), Is.Null);
        }

        [Test]
        public void should_not_fail_when_language_is_mixed_with_an_unknown_reason()
        {
            Assert.That(ImportRejectionClassifier.Classify(Status(LanguageReason, "Rejected: No match in local library")), Is.Null);
        }
    }
}
