using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Identification;
using NzbDrone.Core.Talearr.Calibre;

namespace Chaptarr.Core.Test.Talearr.Calibre
{
    // Talearr: the Manual Import preview with a Calibre library set (issue #4).
    [TestFixture]
    public class TalearrCalibreManualPreviewFixture
    {
        private CalibreTestLibrary _library;
        private string _path;
        private int _calibreId;

        [SetUp]
        public void SetUp()
        {
            _library = new CalibreTestLibrary();
            _calibreId = _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "PDF");
            _path = _library.FilePath(_calibreId, "Dune", "Frank Herbert", "pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, "%PDF-1.4");
        }

        [TearDown]
        public void TearDown()
        {
            _library.Dispose();
        }

        private class ThrowingProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class PoorPdfTags : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataTagService.ReadAllTagsAndDuration))
                {
                    return (new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["title"] = new List<string> { "Microsoft Word - scan.docx" },
                        ["author"] = new List<string> { "jsmith" }
                    }, (int?)null);
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private class Matcher : DispatchProxy
        {
            public bool OfferEdition { get; set; }
            public List<DiscoveredFileWithMetadata> Seen { get; } = new List<DiscoveredFileWithMetadata>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IFileMatchingService.MatchFilesToLibraryAsync) && args?[0] is DiscoveredFileWithMetadata[] files)
                {
                    Seen.AddRange(files);
                    var suggestions = OfferEdition
                        ? new[] { new AuthorSuggestion { ProviderId = "hc:1", AuthorName = "Frank Herbert", BookProviderId = "hc:2", BookTitle = "Dune", EditionHardcoverId = "hc:3", EditionTitle = "Dune" } }
                        : Array.Empty<AuthorSuggestion>();

                    return Task.FromResult(new FileMatchResult
                    {
                        UnmatchedFiles = files.Select(file => new UnmatchedFile { File = file, Reason = "NO_MATCH", PotentialAuthors = suggestions }).ToArray()
                    });
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private (NzbDrone.Core.Parser.Model.LocalBook Book, Matcher Matcher) Preview(bool enabled, bool offerEdition)
        {
            var matching = DispatchProxy.Create<IFileMatchingService, Matcher>();
            var matcher = (Matcher)(object)matching;
            matcher.OfferEdition = offerEdition;

            var sut = new SimpleImportDecisionMaker(
                DispatchProxy.Create<IMetadataTagService, PoorPdfTags>(),
                matching,
                DispatchProxy.Create<IAuthorService, ThrowingProxy>(),
                DispatchProxy.Create<IBookService, ThrowingProxy>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy>(),
                DispatchProxy.Create<IMediaFileService, ThrowingProxy>(),
                LogManager.GetCurrentClassLogger(),
                new TalearrCalibreIdentifier(_library.Settings(enabled), LogManager.GetCurrentClassLogger()));

            var decisions = sut.GetImportDecisions(
                new List<IFileInfo> { new FileSystem().FileInfo.FromFileName(_path) },
                null,
                new ImportDecisionMakerInfo(),
                new ImportDecisionMakerConfig { Filter = FilterFilesType.None, NewDownload = true, IncludeExisting = true });

            return (decisions.Single().Item, matcher);
        }

        [Test]
        public void preview_shows_the_calibre_evidence_and_says_when_the_provider_has_no_edition()
        {
            var (book, matcher) = Preview(enabled: true, offerEdition: false);

            Assert.Multiple(() =>
            {
                Assert.That(matcher.Seen.Single().AllTags["title"], Is.EqualTo(new[] { "Dune" }));
                Assert.That(matcher.Seen.Single().AllTags["isbn"], Is.EqualTo(new[] { "9780441013593" }));
                Assert.That(book.RawTags.AllTags["title"], Is.EqualTo(new[] { "Microsoft Word - scan.docx" }), "the row keeps the file's own tags");
                Assert.That(book.TalearrCalibreEvidence, Is.EqualTo("Identified from Calibre (library path): Dune by Frank Herbert, ISBN 9780441013593, language eng"));
                Assert.That(book.TalearrCalibreWarning, Is.EqualTo("Calibre identifies this file as Dune by Frank Herbert (ISBN 9780441013593), but the metadata provider has no matching edition. Choose the book yourself."));
            });
        }

        [Test]
        public void preview_has_no_warning_when_the_provider_offers_an_edition()
        {
            var (book, _) = Preview(enabled: true, offerEdition: true);

            Assert.Multiple(() =>
            {
                Assert.That(book.TalearrCalibreEvidence, Does.StartWith("Identified from Calibre"));
                Assert.That(book.TalearrCalibreWarning, Is.Null);
                Assert.That(book.SuggestedForeignEditionId, Is.EqualTo("hc:3"));
            });
        }

        [Test]
        public void preview_is_unchanged_with_the_setting_off()
        {
            var (book, matcher) = Preview(enabled: false, offerEdition: false);

            Assert.Multiple(() =>
            {
                Assert.That(matcher.Seen.Single().AllTags["title"], Is.EqualTo(new[] { "Microsoft Word - scan.docx" }));
                Assert.That(matcher.Seen.Single().AllTags.ContainsKey("isbn"), Is.False);
                Assert.That(book.TalearrCalibreEvidence, Is.Null);
                Assert.That(book.TalearrCalibreWarning, Is.Null);
            });
        }
    }
}
