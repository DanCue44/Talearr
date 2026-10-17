using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Talearr.MetadataFallback;

namespace Chaptarr.Core.Test.Talearr.MetadataFallback
{
    // Real Open Library response shapes (captured 2026-10-17, trimmed).
    internal static class OpenLibraryFixtures
    {
        public const string AuthorSearch = @"{""numFound"":2,""start"":0,""docs"":[
            {""key"":""OL239800A"",""name"":""Truman Capote"",""birth_date"":""30 September 1924"",""death_date"":""25 August 1984"",""top_work"":""In Cold Blood"",""type"":""author"",""work_count"":109},
            {""key"":""OL13976414A"",""name"":""Truman Capote"",""top_work"":""Expertise in Marketing"",""type"":""author"",""work_count"":6},
            {""key"":""OL15458626A"",""name"":""truman capote and mark schorer"",""top_work"":""selected writings"",""type"":""author"",""work_count"":0}]}";

        public const string Author = @"{""name"":""Truman Capote"",
            ""bio"":""**Truman Capote** was an American writer, many of whose short stories, novels, plays, and nonfiction are recognized literary classics."",
            ""birth_date"":""30 September 1924"",""death_date"":""25 August 1984"",
            ""alternate_names"":[""Truman Streckfus Persons""],
            ""personal_name"":""Truman Streckfus Persons"",
            ""remote_ids"":{""isni"":""0000000121326091"",""viaf"":""51686739"",""wikidata"":""Q134180"",""goodreads"":""431149"",""amazon"":""B000APW81E""},
            ""photos"":[14854669,14854668,7402406],
            ""key"":""/authors/OL239800A"",""type"":{""key"":""/type/author""},""revision"":30}";

        public const string AuthorWithBioObject = @"{""name"":""Example Author"",""bio"":{""type"":""/type/text"",""value"":""A bio in an object.""},""key"":""/authors/OL1A""}";

        public const string Works = @"{""numFound"":113,""start"":0,""docs"":[
            {""author_key"":[""OL239800A""],""author_name"":[""Truman Capote""],""cover_i"":228066,""edition_count"":121,
             ""edition_key"":[""OL50551313M"",""OL37757785M""],""first_publish_year"":1965,
             ""isbn"":[""0451154460"",""9780141038391"",""349911764"",""9780679745587""],""key"":""/works/OL1992198W"",
             ""language"":[""jpn"",""ita"",""eng""],""number_of_pages_median"":352,""publisher"":[""Random House N.Y.""],
             ""title"":""In Cold Blood"",""ratings_average"":4.060606,""ratings_count"":99},
            {""author_key"":[""OL239800A""],""author_name"":[""Truman Capote""],""cover_edition_key"":""OL7351911M"",""cover_i"":100108,""edition_count"":113,
             ""edition_key"":[""OL60445140M""],""first_publish_year"":1956,""isbn"":[""0140016074"",""9780679745655""],""key"":""/works/OL1992167W"",
             ""language"":[""eng""],""number_of_pages_median"":160,""publisher"":[""Penguin""],""title"":""Breakfast at Tiffany's"",""ratings_average"":3.875,""ratings_count"":16},
            {""author_key"":[""OL8083348A""],""author_name"":[""Someone Else""],""edition_key"":[""OL1M""],""key"":""/works/OL5W"",""title"":""Not His Book""},
            {""author_key"":[""OL239800A""],""author_name"":[""Truman Capote""],""key"":""/works/OL9W"",""title"":""No Editions""}]}";

        public const string WorkSearch = @"{""numFound"":501,""start"":0,""docs"":[
            {""author_key"":[""OL239800A""],""author_name"":[""Truman Capote""],""cover_i"":228066,""first_publish_year"":1965,
             ""isbn"":[""0451154460"",""9780141038391""],""key"":""/works/OL1992198W"",""title"":""In Cold Blood"",""edition_key"":[""OL50551313M""]}]}";

        public static OpenLibraryAuthorRecord AuthorRecord() => OpenLibraryAuthorRecord.Parse(JToken.Parse(Author));

        public static List<OpenLibraryWorkDoc> WorkDocs() => JToken.Parse(Works)["docs"].Select(OpenLibraryWorkDoc.Parse).Where(w => w != null).ToList();
    }

    internal sealed class FakeOpenLibraryClient : IOpenLibraryClient
    {
        public List<string> Calls { get; } = new();
        public bool Fail { get; set; }

        public List<OpenLibraryAuthorDoc> SearchAuthors(string name, int limit = 10)
        {
            Calls.Add($"SearchAuthors:{name}");
            ThrowIfFailing();
            return JToken.Parse(OpenLibraryFixtures.AuthorSearch)["docs"].Select(OpenLibraryAuthorDoc.Parse).ToList();
        }

        public OpenLibraryAuthorRecord GetAuthor(string authorKey)
        {
            Calls.Add($"GetAuthor:{authorKey}");
            ThrowIfFailing();
            return authorKey == "OL239800A" ? OpenLibraryFixtures.AuthorRecord() : null;
        }

        public List<OpenLibraryWorkDoc> GetAuthorWorks(string authorKey, int limit = 100)
        {
            Calls.Add($"GetAuthorWorks:{authorKey}");
            ThrowIfFailing();
            return OpenLibraryFixtures.WorkDocs();
        }

        public List<OpenLibraryWorkDoc> SearchWorks(string query, int limit = 20)
        {
            Calls.Add($"SearchWorks:{query}");
            ThrowIfFailing();
            return JToken.Parse(OpenLibraryFixtures.WorkSearch)["docs"].Select(OpenLibraryWorkDoc.Parse).ToList();
        }

        private void ThrowIfFailing()
        {
            if (Fail)
            {
                throw new OpenLibraryException("Open Library returned {0} {1}", 503, HttpStatusCode.ServiceUnavailable);
            }
        }
    }

    internal sealed class FakeFallbackSettings : ITalearrMetadataFallbackSettingsService
    {
        public TalearrMetadataFallbackMode Mode { get; set; } = TalearrMetadataFallbackMode.WhenPrimaryDown;

        public TalearrMetadataFallbackMode GetMode() => Mode;

        public TalearrMetadataFallbackMode SaveMode(TalearrMetadataFallbackMode mode)
        {
            Mode = mode;
            return Mode;
        }
    }

    internal sealed class FakeProbe : ITalearrMetadataServerProbe
    {
        public MetadataServerProbeResult LastResult { get; set; } = new MetadataServerProbeResult { State = MetadataServerProbeState.Healthy };

        public MetadataServerProbeResult ProbeNow() => LastResult;
    }

    // Records every call to the fallback; used to prove refresh never reaches it.
    internal sealed class RecordingFallback : ITalearrMetadataFallback
    {
        public List<string> Calls { get; } = new();
        public TalearrMetadataFallbackMode Mode { get; set; } = TalearrMetadataFallbackMode.WhenPrimaryDown;
        public bool IsEnabled => Mode != TalearrMetadataFallbackMode.Off;
        public bool ShowSource => Mode == TalearrMetadataFallbackMode.WhenPrimaryDownShowSource;

        public bool IsPrimaryUnavailable()
        {
            Calls.Add(nameof(IsPrimaryUnavailable));
            return true;
        }

        public bool ShouldUseForAdd(string requestedProviderId, Exception primaryError)
        {
            Calls.Add(nameof(ShouldUseForAdd));
            return true;
        }

        public bool ShouldUseForSearch(Exception searchError)
        {
            Calls.Add(nameof(ShouldUseForSearch));
            return true;
        }

        public NzbDrone.Core.MetadataSource.BookInfo.V5.V5AuthorResponse GetAuthor(string requestedProviderId, string authorNameHint, IReadOnlyDictionary<string, string> requestedWorkTitles = null)
        {
            Calls.Add(nameof(GetAuthor));
            return null;
        }

        public List<object> Search(string term)
        {
            Calls.Add(nameof(Search));
            return new List<object>();
        }

        public TalearrMetadataFallbackStatus GetStatus() => new TalearrMetadataFallbackStatus { Mode = Mode };
    }

    // IConfigService with Hardcover configured; every other member returns its default.
    internal class HardcoverConfigProxy : DispatchProxy
    {
        public bool Enabled { get; set; } = true;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case "get_MetadataServerUrl":
                    return "https://api2.chaptarr.com";
                case "get_HardcoverEnabled":
                    return Enabled;
                case "get_HardcoverApiToken":
                    return Enabled ? "token" : null;
            }

            var returnType = targetMethod?.ReturnType;
            if (returnType == null || returnType == typeof(void))
            {
                return null;
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        public static IConfigService Create(bool enabled = true)
        {
            var config = DispatchProxy.Create<IConfigService, HardcoverConfigProxy>();
            ((HardcoverConfigProxy)(object)config).Enabled = enabled;
            return config;
        }
    }

    internal sealed class FakeHardcoverSearch : NzbDrone.Core.MetadataSource.Hardcover.IHardcoverSearchClient
    {
        public Exception Error { get; set; }
        public int Calls { get; private set; }

        public List<object> Search(string query)
        {
            Calls++;
            if (Error != null)
            {
                throw Error;
            }

            return new List<object>
            {
                new NzbDrone.Core.MetadataSource.Hardcover.HardcoverAuthorResult { Id = "136113", Name = "Truman Capote", Slug = "truman-capote", BooksCount = 210 }
            };
        }
    }

    // Answers every request with one status code; counts requests.
    internal sealed class StatusHttpClient : IHttpClient
    {
        private readonly HttpStatusCode _status;
        private readonly string _content;

        public StatusHttpClient(HttpStatusCode status, string content = "")
        {
            _status = status;
            _content = content;
        }

        public List<HttpRequest> Requests { get; } = new();

        public HttpResponse Get(HttpRequest request)
        {
            Requests.Add(request);
            return new HttpResponse(request, new HttpHeader(), _content, _status);
        }

        public HttpResponse Execute(HttpRequest request) => Get(request);
        public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
        public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
        public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
        public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public Task<HttpResponse> ExecuteAsync(HttpRequest request) => Task.FromResult(Get(request));
        public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
        public Task<HttpResponse> GetAsync(HttpRequest request) => Task.FromResult(Get(request));
        public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
        public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
        public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
    }

    // IProvideAuthorInfo whose primary fetch always fails with the given error, plus the
    // fallback author interface backed by a callback.
    internal sealed class FailingAuthorInfo : IProvideAuthorInfo, ITalearrFallbackAuthorInfo
    {
        private readonly Exception _error;
        private readonly Func<string, Exception, bool> _shouldUse;
        private readonly Func<string, string, Author> _fallback;

        public FailingAuthorInfo(Exception error, Func<string, Exception, bool> shouldUse, Func<string, string, Author> fallback)
        {
            _error = error;
            _shouldUse = shouldUse;
            _fallback = fallback;
        }

        public int FallbackCalls { get; private set; }

        public Author GetAuthorInfo(string chaptarrId, bool useCache = true) => throw _error;

        public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false)
            => throw new InvalidOperationException("not used");

        public bool ShouldUseMetadataFallback(string requestedProviderId, Exception primaryError) => _shouldUse(requestedProviderId, primaryError);

        public Author GetFallbackAuthorInfo(string requestedProviderId, string authorNameHint, IReadOnlyDictionary<string, string> requestedWorkTitles = null)
        {
            FallbackCalls++;
            return _fallback(requestedProviderId, authorNameHint);
        }
    }
}
