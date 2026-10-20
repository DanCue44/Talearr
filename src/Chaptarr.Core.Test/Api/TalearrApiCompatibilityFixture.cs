using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Chaptarr.Api.V1.Author;
using Chaptarr.Api.V1.Books;
using Chaptarr.Api.V1.Commands;
using Chaptarr.Api.V1.History;
using Chaptarr.Api.V1.Indexers;
using Chaptarr.Api.V1.Queue;
using Chaptarr.Api.V1.System;
using Chaptarr.Http;
using Chaptarr.Http.ClientSchema;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Api
{
    // Talearr: pins the parts of the v1 API that Prowlarr's "Readarr" app sync, Allseerr and
    // other Readarr clients read. A failure here means an outside client breaks: change the
    // client contract on purpose (and docs/README) or put the field/route back.
    [TestFixture]
    public class TalearrApiCompatibilityFixture
    {
        private static HashSet<string> JsonNames(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
                .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .ToHashSet();
        }

        private static void AssertHasFields(Type type, params string[] names)
        {
            var actual = JsonNames(type);
            var missing = names.Where(n => !actual.Contains(n)).ToList();

            Assert.That(missing, Is.Empty, $"{type.Name} lost fields clients read: {string.Join(", ", missing)}");
        }

        private static readonly Regex RouteParameter = new Regex(@"\{[^}]*\}", RegexOptions.Compiled);

        [SetUp]
        public void SetUp()
        {
            typeof(SchemaBuilder)
                .GetField("_localizationService", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, new PassThroughLocalizationService());
        }

        private static HashSet<string> Routes()
        {
            var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var controllers = typeof(BookController).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && t.GetCustomAttribute<V1ApiControllerAttribute>() != null);

            foreach (var controller in controllers)
            {
                var resource = controller.GetCustomAttribute<V1ApiControllerAttribute>().Resource;

                if (resource == VersionedApiControllerAttribute.CONTROLLER_RESOURCE)
                {
                    resource = controller.Name.Substring(0, controller.Name.Length - "Controller".Length);
                }

                foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (var attribute in method.GetCustomAttributes(true))
                    {
                        if (attribute is not IActionHttpMethodProvider http)
                        {
                            continue;
                        }

                        var template = (attribute as IRouteTemplateProvider)?.Template;
                        var path = string.IsNullOrWhiteSpace(template) ? resource : $"{resource}/{template}";

                        // Route parameters compare by position only: {id:int?} and {id} are the same URL.
                        path = RouteParameter.Replace(path, "{}");

                        foreach (var verb in http.HttpMethods)
                        {
                            routes.Add($"{verb} {path.ToLowerInvariant()}");
                        }
                    }
                }
            }

            return routes;
        }

        // Prowlarr ReadarrV1Proxy and common Readarr clients (Allseerr, dashboards, LunaSea).
        [TestCase("GET system/status")]
        [TestCase("GET indexer")]
        [TestCase("GET indexer/{}")]
        [TestCase("GET indexer/schema")]
        [TestCase("POST indexer")]
        [TestCase("PUT indexer/{}")]
        [TestCase("DELETE indexer/{}")]
        [TestCase("POST indexer/test")]
        [TestCase("GET book")]
        [TestCase("GET book/{}")]
        [TestCase("POST book")]
        [TestCase("PUT book/{}")]
        [TestCase("PUT book/monitor")]
        [TestCase("GET book/lookup")]
        [TestCase("GET author")]
        [TestCase("GET author/{}")]
        [TestCase("POST author")]
        [TestCase("PUT author/{}")]
        [TestCase("DELETE author/{}")]
        [TestCase("GET author/lookup")]
        [TestCase("GET queue")]
        [TestCase("GET queue/status")]
        [TestCase("GET wanted/missing")]
        [TestCase("GET wanted/cutoff")]
        [TestCase("GET calendar")]
        [TestCase("GET command")]
        [TestCase("POST command")]
        [TestCase("GET history")]
        [TestCase("GET history/author")]
        public void readarr_route_exists(string route)
        {
            Assert.That(Routes(), Does.Contain(route), $"route {route} is gone; registered routes: {string.Join(", ", Routes().Where(r => r.Contains(route.Split(' ')[1].Split('/')[0])))}");
        }

        [Test]
        public void system_status_keeps_the_chaptarr_app_name_and_a_parsable_version()
        {
            // Allseerr tells Chaptarr from Readarr by appName; Prowlarr reads X-Application-Version.
            Assert.That(BuildInfo.AppName, Is.EqualTo("Chaptarr"));
            Assert.That(BuildInfo.Version, Is.Not.Null);
            Assert.That(Version.TryParse(BuildInfo.Version.ToString(), out _), Is.True);
            AssertHasFields(typeof(SystemResource), "appName", "instanceName", "version", "buildTime", "isDocker", "urlBase", "startTime", "branch", "osName");
        }

        [Test]
        public void prowlarr_newznab_schema_fields()
        {
            var names = SchemaBuilder.ToSchema(new NewznabSettings()).Select(f => f.Name).ToList();

            Assert.That(names, Is.SupersetOf(new[] { "baseUrl", "apiPath", "apiKey", "categories", "earlyReleaseLimit", "additionalParameters" }));
        }

        [Test]
        public void prowlarr_torznab_schema_fields()
        {
            var names = SchemaBuilder.ToSchema(new TorznabSettings()).Select(f => f.Name).ToList();

            Assert.That(names, Is.SupersetOf(new[]
            {
                "baseUrl", "apiPath", "apiKey", "categories", "minimumSeeders",
                "seedCriteria.seedRatio", "seedCriteria.seedTime", "seedCriteria.discographySeedTime",
                "rejectBlocklistedTorrentHashesWhileGrabbing"
            }));
        }

        [Test]
        public void prowlarr_picks_schemas_by_implementation_and_config_contract()
        {
            Assert.That(typeof(Newznab).Name, Is.EqualTo("Newznab"));
            Assert.That(typeof(Torznab).Name, Is.EqualTo("Torznab"));
            Assert.That(typeof(NewznabSettings).Name, Is.EqualTo("NewznabSettings"));
            Assert.That(typeof(TorznabSettings).Name, Is.EqualTo("TorznabSettings"));
            AssertHasFields(typeof(IndexerResource), "id", "name", "fields", "implementation", "implementationName", "configContract", "tags",
                "enableRss", "enableAutomaticSearch", "enableInteractiveSearch", "priority", "downloadClientId", "protocol", "supportsRss", "supportsSearch");
        }

        [Test]
        public void default_book_categories_cover_audiobooks_and_ebooks()
        {
            Assert.That(new NewznabSettings().Categories, Is.SupersetOf(new[] { 3030, 7020 }));
            Assert.That(new TorznabSettings().Categories, Is.SupersetOf(new[] { 3030, 7020 }));
        }

        // Prowlarr's default Readarr sync categories, and MAM's mapping (AudioBooks 3030,
        // E-Books 7020, Musicology and Radio under the Audio parent 3000).
        [TestCase(new[] { 7000, 3030, 7050, 7020, 7030, 7010, 7040 }, BookMediaType.Audiobook, new[] { 3030 })]
        [TestCase(new[] { 7000, 3030, 7050, 7020, 7030, 7010, 7040 }, BookMediaType.Ebook, new[] { 7000, 7050, 7020, 7030, 7010, 7040 })]
        [TestCase(new[] { 3000, 3030, 7020 }, BookMediaType.Audiobook, new[] { 3000, 3030 })]
        [TestCase(new[] { 3030, 7020, 8010 }, BookMediaType.Ebook, new[] { 7020 })]
        public void synced_categories_route_by_media_type(int[] configured, BookMediaType mediaType, int[] expected)
        {
            Assert.That(SearchMediaTypeHelper.FilterCategoriesForMediaType(configured, mediaType), Is.EqualTo(expected));
        }

        [Test]
        public void a_release_in_an_audio_category_without_a_format_is_unknown_audio()
        {
            Assert.That(QualityParser.ParseQuality("Frank Herbert - Dune", null, new List<int> { 3030, 100013 }).Quality, Is.EqualTo(Quality.UnknownAudio));
            Assert.That(QualityParser.ParseQuality("Frank Herbert - Dune", null, new List<int> { 7020 }).Quality, Is.EqualTo(Quality.Unknown));
        }

        [TestCase(13, 39, new[] { 3030, 100039 })]
        [TestCase(14, 63, new[] { 7020, 100063 })]
        [TestCase(16, 127, new[] { 3000, 100127 })]
        [TestCase(15, 19, new[] { 100019 })]
        public void native_mam_releases_carry_newznab_categories(int mainCategory, int siteCategory, int[] expected)
        {
            // Same shape Prowlarr gives MAM results, so MAM and Prowlarr releases rank alike.
            Assert.That(MyAnonaMouseJsonParser.ToNewznabCategories(mainCategory, siteCategory), Is.EqualTo(expected));
        }

        [Test]
        public void native_mam_audiobook_without_a_format_is_unknown_audio()
        {
            var categories = MyAnonaMouseJsonParser.ToNewznabCategories(13, 39);

            Assert.That(QualityParser.ParseQuality("Frank Herbert - Dune", null, categories).Quality, Is.EqualTo(Quality.UnknownAudio));
            Assert.That(MyAnonaMouseJsonParser.ToNewznabCategories(null, null), Is.Null);
        }

        [Test]
        public void book_resource_fields()
        {
            // Readarr fields plus the Chaptarr per-format fields Allseerr reads.
            AssertHasFields(typeof(BookResource),
                "id", "title", "titleSlug", "overview", "authorId", "author", "foreignBookId", "foreignEditionId", "monitored",
                "anyEditionOk", "ratings", "releaseDate", "pageCount", "genres", "images", "links", "statistics", "added",
                "addOptions", "remoteCover", "editions", "seriesTitle", "authorTitle",
                "mediaType", "ebookMonitored", "audiobookMonitored", "hasFiles", "localEbookBooks", "localAudiobookBooks");
            AssertHasFields(typeof(BookStatisticsResource), "bookFileCount", "sizeOnDisk");
            AssertHasFields(typeof(EditionResource), "foreignEditionId", "title", "isbn13", "language", "monitored");
        }

        [Test]
        public void book_media_type_is_a_lowercase_string()
        {
            var json = STJson.ToJson(new Book { Title = "Dune", MediaType = BookMediaType.Audiobook }.ToResource());

            Assert.That(json, Does.Match("\"mediaType\":\\s*\"audiobook\""));
        }

        [Test]
        public void author_resource_fields()
        {
            AssertHasFields(typeof(AuthorResource),
                "id", "authorName", "authorNameLastFirst", "foreignAuthorId", "titleSlug", "overview", "path", "monitored",
                "qualityProfileId", "metadataProfileId", "rootFolderPath", "monitorNewItems", "tags", "added", "addOptions",
                "statistics", "images", "links", "status", "ended", "sortName", "lastBook", "nextBook", "genres", "ratings",
                "ebookTags", "audiobookTags");
        }

        [Test]
        public void queue_resource_fields()
        {
            AssertHasFields(typeof(QueueResource),
                "id", "authorId", "bookId", "author", "book", "quality", "size", "title", "sizeleft", "timeleft",
                "estimatedCompletionTime", "status", "trackedDownloadStatus", "trackedDownloadState", "statusMessages",
                "errorMessage", "downloadId", "protocol", "downloadClient", "indexer", "outputPath");
            AssertHasFields(typeof(QueueStatusResource), "totalCount", "count", "unknownCount", "errors", "warnings", "unknownErrors", "unknownWarnings");
            AssertHasFields(typeof(PagingResource<QueueResource>), "page", "pageSize", "sortKey", "sortDirection", "totalRecords", "records");
        }

        [Test]
        public void queue_states_serialize_as_readarr_strings()
        {
            var json = STJson.ToJson(new QueueResource
            {
                TrackedDownloadState = TrackedDownloadState.ImportBlocked,
                TrackedDownloadStatus = TrackedDownloadStatus.Warning
            });

            Assert.That(json, Does.Match("\"trackedDownloadState\":\\s*\"importBlocked\""));
            Assert.That(json, Does.Match("\"trackedDownloadStatus\":\\s*\"warning\""));
        }

        [Test]
        public void history_resource_fields_and_event_numbers()
        {
            AssertHasFields(typeof(HistoryResource), "id", "bookId", "authorId", "sourceTitle", "quality", "date", "downloadId", "eventType", "data", "book", "author");

            // Readarr clients filter history by these numbers (eventType query parameter).
            Assert.That((int)EntityHistoryEventType.Grabbed, Is.EqualTo(1));
            Assert.That((int)EntityHistoryEventType.BookFileImported, Is.EqualTo(3));
            Assert.That((int)EntityHistoryEventType.DownloadFailed, Is.EqualTo(4));
            Assert.That((int)EntityHistoryEventType.BookFileDeleted, Is.EqualTo(5));
            Assert.That((int)EntityHistoryEventType.BookImportIncomplete, Is.EqualTo(7));
            Assert.That((int)EntityHistoryEventType.DownloadImported, Is.EqualTo(8));

            Assert.That(STJson.ToJson(new HistoryResource { EventType = EntityHistoryEventType.BookFileImported }), Does.Match("\"eventType\":\\s*\"bookFileImported\""));
        }

        [Test]
        public void command_resource_fields_and_readarr_command_names()
        {
            AssertHasFields(typeof(CommandResource), "id", "name", "commandName", "message", "body", "priority", "status", "queued", "started", "ended", "trigger");

            // POST /api/v1/command {"name": ...} resolves "<Name>Command" types.
            var commandTypes = typeof(NzbDrone.Core.Messaging.Commands.Command).Assembly.GetTypes().Select(t => t.Name).ToHashSet();
            foreach (var name in new[] { "BookSearch", "AuthorSearch", "MissingBookSearch", "CutoffUnmetBookSearch", "RefreshAuthor", "RefreshBook", "RssSync", "RescanFolders", "DownloadedBooksScan" })
            {
                Assert.That(commandTypes, Does.Contain(name + "Command"), name);
            }
        }
    }

    internal sealed class PassThroughLocalizationService : ILocalizationService
    {
        public Dictionary<string, string> GetLocalizationDictionary()
        {
            return new Dictionary<string, string>();
        }

        public string GetLocalizedString(string phrase)
        {
            return phrase;
        }

        public string GetLocalizedString(string phrase, Dictionary<string, object> tokens)
        {
            return phrase;
        }
    }
}
