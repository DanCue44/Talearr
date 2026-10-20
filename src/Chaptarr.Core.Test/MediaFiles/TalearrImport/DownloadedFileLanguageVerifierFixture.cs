using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.TalearrImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    [TestFixture]
    public class DownloadedFileLanguageVerifierFixture
    {
        private const string SwedishRelease = "John.Grisham.The.Pelican.Brief.2021.SWEDiSH.RETAiL.ePub.eBOOK-DECiPHER-xpost";

        private StubLanguageReader _reader;
        private MetadataProfileServiceProxy _profiles;
        private Author _author;
        private Book _book;
        private DownloadClientItem _downloadItem;
        private RemoteBook _remoteBook;

        [SetUp]
        public void SetUp()
        {
            _reader = new StubLanguageReader();
            _profiles = (MetadataProfileServiceProxy)(object)DispatchProxy.Create<IMetadataProfileService, MetadataProfileServiceProxy>();
            _author = new Author { Id = 7, Name = "John Grisham", EbookMetadataProfileId = 2 };
            _book = new Book
            {
                Id = 3091,
                AuthorId = 7,
                Title = "The Pelican Brief",
                MediaType = BookMediaType.Ebook,
                Author = _author,
                Editions = new List<Edition>
                {
                    new Edition { Id = 1, BookId = 3091, Title = "The Pelican Brief", Language = "eng", Monitored = true },
                    new Edition { Id = 2, BookId = 3091, Title = "Pelikanfallet", Language = "swe", Monitored = false },
                    new Edition { Id = 3, BookId = 3091, Title = "Achter gesloten deuren", Language = "nld", Monitored = false }
                }
            };
            _downloadItem = new DownloadClientItem { DownloadId = "abc", Title = SwedishRelease };
            _remoteBook = new RemoteBook
            {
                Author = _author,
                Books = new List<Book> { _book },
                Release = new ReleaseInfo { Title = SwedishRelease }
            };
        }

        private DownloadedFileLanguageVerifier Subject(bool enabled = true)
        {
            return new DownloadedFileLanguageVerifier(
                _reader,
                (IMetadataProfileService)(object)_profiles,
                DispatchProxy.Create<IEditionService, ThrowingEditionService>(),
                TalearrConfigProxy.Create(verifyFileLanguage: enabled),
                LogManager.GetCurrentClassLogger());
        }

        private ImportDecision<LocalBook> Decision(string path, Book matchedBook = null, Edition matchedEdition = null, params Rejection[] rejections)
        {
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = path,
                Book = matchedBook,
                Author = matchedBook?.Author,
                Edition = matchedEdition
            });

            foreach (var rejection in rejections)
            {
                decision.Reject(rejection);
            }

            return decision;
        }

        [Test]
        public void should_reject_a_swedish_epub_when_the_monitored_edition_is_english()
        {
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.False);
            var rejection = decision.Rejections.Single();
            Assert.That(rejection.IsLanguageFilter, Is.True);
            Assert.That(rejection.Type, Is.EqualTo(RejectionType.Permanent));
            Assert.That(rejection.Reason, Does.Contain("Swedish (swe)"));
            Assert.That(rejection.Reason, Does.Contain("English (eng)"));
            Assert.That(rejection.Reason, Does.Contain("EPUB dc:language"));
            Assert.That(rejection.Reason, Does.Contain("Talearr"));
        }

        [Test]
        public void should_keep_a_file_in_the_wanted_language()
        {
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "eng" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_trust_the_file_over_the_release_name()
        {
            // The release name says Swedish but the file itself says English: the file wins.
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "eng" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_fall_back_to_the_release_name_when_the_file_is_silent()
        {
            var decision = Decision("/dl/01 - Chapter.mp3", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.False);
            Assert.That(decision.Rejections.Single().Reason, Does.Contain("release name"));
            Assert.That(decision.Rejections.Single().Reason, Does.Contain("Swedish"));
        }

        [Test]
        public void should_accept_a_silent_file_with_a_neutral_release_name()
        {
            _downloadItem.Title = "John Grisham - The Pelican Brief (Unabridged)";
            _remoteBook.Release.Title = _downloadItem.Title;
            var decision = Decision("/dl/01 - Chapter.mp3", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_check_files_that_did_not_match_the_grabbed_book()
        {
            // A Swedish file titled "Pelikanfallet" may not match anything; it is still Swedish and
            // the grab was for the English book.
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", null, null, new Rejection("No match in local library"));

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Rejections.Select(r => r.Category), Is.EquivalentTo(new[] { "General", "Language" }));
        }

        [Test]
        public void should_use_the_grabbed_book_when_a_translation_matched_its_foreign_edition()
        {
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[1]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.False);
        }

        [Test]
        public void should_not_trust_a_single_mislabelled_monitored_edition()
        {
            // Seen in the wild: the monitored English "Animal Farm" ebook edition says "urd".
            _book.Title = "Animal Farm";
            _book.Editions = new List<Edition>
            {
                new Edition { Id = 10, BookId = 3091, Title = "Animal Farm", Language = "urd", Monitored = true },
                new Edition { Id = 11, BookId = 3091, Title = "Animal Farm", Language = "eng", Monitored = false },
                new Edition { Id = 12, BookId = 3091, Title = "Djurfarmen", Language = "swe", Monitored = false }
            };
            _reader.Evidence["/dl/english.epub"] = new FileLanguageEvidence(new[] { "eng" }, "EPUB dc:language");
            _reader.Evidence["/dl/swedish.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var english = Decision("/dl/english.epub", _book, _book.Editions[0]);
            var swedish = Decision("/dl/swedish.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { english, swedish }, _remoteBook, _downloadItem);

            Assert.That(english.Approved, Is.True);
            Assert.That(swedish.Approved, Is.False);
        }

        [Test]
        public void should_prefer_the_metadata_profile_languages_when_set()
        {
            _profiles.Profiles[2] = new MetadataProfile { Id = 2, Name = "Ebook Swedish", AllowedLanguages = "swe, eng" };
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_name_the_metadata_profile_when_it_decides()
        {
            _profiles.Profiles[2] = new MetadataProfile { Id = 2, Name = "Ebook English", AllowedLanguages = "eng, null" };
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "nld" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Rejections.Single().Reason, Does.Contain("Dutch (nld)"));
            Assert.That(decision.Rejections.Single().Reason, Does.Contain("metadata profile 'Ebook English'"));
        }

        [Test]
        public void should_skip_the_check_when_no_wanted_language_is_known()
        {
            foreach (var edition in _book.Editions)
            {
                edition.Language = null;
            }

            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_not_check_manual_imports()
        {
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject().Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, null);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_do_nothing_when_turned_off()
        {
            _reader.Evidence["/dl/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/dl/book.epub", _book, _book.Editions[0]);

            Subject(enabled: false).Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void should_read_a_real_swedish_epub_end_to_end()
        {
            var root = Path.Combine(Path.GetTempPath(), "talearr-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var path = Path.Combine(root, "john.grisham.the.pelican.brief.2021.swedish.retail.epub.ebook-decipher.epub");
                File.WriteAllBytes(path, TestArchives.Epub("sv", "Pelikanfallet"));
                var decision = Decision(path, _book, _book.Editions[0]);
                var subject = new DownloadedFileLanguageVerifier(
                    new FileLanguageReader(LogManager.GetCurrentClassLogger()),
                    (IMetadataProfileService)(object)_profiles,
                    DispatchProxy.Create<IEditionService, ThrowingEditionService>(),
                    TalearrConfigProxy.Create(),
                    LogManager.GetCurrentClassLogger());

                subject.Verify(new List<ImportDecision<LocalBook>> { decision }, _remoteBook, _downloadItem);

                Assert.That(decision.Approved, Is.False);
                Assert.That(decision.Rejections.Single().Reason, Does.StartWith("File language is Swedish (swe) (from EPUB dc:language)"));
            }
            finally
            {
                try
                {
                    Directory.Delete(root, true);
                }
                catch
                {
                }
            }
        }

        private sealed class StubLanguageReader : IFileLanguageReader
        {
            public Dictionary<string, FileLanguageEvidence> Evidence { get; } = new Dictionary<string, FileLanguageEvidence>();

            public FileLanguageEvidence Read(string path, IDictionary<string, List<string>> audioTags = null)
            {
                return Evidence.TryGetValue(path, out var evidence) ? evidence : FileLanguageEvidence.None;
            }
        }

        [Test]
        public void manual_import_should_describe_a_language_mismatch_without_rejecting()
        {
            _reader.Evidence["/old/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");
            var decision = Decision("/old/book.epub", _book, _book.Editions[0]);

            var mismatch = Subject().DescribeMismatch(decision.Item);

            Assert.That(mismatch, Does.Contain("Swedish (swe)"));
            Assert.That(mismatch, Does.Contain("English (eng)"));
            Assert.That(mismatch, Does.Not.Contain("rejected"));
            Assert.That(decision.Approved, Is.True);
        }

        [Test]
        public void manual_import_should_not_describe_a_file_in_the_wanted_language()
        {
            _reader.Evidence["/old/book.epub"] = new FileLanguageEvidence(new[] { "eng" }, "EPUB dc:language");

            Assert.That(Subject().DescribeMismatch(Decision("/old/book.epub", _book, _book.Editions[0]).Item), Is.Null);
        }

        [Test]
        public void manual_import_should_not_describe_when_the_check_is_off()
        {
            _reader.Evidence["/old/book.epub"] = new FileLanguageEvidence(new[] { "swe" }, "EPUB dc:language");

            Assert.That(Subject(enabled: false).DescribeMismatch(Decision("/old/book.epub", _book, _book.Editions[0]).Item), Is.Null);
        }

        public class MetadataProfileServiceProxy : DispatchProxy
        {
            public Dictionary<int, MetadataProfile> Profiles { get; } = new Dictionary<int, MetadataProfile>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IMetadataProfileService.Exists):
                        return Profiles.ContainsKey((int)args[0]);
                    case nameof(IMetadataProfileService.Get):
                        return Profiles[(int)args[0]];
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IMetadataProfileService.{targetMethod?.Name}");
                }
            }
        }

        public class ThrowingEditionService : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement IEditionService.{targetMethod?.Name}");
            }
        }
    }
}
