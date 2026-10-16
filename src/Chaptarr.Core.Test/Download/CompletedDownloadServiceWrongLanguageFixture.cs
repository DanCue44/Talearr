using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Download
{
    /// <summary>
    /// TaleArr: a completed download whose files are in the wrong language is a bad grab. It is
    /// marked failed the normal way (blocklist + replacement search) instead of sitting in the
    /// queue, unless the user forced the grab.
    /// </summary>
    [TestFixture]
    public class CompletedDownloadServiceWrongLanguageFixture
    {
        private const string LanguageReason = "File language is Swedish (swe) (from EPUB dc:language), but English (eng) is wanted (editions of 'The Pelican Brief'). TaleArr rejected this release so another one can be tried.";

        [Test]
        public void should_detect_a_payload_rejected_for_language()
        {
            var results = new List<ImportResult> { BuildResult(Quality.EPUB, LanguageRejection()) };

            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(results), Is.True);
        }

        [Test]
        public void should_detect_language_beside_quality_gated_files()
        {
            var results = new List<ImportResult>
            {
                BuildResult(Quality.EPUB, LanguageRejection()),
                BuildResult(Quality.PDF, new Rejection("Quality 'PDF' not allowed", RejectionType.Permanent, canBypass: false, category: "Quality", severity: 3))
            };

            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(results), Is.True);
        }

        [Test]
        public void should_detect_language_on_a_file_that_also_failed_to_match()
        {
            var results = new List<ImportResult> { BuildResult(Quality.EPUB, new Rejection("No match in local library"), LanguageRejection()) };

            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(results), Is.True);
        }

        [Test]
        public void should_not_treat_a_download_that_imported_something_as_wrong_language()
        {
            var results = new List<ImportResult>
            {
                BuildResult(Quality.EPUB),
                BuildResult(Quality.MOBI, LanguageRejection())
            };

            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(results), Is.False);
        }

        [Test]
        public void should_not_treat_other_failures_as_wrong_language()
        {
            var results = new List<ImportResult>
            {
                BuildResult(Quality.EPUB, LanguageRejection()),
                BuildResult(Quality.MOBI, new Rejection("Author not found"))
            };

            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(results), Is.False);
            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(new List<ImportResult>()), Is.False);
            Assert.That(CompletedDownloadService.IsWrongLanguagePayload(null), Is.False);
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_mark_a_wrong_language_download_failed_with_the_reason()
        {
            var harness = new Harness();
            harness.ImportResults = new List<ImportResult> { BuildResult(Quality.EPUB, LanguageRejection()) };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.FailedDownloadService.Failures, Has.Count.EqualTo(1));
            Assert.That(harness.FailedDownloadService.Failures[0].Reason, Is.EqualTo(LanguageReason));
            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.DownloadFailed));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_block_instead_of_failing_a_forced_grab()
        {
            var harness = new Harness();
            harness.TrackedDownload.DownloadItem.DownloadForced = true;
            harness.ImportResults = new List<ImportResult> { BuildResult(Quality.EPUB, LanguageRejection()) };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.FailedDownloadService.Failures, Is.Empty);
            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.EqualTo(LanguageReason));
        }

        [Test, Platform(Exclude = "Win", Reason = "Test uses Unix paths")]
        public void should_block_with_the_reason_when_the_download_cannot_be_failed()
        {
            var harness = new Harness();
            harness.FailedDownloadService.CanFail = false;
            harness.ImportResults = new List<ImportResult> { BuildResult(Quality.EPUB, LanguageRejection()) };

            harness.Subject.Import(harness.TrackedDownload);

            Assert.That(harness.TrackedDownload.State, Is.EqualTo(TrackedDownloadState.ImportBlocked));
            Assert.That(harness.TrackedDownload.StatusMessages.SelectMany(m => m.Messages), Has.Some.EqualTo(LanguageReason));
        }

        private sealed class Harness
        {
            public RecordingEventAggregator EventAggregator { get; } = new();
            public RecordingFailedDownloadService FailedDownloadService { get; } = new();
            public TrackedDownload TrackedDownload { get; }
            public CompletedDownloadService Subject { get; }

            public List<ImportResult> ImportResults
            {
                get => _importService.Results;
                set => _importService.Results = value;
            }

            private readonly RecordingDownloadedBooksImportService _importService = new();

            public Harness()
            {
                var author = new Author { Id = 7, Name = "Katee Robert" };
                var book = new Book { Id = 901, AuthorId = 7, Title = "Learn My Lesson", MediaType = BookMediaType.Ebook, AnyEditionOk = true };

                TrackedDownload = new TrackedDownload
                {
                    DownloadClient = 1,
                    State = TrackedDownloadState.ImportPending,
                    DownloadItem = new DownloadClientItem
                    {
                        DownloadId = "learn-my-lesson",
                        Title = "Learn My Lesson, epub, please...thanks - Learn My Lesson.mobi",
                        OutputPath = new OsPath("/downloads/learn-my-lesson")
                    },
                    RemoteBook = new RemoteBook
                    {
                        Author = author,
                        Books = new List<Book> { book },
                        ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MOBI) }
                    }
                };

                var historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
                ((HistoryServiceProxy)(object)historyService).HistoryItems = new List<EntityHistory>
                {
                    new()
                    {
                        DownloadId = "learn-my-lesson",
                        EventType = EntityHistoryEventType.Grabbed,
                        AuthorId = 7,
                        BookId = 901,
                        Date = DateTime.UtcNow.AddMinutes(-10)
                    }
                };

                Subject = new CompletedDownloadService(
                    EventAggregator,
                    historyService,
                    new StubProvideImportItemService("/downloads/learn-my-lesson"),
                    _importService,
                    new PassthroughDownloadImportModeResolver(),
                    new StubTrackedDownloadAlreadyImported(),
                    FailedDownloadService,
                    LogManager.GetCurrentClassLogger(),
                    NoopDownloadClientFileSnapshotService.Instance);
            }
        }

        private sealed class RecordingFailedDownloadService : IFailedDownloadService
        {
            public List<(TrackedDownload Download, string Reason)> Failures { get; } = new();

            public bool CanFail { get; set; } = true;

            public void MarkAsFailed(TrackedDownload trackedDownload, string reason, bool skipRedownload = false)
            {
                if (!CanFail)
                {
                    // Mirrors FailedDownloadService when there is no grab history to fail.
                    return;
                }

                Failures.Add((trackedDownload, reason));
                trackedDownload.State = TrackedDownloadState.DownloadFailed;
            }

            public void MarkAsFailed(int historyId, bool skipRedownload = false) => throw new NotImplementedException();
            public void MarkAsFailed(string downloadId, bool skipRedownload = false) => throw new NotImplementedException();
            public void Check(TrackedDownload trackedDownload) => throw new NotImplementedException();
            public void ProcessFailed(TrackedDownload trackedDownload) => throw new NotImplementedException();
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private sealed class StubProvideImportItemService : IProvideImportItemService
        {
            private readonly OsPath _outputPath;

            public StubProvideImportItemService(string outputPath)
            {
                _outputPath = new OsPath(outputPath);
            }

            public DownloadClientItem ProvideImportItem(DownloadClientItem item, DownloadClientItem previousImportAttempt)
            {
                var clone = item.Clone();
                clone.OutputPath = _outputPath;
                return clone;
            }
        }

        private sealed class RecordingDownloadedBooksImportService : IDownloadedBooksImportService
        {
            public List<ImportResult> Results { get; set; } = new();

            public List<ImportResult> ProcessPath(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => Results;

            public List<ImportResult> ProcessFolder(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();

            public List<ImportResult> ProcessFile(string path, ImportMode importMode = ImportMode.Auto, Author author = null, DownloadClientItem downloadClientItem = null, RemoteBook remoteBook = null, bool requireDefaultRootFolderForMissingAuthors = false) => throw new NotImplementedException();
        }

        private sealed class PassthroughDownloadImportModeResolver : IDownloadImportModeResolver
        {
            public ImportMode Resolve(ImportMode requestedMode, DownloadClientItem downloadClientItem) => requestedMode;
            public DownloadImportPolicy ResolvePolicy(ImportMode requestedMode, DownloadClientItem downloadClientItem) => new(requestedMode, false);
            public bool ShouldPreserveDownloadClientItem(DownloadClientItem downloadClientItem) => false;
        }

        private sealed class StubTrackedDownloadAlreadyImported : ITrackedDownloadAlreadyImported
        {
            public bool IsImported(TrackedDownload trackedDownload, List<EntityHistory> historyItems) => false;
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> HistoryItems { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IHistoryService.FindByDownloadId))
                {
                    var downloadId = (string)args[0];
                    return HistoryItems.Where(h => h.DownloadId == downloadId).ToList();
                }

                if (targetMethod?.Name == nameof(IHistoryService.MostRecentForDownloadId))
                {
                    return HistoryItems.OrderByDescending(h => h.Date).FirstOrDefault();
                }

                throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        private static Rejection LanguageRejection()
        {
            return new Rejection(LanguageReason, RejectionType.Permanent, canBypass: false, category: "Language", severity: 3);
        }

        private static ImportResult BuildResult(Quality quality, params Rejection[] rejections)
        {
            var imported = rejections.Length == 0;
            var decision = new ImportDecision<LocalBook>(new LocalBook
            {
                Path = $"/downloads/book.{quality.Name.ToLowerInvariant()}",
                Quality = new QualityModel(quality),
                Book = imported
                    ? new Book { Id = 901, AuthorId = 7, Title = "Learn My Lesson", MediaType = BookMediaType.Ebook }
                    : null,
                Author = imported ? new Author { Id = 7, Name = "Katee Robert" } : null
            });

            if (imported)
            {
                return new ImportResult(decision);
            }

            foreach (var rejection in rejections)
            {
                decision.Reject(rejection);
            }

            return new ImportResult(decision, rejections.Select(r => r.Reason).ToArray());
        }
    }
}
