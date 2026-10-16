using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.TalearrImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    /// <summary>
    /// The TaleArr archive and language steps wired into DownloadedBooksImportService with real
    /// files on disk: what reaches ImportApprovedBooks, what happens to the work folder, and that
    /// the original download is left alone.
    /// </summary>
    [TestFixture]
    public class DownloadedBooksImportServiceTalearrFixture
    {
        private string _root;
        private string _download;
        private RecordingImportApprovedBooks _importApprovedBooks;
        private IConfigService _config;
        private Book _book;
        private RemoteBook _remoteBook;
        private DownloadClientItem _downloadItem;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-tests", Guid.NewGuid().ToString("N"));
            _download = Path.Combine(_root, "John.Grisham.The.Pelican.Brief.2010.RETAiL.ePub.eBook-GRP");
            Directory.CreateDirectory(_download);

            _importApprovedBooks = new RecordingImportApprovedBooks();
            _config = TalearrConfigProxy.Create();

            var author = new Author { Id = 7, Name = "John Grisham" };
            _book = new Book
            {
                Id = 3091,
                AuthorId = 7,
                Title = "The Pelican Brief",
                MediaType = BookMediaType.Ebook,
                Author = author,
                Editions = new List<Edition> { new Edition { Id = 1, BookId = 3091, Title = "The Pelican Brief", Language = "eng", Monitored = true } }
            };
            _remoteBook = new RemoteBook { Author = author, Books = new List<Book> { _book } };
            _downloadItem = new DownloadClientItem
            {
                DownloadId = "nzb-1",
                Title = Path.GetFileName(_download),
                OutputPath = new OsPath(_download),
                CanMoveFiles = false,
                FilePaths = new List<string>(),
                FileListConfidence = DownloadClientFileListConfidence.Authoritative
            };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        private DownloadedBooksImportService Subject()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var diskProvider = DispatchProxy.Create<IDiskProvider, RealDiskProviderProxy>();

            return new DownloadedBooksImportService(
                diskProvider,
                DispatchProxy.Create<IDiskScanService, ThrowingProxy<IDiskScanService>>(),
                DispatchProxy.Create<IFileMatchingService, NothingMatchesProxy>(),
                DispatchProxy.Create<IMetadataTagService, NoTagsProxy>(),
                _importApprovedBooks,
                DispatchProxy.Create<IBookService, ThrowingProxy<IBookService>>(),
                DispatchProxy.Create<IAuthorService, ThrowingProxy<IAuthorService>>(),
                DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                DispatchProxy.Create<IImportOrchestrator, ThrowingProxy<IImportOrchestrator>>(),
                DispatchProxy.Create<IAuthorLibraryService, ThrowingProxy<IAuthorLibraryService>>(),
                DispatchProxy.Create<IRootFolderService, ThrowingProxy<IRootFolderService>>(),
                _config,
                DispatchProxy.Create<IHistoryService, ThrowingProxy<IHistoryService>>(),
                DispatchProxy.Create<IEventAggregator, ThrowingProxy<IEventAggregator>>(),
                DispatchProxy.Create<IRuntimeInfo, ThrowingProxy<IRuntimeInfo>>(),
                DispatchProxy.Create<IMediaInfoExtractor, ThrowingProxy<IMediaInfoExtractor>>(),
                logger,
                new DownloadArchiveUnpacker(FreeSpaceDiskProviderProxy.Create(), _config, logger),
                new DownloadedFileLanguageVerifier(
                    new FileLanguageReader(logger),
                    DispatchProxy.Create<IMetadataProfileService, NoProfilesProxy>(),
                    DispatchProxy.Create<IEditionService, ThrowingProxy<IEditionService>>(),
                    _config,
                    logger));
        }

        private void WriteSceneRelease(string language)
        {
            var rar = TestArchives.Rar(("john.grisham.the.pelican.brief.retail.epub.ebook-grp.epub", TestArchives.Epub(language)));
            File.WriteAllBytes(Path.Combine(_download, "grp1b.zip"), TestArchives.Zip(("grp1b.rar", rar), ("GRP.nfo", TestArchives.Text("nfo"))));
            File.WriteAllText(Path.Combine(_download, "grp.nfo"), "nfo");
            _downloadItem.FilePaths = Directory.GetFiles(_download).ToList();
        }

        [Test]
        public void should_import_the_book_from_a_nested_archive_and_remove_the_work_folder()
        {
            WriteSceneRelease("en");
            var originals = Directory.GetFiles(_download).ToDictionary(f => f, File.ReadAllBytes);

            Subject().ProcessPath(_download, ImportMode.Copy, null, _downloadItem, _remoteBook);

            var call = _importApprovedBooks.Calls.Single();
            var workFolder = DownloadArchiveUnpacker.GetWorkFolder(_download, true);
            Assert.That(call.Decisions.Single().Item.Path, Does.StartWith(workFolder + Path.DirectorySeparatorChar));
            Assert.That(call.Decisions.Single().Item.Path, Does.EndWith(".epub"));

            // Our copy is moved out of the work folder; the download client item is untouched.
            Assert.That(call.ImportMode, Is.EqualTo(ImportMode.Move));
            Assert.That(call.DownloadClientItem.OutputPath.FullPath, Is.EqualTo(workFolder));
            Assert.That(call.DownloadClientItem.FilePaths, Is.Null);
            Assert.That(call.DownloadClientItem.DownloadId, Is.EqualTo("nzb-1"));
            Assert.That(_downloadItem.OutputPath.FullPath, Is.EqualTo(_download));
            Assert.That(_downloadItem.CanMoveFiles, Is.False);

            Assert.That(Directory.Exists(workFolder), Is.False);
            Assert.That(Directory.GetFiles(_download).ToDictionary(f => f, File.ReadAllBytes), Is.EqualTo(originals));
        }

        [Test]
        public void should_reject_a_swedish_book_found_inside_archives_for_its_language()
        {
            WriteSceneRelease("sv");

            Subject().ProcessPath(_download, ImportMode.Copy, null, _downloadItem, _remoteBook);

            var decision = _importApprovedBooks.Calls.Single().Decisions.Single();
            Assert.That(decision.Rejections.Any(r => r.IsLanguageFilter), Is.True);
            Assert.That(decision.Rejections.First(r => r.IsLanguageFilter).Reason, Does.Contain("Swedish (swe)"));
        }

        [Test]
        public void should_keep_the_work_folder_while_a_conversion_is_pending()
        {
            WriteSceneRelease("en");
            _importApprovedBooks.ResultType = ImportResultType.Pending;

            Subject().ProcessPath(_download, ImportMode.Copy, null, _downloadItem, _remoteBook);

            Assert.That(Directory.Exists(DownloadArchiveUnpacker.GetWorkFolder(_download, true)), Is.True);
        }

        [Test]
        public void should_report_an_archive_that_cannot_be_unpacked()
        {
            File.WriteAllBytes(Path.Combine(_download, "broken.zip"), new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 });
            _downloadItem.FilePaths = Directory.GetFiles(_download).ToList();

            var results = Subject().ProcessPath(_download, ImportMode.Copy, null, _downloadItem, _remoteBook);

            Assert.That(results, Has.Count.EqualTo(1));
            Assert.That(results[0].Result, Is.EqualTo(ImportResultType.Rejected));
            Assert.That(results[0].Errors.Single(), Does.StartWith("TaleArr could not unpack the archives in"));
            Assert.That(_importApprovedBooks.Calls, Is.Empty);
        }

        [Test]
        public void should_not_unpack_when_turned_off()
        {
            WriteSceneRelease("en");
            ((TalearrConfigProxy)(object)_config).UnpackArchives = false;

            var results = Subject().ProcessPath(_download, ImportMode.Copy, null, _downloadItem, _remoteBook);

            Assert.That(_importApprovedBooks.Calls, Is.Empty);
            Assert.That(results.Single().Errors.Single(), Does.Contain("No supported audio or ebook files"));
        }

        private sealed class RecordingImportApprovedBooks : IImportApprovedBooks
        {
            public List<(List<ImportDecision<LocalBook>> Decisions, DownloadClientItem DownloadClientItem, ImportMode ImportMode)> Calls { get; } = new();
            public ImportResultType? ResultType { get; set; }

            public List<ImportResult> Import(List<ImportDecision<LocalBook>> decisions, bool replaceExisting, DownloadClientItem downloadClientItem = null, ImportMode importMode = ImportMode.Auto, CancellationToken cancellationToken = default)
            {
                Calls.Add((decisions, downloadClientItem, importMode));
                return decisions
                    .Select(d => ResultType.HasValue
                        ? new ImportResult(d, ResultType.Value)
                        : new ImportResult(d, d.Rejections.Select(r => r.Reason).ToArray()))
                    .ToList();
            }
        }

        public class RealDiskProviderProxy : DispatchProxy
        {
            private static readonly FileSystem FileSystem = new FileSystem();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IDiskProvider.FolderExists):
                        return Directory.Exists((string)args[0]);
                    case nameof(IDiskProvider.FileExists):
                        return File.Exists((string)args[0]);
                    case nameof(IDiskProvider.GetDirectoryInfo):
                        return FileSystem.DirectoryInfo.FromDirectoryName((string)args[0]);
                    case nameof(IDiskProvider.GetFileInfo):
                        return FileSystem.FileInfo.FromFileName((string)args[0]);
                    case nameof(IDiskProvider.GetFileSize):
                        return new FileInfo((string)args[0]).Length;
                    case nameof(IDiskProvider.FileGetLastWrite):
                        return File.GetLastWriteTimeUtc((string)args[0]);
                    case nameof(IDiskProvider.GetFileInfos):
                        var option = args.Length > 1 && args[1] is true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        return Directory.EnumerateFiles((string)args[0], "*", option)
                            .Select(file => FileSystem.FileInfo.FromFileName(file))
                            .Cast<IFileInfo>()
                            .ToList();
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
                }
            }
        }

        public class NothingMatchesProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IFileMatchingService.MatchFilesToLibraryAsync))
                {
                    var files = (DiscoveredFileWithMetadata[])args[0];
                    return Task.FromResult(new FileMatchResult
                    {
                        UnmatchedFiles = files.Select(file => new UnmatchedFile { File = file, Reason = "No match in local library" }).ToArray()
                    });
                }

                throw new NotImplementedException($"Test proxy does not implement IFileMatchingService.{targetMethod?.Name}");
            }
        }

        public class NoTagsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataTagService.ReadAllTagsAndDuration))
                {
                    return (new Dictionary<string, List<string>>(), (int?)null);
                }

                throw new NotImplementedException($"Test proxy does not implement IMetadataTagService.{targetMethod?.Name}");
            }
        }

        public class NoProfilesProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IMetadataProfileService.Exists))
                {
                    return false;
                }

                throw new NotImplementedException($"Test proxy does not implement IMetadataProfileService.{targetMethod?.Name}");
            }
        }

        public class ThrowingProxy<T> : DispatchProxy
            where T : class
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod?.Name}");
            }
        }
    }
}
