using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace Chaptarr.Core.Test.Talearr
{
    [TestFixture]
    [NonParallelizable]
    public class TalearrMetadataOutageFixture
    {
        private const string Source = "https://api2.chaptarr.com";

        // Records every call; returns default values. Used where a test must prove that
        // nothing was written.
        public class RecordingProxy<T> : DispatchProxy
        {
            public List<string> Calls { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                Calls.Add(targetMethod.Name);

                var returnType = targetMethod.ReturnType;
                if (returnType == typeof(void))
                {
                    return null;
                }

                if (returnType == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
            }
        }

        private sealed class OutageHttpClient : IHttpClient
        {
            public int Requests { get; private set; }

            public HttpResponse Get(HttpRequest request)
            {
                Requests++;
                return new HttpResponse(request, new HttpHeader(), "bad gateway", HttpStatusCode.BadGateway);
            }

            public HttpResponse Execute(HttpRequest request) => Get(request);
            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => Task.FromResult(Get(request));
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private sealed class UnavailableAuthorInfo : IProvideAuthorInfo
        {
            public int RefreshCalls { get; private set; }
            public int GetCalls { get; private set; }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true)
            {
                GetCalls++;
                throw new InvalidOperationException("must not fall back to a second request during an outage");
            }

            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false)
            {
                RefreshCalls++;
                return RefreshResult.Unavailable("Refresh failed: Metadata server is temporarily unavailable");
            }
        }

        private MemoryTarget _memory;
        private LogFactory _logFactory;

        [SetUp]
        public void SetUp()
        {
            TalearrMetadataOutageLog.Reset(Source);

            _memory = new MemoryTarget { Layout = "${level}|${message}" };
            var configuration = new LoggingConfiguration();
            configuration.AddRuleForAllLevels(_memory);
            _logFactory = new LogFactory { Configuration = configuration };
        }

        [TearDown]
        public void TearDown()
        {
            TalearrMetadataOutageLog.Reset(Source);
            _logFactory.Dispose();
        }

        private List<string> Logs(string level) => _memory.Logs.Where(l => l.StartsWith(level + "|", StringComparison.Ordinal)).ToList();

        private static (MetadataServerHealthService Service, MetadataServerHealthGate Gate) CreateGate(Logger logger)
        {
            var service = new MetadataServerHealthService(logger);
            var gate = new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), service, logger);
            return (service, gate);
        }

        [Test]
        public void outage_logs_one_warning_then_debug_and_recovery_starts_a_new_window()
        {
            var logger = _logFactory.GetLogger("outage");
            var (service, gate) = CreateGate(logger);

            Assert.That(TalearrMetadataOutageLog.TryLog(gate, logger, "refresh of author hc:1", "boom"), Is.False,
                "while the server is healthy the caller logs as usual");

            service.ReportFailure(Source, new WebException("connect failed", WebExceptionStatus.ConnectFailure));
            _memory.Logs.Clear();

            for (var i = 0; i < 50; i++)
            {
                Assert.That(TalearrMetadataOutageLog.TryLog(gate, logger, $"refresh of author hc:{i}", "HTTP 502"), Is.True);
            }

            Assert.That(Logs("Warn"), Has.Count.EqualTo(1));
            Assert.That(Logs("Warn")[0], Does.Contain("Keeping existing authors and books unchanged"));
            Assert.That(Logs("Error"), Is.Empty);
            Assert.That(TalearrMetadataOutageLog.GetSuppressedCount(Source), Is.EqualTo(49));

            service.ReportSuccess(Source);
            Assert.That(_memory.Logs.Any(l => l.Contains("50 metadata request(s) were skipped")), Is.True);

            service.ReportFailure(Source, new WebException("connect failed", WebExceptionStatus.ConnectFailure));
            _memory.Logs.Clear();

            TalearrMetadataOutageLog.TryLog(gate, logger, "refresh of author hc:1", "HTTP 502");
            Assert.That(Logs("Warn"), Has.Count.EqualTo(1), "a new outage gets its own warning");
        }

        [Test]
        public void book_info_refresh_during_outage_returns_unavailable_without_error_spam()
        {
            var logger = _logFactory.GetLogger("bookinfo");
            var (_, gate) = CreateGate(logger);
            var http = new OutageHttpClient();
            var proxy = new BookInfoProxy(http,
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: ConfigServiceTestProxy.Create(),
                requestBuilder: null,
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: gate);

            var results = Enumerable.Range(1, 20)
                .Select(i => proxy.RefreshAuthorInfo($"hc:{i}"))
                .ToList();

            Assert.That(http.Requests, Is.EqualTo(1), "the open circuit stops further requests");
            Assert.That(results.All(r => r.Reason == RefreshReason.Error && r.MetadataServerUnavailable), Is.True);
            Assert.That(results.All(r => r.Author == null), Is.True);
            Assert.That(Logs("Error"), Is.Empty, "an outage must not log an error per author");
            Assert.That(Logs("Warn").Count(l => l.Contains("Keeping existing authors and books unchanged")), Is.EqualTo(1));
        }

        [Test]
        public void author_refresh_during_outage_keeps_existing_books_and_monitoring()
        {
            var logger = _logFactory.GetLogger("refresh");
            var authorInfo = new UnavailableAuthorInfo();
            var bookService = DispatchProxy.Create<IBookService, RecordingProxy<IBookService>>();
            var editionService = DispatchProxy.Create<IEditionService, RecordingProxy<IEditionService>>();
            var authorService = DispatchProxy.Create<IAuthorService, RecordingProxy<IAuthorService>>();
            var syncMetadata = DispatchProxy.Create<IAuthorSyncMetadataService, RecordingProxy<IAuthorSyncMetadataService>>();

            var service = new RefreshAuthorService(authorInfo,
                authorService,
                bookService,
                editionService,
                metadataProfileService: null,
                refreshBookService: null,
                refreshSeriesService: null,
                eventAggregator: null,
                commandQueueManager: null,
                mediaFileService: null,
                historyService: null,
                rootFolderService: null,
                checkIfAuthorShouldBeRefreshed: null,
                monitorNewBookService: null,
                configService: null,
                importListExclusionService: null,
                syncMetadataService: syncMetadata,
                syncQueueService: null,
                rootFolderSettingsResolver: null,
                logger: logger);

            var author = new Author
            {
                Id = 7,
                Name = "Kept Author",
                HardcoverAuthorId = "hc:7",
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None
            };

            var getSkyhookData = typeof(RefreshAuthorService).GetMethod("GetSkyhookData", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(getSkyhookData, Is.Not.Null);

            var remote = getSkyhookData.Invoke(service, new object[] { author, false, null, false, null });

            Assert.That(remote, Is.Null, "no remote data means the refresh is skipped, not applied as an empty catalog");
            Assert.That(authorInfo.RefreshCalls, Is.EqualTo(1));
            Assert.That(authorInfo.GetCalls, Is.Zero);
            Assert.That(((RecordingProxy<IBookService>)(object)bookService).Calls, Is.Empty, "books are not deleted, updated or unmonitored");
            Assert.That(((RecordingProxy<IEditionService>)(object)editionService).Calls, Is.Empty);
            Assert.That(((RecordingProxy<IAuthorService>)(object)authorService).Calls, Is.Empty, "author monitoring is not touched");
            Assert.That(((RecordingProxy<IAuthorSyncMetadataService>)(object)syncMetadata).Calls, Does.Contain(nameof(IAuthorSyncMetadataService.UpdateSyncResult)),
                "the failed attempt is still recorded so the refresh is retried later");
            Assert.That(author.AudiobookMonitored, Is.True);
            Assert.That(Logs("Error"), Is.Empty);
        }
    }
}
