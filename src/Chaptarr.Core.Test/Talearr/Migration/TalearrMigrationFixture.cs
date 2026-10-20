using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Talearr.Audiobook;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.Sabnzbd;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.Indexers.Torznab;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Talearr.Migration;

namespace Chaptarr.Core.Test.Talearr.Migration
{
    [TestFixture]
    public class TalearrMigrationFixture
    {
        private static readonly string FixtureFolder = Path.Combine(TestContext.CurrentContext.TestDirectory, "Talearr", "Migration", "Fixtures");

        private string _folder;
        private string _audioRoot;
        private List<RootFolder> _roots;
        private List<Tag> _tags;
        private Dictionary<string, Book> _localBooks;
        private List<(string ProviderId, MonitoringConfig Config)> _enqueued;
        private List<Command> _pushed;
        private List<DownloadClientDefinition> _clients;
        private List<IndexerDefinition> _indexers;
        private List<string> _searches;
        private List<RootFolder> _addedRoots;
        private TalearrMigrationService _service;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-migration-test-" + Guid.NewGuid().ToString("N"));
            _audioRoot = Path.Combine(_folder, "audiobooks");
            Directory.CreateDirectory(_audioRoot);

            _roots = new List<RootFolder> { new RootFolder { Id = 3, Path = "/ebooks", FolderType = FolderType.Ebook } };
            _tags = new List<Tag> { new Tag { Id = 9, Label = "fav" } };
            _localBooks = new Dictionary<string, Book>
            {
                ["hc:1001|Ebook"] = new Book { Id = 501, AuthorId = 1, Title = "Project Hail Mary", MediaType = BookMediaType.Ebook, EbookMonitored = false, HardcoverBookId = "hc:1001" }
            };
            _enqueued = new List<(string, MonitoringConfig)>();
            _pushed = new List<Command>();
            _clients = new List<DownloadClientDefinition>();
            _indexers = new List<IndexerDefinition>();
            _searches = new List<string>();
            _addedRoots = new List<RootFolder>();
            _service = Service();
        }

        [TearDown]
        public void TearDown()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private static Book Found(string title, string author, string bookId, string authorId)
        {
            return new Book { Title = title, HardcoverBookId = bookId, Author = new Author { Name = author, HardcoverAuthorId = authorId } };
        }

        private TalearrMigrationService Service()
        {
            var (search, searchFake) = Fake<ISearchForNewBook>.Create();
            searchFake.On(nameof(ISearchForNewBook.SearchByGoodreadsBookId), args =>
                {
                    _searches.Add("gr:" + args[0]);
                    return (int)args[0] == 54493401 ? new List<Book> { Found("Project Hail Mary", "Andy Weir", "hc:1001", "hc:500") } : new List<Book>();
                })
                .On(nameof(ISearchForNewBook.SearchByAsin), args =>
                {
                    _searches.Add("asin:" + args[0]);
                    return (string)args[0] == "B002UZMLXM" ? new List<Book> { Found("Mistborn: The Final Empire", "Brandon Sanderson", "hc:2001", "hc:600") } : new List<Book>();
                })
                .On(nameof(ISearchForNewBook.SearchByIsbn), args =>
                {
                    _searches.Add("isbn:" + args[0]);
                    return (string)args[0] == "9780593135204" ? new List<Book> { Found("Project Hail Mary", "Andy Weir", "hc:1001", "hc:500") } : new List<Book>();
                })
                .On(nameof(ISearchForNewBook.SearchForNewBook), args =>
                {
                    _searches.Add("title:" + args[0]);
                    return new List<Book>();
                });

            var (authorSearch, authorSearchFake) = Fake<ISearchForNewAuthor>.Create();
            authorSearchFake.On(nameof(ISearchForNewAuthor.SearchForNewAuthor), args =>
            {
                _searches.Add("author:" + args[0]);
                return new List<Author>();
            });

            var (books, booksFake) = Fake<IBookService>.Create();
            booksFake.On("FindByProviderId", args => _localBooks.TryGetValue((string)args[1] + "|" + args[2], out var book) ? book : null)
                .On(nameof(IBookService.SetMonitoredForMediaType), args =>
                {
                    foreach (var id in (IEnumerable<int>)args[0])
                    {
                        _localBooks.Values.Single(b => b.Id == id).SetMonitoredForMediaType((string)args[1], (bool)args[2]);
                    }

                    return null;
                });

            var andy = new Author { Id = 1, Name = "Andy Weir", EbookMonitored = true };
            var (authors, authorsFake) = Fake<IAuthorService>.Create();
            authorsFake.On(nameof(IAuthorService.FindByProviderId), args => (string)args[0] == "hc" && (string)args[1] == "500" ? andy : null)
                .On(nameof(IAuthorService.GetAuthor), _ => andy)
                .On(nameof(IAuthorService.UpdateAuthor), args => args[0]);

            var (roots, rootsFake) = Fake<IRootFolderService>.Create();
            rootsFake.On(nameof(IRootFolderService.All), _ => _roots.ToList())
                .On(nameof(IRootFolderService.Add), args =>
                {
                    var root = (RootFolder)args[0];
                    root.Id = 10 + _roots.Count;
                    _roots.Add(root);
                    _addedRoots.Add(root);
                    return root;
                });

            var (resolver, resolverFake) = Fake<IRootFolderSettingsResolver>.Create();
            resolverFake.On(nameof(IRootFolderSettingsResolver.ResolveSettings), args =>
            {
                var folder = (RootFolder)args[0];
                var mediaType = (BookMediaType)args[1];
                var settings = mediaType == BookMediaType.Audiobook ? folder.GetAudiobookSettings() : folder.GetEbookSettings();
                if (folder.Id == 3 && mediaType == BookMediaType.Ebook)
                {
                    return new ResolvedRootFolderSettings { IsConfigured = true, QualityProfileId = 5, MetadataProfileId = 7 };
                }

                return settings == null
                    ? new ResolvedRootFolderSettings { IsConfigured = false }
                    : new ResolvedRootFolderSettings { IsConfigured = true, QualityProfileId = settings.QualityProfileId, MetadataProfileId = settings.MetadataProfileId, Tags = settings.Tags };
            });

            var (config, configFake) = Fake<IConfigService>.Create();
            configFake.On("get_" + nameof(IConfigService.DefaultAudiobookRootFolderPath), _ => string.Empty)
                .On("get_" + nameof(IConfigService.DefaultEbookRootFolderPath), _ => string.Empty);

            var (quality, qualityFake) = Fake<IQualityProfileService>.Create();
            qualityFake.On(nameof(IQualityProfileService.All), _ => new List<QualityProfile>
            {
                new QualityProfile { Id = 5, Name = "eBook", ProfileType = ProfileType.Ebook },
                new QualityProfile { Id = 6, Name = "Audio", ProfileType = ProfileType.Audiobook }
            });

            var (metadata, metadataFake) = Fake<IMetadataProfileService>.Create();
            metadataFake.On(nameof(IMetadataProfileService.All), _ => new List<MetadataProfile> { new MetadataProfile { Id = 7, Name = "Standard", ProfileType = MetadataProfileType.General } });

            var (tags, tagsFake) = Fake<ITagService>.Create();
            tagsFake.On(nameof(ITagService.All), _ => _tags.ToList())
                .On(nameof(ITagService.Add), args =>
                {
                    var tag = (Tag)args[0];
                    tag.Id = 20 + _tags.Count;
                    _tags.Add(tag);
                    return tag;
                });

            var (clients, clientsFake) = Fake<IDownloadClientFactory>.Create();
            clientsFake.On(nameof(IDownloadClientFactory.All), _ => _clients.ToList())
                .On(nameof(IDownloadClientFactory.GetDefaultDefinitions), _ => new List<DownloadClientDefinition>
                {
                    new DownloadClientDefinition { Implementation = "Sabnzbd", Settings = new SabnzbdSettings() }
                })
                .On(nameof(IDownloadClientFactory.Create), args =>
                {
                    _clients.Add((DownloadClientDefinition)args[0]);
                    return args[0];
                });

            var (indexers, indexersFake) = Fake<IIndexerFactory>.Create();
            indexersFake.On(nameof(IIndexerFactory.All), _ => _indexers.ToList())
                .On(nameof(IIndexerFactory.GetDefaultDefinitions), _ => new List<IndexerDefinition>
                {
                    new IndexerDefinition { Implementation = "Newznab", Settings = new NewznabSettings() },
                    new IndexerDefinition { Implementation = "Torznab", Settings = new TorznabSettings() }
                })
                .On(nameof(IIndexerFactory.Create), args =>
                {
                    _indexers.Add((IndexerDefinition)args[0]);
                    return args[0];
                });

            var (pending, pendingFake) = Fake<IPendingAuthorImportService>.Create();
            pendingFake.On(nameof(IPendingAuthorImportService.EnqueueAsync), args =>
            {
                _enqueued.Add(((string)args[0], (MonitoringConfig)args[1]));
                return Task.FromResult(_enqueued.Count);
            });

            var (queue, queueFake) = Fake<IManageCommandQueue>.Create();
            queueFake.On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });

            var (folders, foldersFake) = Fake<IAppFolderInfo>.Create();
            foldersFake.On("get_" + nameof(IAppFolderInfo.AppDataFolder), _ => _folder);

            var (api, _) = Fake<ITalearrMigrationApiReader>.Create();
            return new TalearrMigrationService(api, new TalearrMigrationBackupReader(), search, authorSearch, books, authors, roots, resolver, config,
                quality, metadata, tags, clients, indexers, pending, queue, folders, LogManager.GetCurrentClassLogger());
        }

        private static TalearrMigrationSnapshot ReadarrSnapshot(string source = TalearrMigrationSources.Readarr)
        {
            var recorded = JObject.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "readarr-api.json")));
            return TalearrMigrationApiReader.ReadReadarr(path => recorded[path]?.ToString() ?? throw new TalearrMigrationException("not recorded: " + path), source);
        }

        private TalearrMigrationRequest Request()
        {
            return new TalearrMigrationRequest
            {
                PathMappings = new List<TalearrMigrationPathMapping>
                {
                    new TalearrMigrationPathMapping { From = "/books", To = "/ebooks" },
                    new TalearrMigrationPathMapping { From = "/audiobooks", To = _audioRoot }
                }
            };
        }

        private string BuildDatabase()
        {
            var path = Path.Combine(_folder, "readarr.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = File.ReadAllText(Path.Combine(FixtureFolder, "readarr-fixture.sqlscript"));
                command.ExecuteNonQuery();
            }

            return path;
        }

        private static TalearrMigrationEntry Entry(TalearrMigrationReport report, string area, string name)
        {
            return report.Entries.Single(e => e.Area == area && e.Name == name);
        }

        [Test]
        public void the_readarr_api_reader_rebuilds_settings_and_editions_from_recorded_json()
        {
            var snapshot = ReadarrSnapshot();

            Assert.That(snapshot.AppName, Is.EqualTo("Readarr"));
            Assert.That(snapshot.Version, Is.EqualTo("0.4.18.2805"));
            Assert.That(snapshot.Authors.Select(a => a.Name), Is.EqualTo(new[] { "Andy Weir", "Brandon Sanderson", "Zzz Nobody" }));
            Assert.That(snapshot.Books, Has.Count.EqualTo(5));

            var mistborn = snapshot.Books.Single(b => b.Id == 20);
            Assert.That(mistborn.Asin, Is.EqualTo("B002UZMLXM"), "the monitored edition wins");
            Assert.That(mistborn.Isbn13, Is.Null);
            Assert.That(snapshot.Books.Single(b => b.Id == 10).Isbn13, Is.EqualTo("9780593135204"));
            Assert.That(snapshot.Books.Single(b => b.Id == 11).HasFile, Is.True);

            var jackett = JObject.Parse(snapshot.Indexers.Single(i => i.Name == "Jackett Books").Settings);
            Assert.That((double)jackett["seedCriteria"]["seedRatio"], Is.EqualTo(1.5), "dotted field names become nested objects");
            Assert.That(snapshot.DownloadClients.Single(c => c.Name == "SABnzbd").Enable, Is.True);
            Assert.That(snapshot.RootFolders.Single(r => r.Path == "/books").Tags, Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void the_backup_reader_reads_a_readarr_db_and_a_zip_without_changing_them()
        {
            var db = BuildDatabase();
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db)));

            var snapshot = new TalearrMigrationBackupReader().Read(db, TalearrMigrationSources.Readarr);

            Assert.That(snapshot.Version, Is.EqualTo("schema 39"));
            Assert.That(snapshot.Authors.Single(a => a.Id == 1).ForeignAuthorId, Is.EqualTo("6540057"));
            Assert.That(snapshot.Authors.Single(a => a.Id == 1).MonitorNewItems, Is.EqualTo("all"));
            Assert.That(snapshot.Authors.Single(a => a.Id == 2).MonitorNewItems, Is.EqualTo("none"));
            Assert.That(snapshot.Books.Select(b => b.Id), Is.EquivalentTo(new[] { 10, 11, 20, 21, 30 }), "a book whose author is gone is left out");
            Assert.That(snapshot.Books.Single(b => b.Id == 20).Asin, Is.EqualTo("B002UZMLXM"));
            Assert.That(snapshot.Books.Single(b => b.Id == 10).HasFile, Is.True);
            Assert.That(snapshot.Books.Single(b => b.Id == 20).HasFile, Is.False);
            Assert.That(JObject.Parse(snapshot.DownloadClients.Single().Settings)["apiKey"].ToString(), Is.EqualTo("sab-key-123"));
            Assert.That(snapshot.Indexers.Single().EnableInteractiveSearch, Is.False);
            Assert.That(snapshot.RootFolders.Single(r => r.Path == "/audiobooks").NewItemMonitor, Is.EqualTo("none"));
            Assert.That(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(db))), Is.EqualTo(hash), "the source database is not touched");

            var zip = Path.Combine(_folder, "readarr_backup_v0.4.18.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(db, "readarr.db");
                archive.CreateEntry("config.xml");
            }

            var fromZip = new TalearrMigrationBackupReader().Read(zip, TalearrMigrationSources.Readarr);
            Assert.That(fromZip.Authors, Has.Count.EqualTo(3));
            Assert.That(fromZip.Books, Has.Count.EqualTo(5));
        }

        [Test]
        public void the_backup_reader_refuses_something_that_is_not_a_readarr_database()
        {
            var path = Path.Combine(_folder, "other.db");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Foo (Id INTEGER)";
                command.ExecuteNonQuery();
            }

            Assert.Throws<TalearrMigrationException>(() => new TalearrMigrationBackupReader().Read(path, TalearrMigrationSources.Readarr));
            Assert.Throws<TalearrMigrationException>(() => new TalearrMigrationBackupReader().Read(Path.Combine(_folder, "missing.db"), TalearrMigrationSources.Readarr));
        }

        [Test]
        public void the_dry_run_reports_creates_matches_unmatched_and_conflicts_without_writing()
        {
            var plan = _service.BuildPlan(Request(), ReadarrSnapshot());
            var report = plan.Report;

            Assert.That(Entry(report, TalearrMigrationAreas.Tag, "fav").Action, Is.EqualTo(TalearrMigrationActions.Exists));
            Assert.That(Entry(report, TalearrMigrationAreas.Tag, "kids").Action, Is.EqualTo(TalearrMigrationActions.Create));
            Assert.That(Entry(report, TalearrMigrationAreas.RootFolder, "/ebooks").Action, Is.EqualTo(TalearrMigrationActions.Exists));
            Assert.That(Entry(report, TalearrMigrationAreas.RootFolder, _audioRoot).Action, Is.EqualTo(TalearrMigrationActions.Create));
            Assert.That(Entry(report, TalearrMigrationAreas.QualityProfile, "eBook").Action, Is.EqualTo(TalearrMigrationActions.Map));
            Assert.That(Entry(report, TalearrMigrationAreas.QualityProfile, "Spoken").Action, Is.EqualTo(TalearrMigrationActions.Unmatched));
            Assert.That(Entry(report, TalearrMigrationAreas.DownloadClient, "SABnzbd").Action, Is.EqualTo(TalearrMigrationActions.Create));
            Assert.That(Entry(report, TalearrMigrationAreas.DownloadClient, "rqbit").Action, Is.EqualTo(TalearrMigrationActions.Conflict));
            Assert.That(Entry(report, TalearrMigrationAreas.Indexer, "MyAnonamouse (Prowlarr)").Action, Is.EqualTo(TalearrMigrationActions.Skip));

            Assert.That(Entry(report, TalearrMigrationAreas.Book, "Project Hail Mary (Andy Weir)").Action, Is.EqualTo(TalearrMigrationActions.Monitor));
            Assert.That(Entry(report, TalearrMigrationAreas.Book, "Mistborn: The Final Empire (Brandon Sanderson)").Action, Is.EqualTo(TalearrMigrationActions.Create));
            Assert.That(Entry(report, TalearrMigrationAreas.Book, "A Book Nobody Has Heard Of (Brandon Sanderson)").Action, Is.EqualTo(TalearrMigrationActions.Unmatched));
            Assert.That(Entry(report, TalearrMigrationAreas.Author, "Zzz Nobody").Action, Is.EqualTo(TalearrMigrationActions.Unmatched));
            Assert.That(Entry(report, TalearrMigrationAreas.Author, "Andy Weir").Action, Is.EqualTo(TalearrMigrationActions.Exists));

            var brandon = report.Authors.Single(a => a.AuthorProviderId == "hc:600");
            Assert.That(brandon.MediaType, Is.EqualTo("audiobook"), "an author under an audio root folder");
            Assert.That(brandon.Books.Select(b => b.BookProviderId), Is.EqualTo(new[] { "hc:2001" }));
            Assert.That(report.Summary.BooksMonitored, Is.EqualTo(3));
            Assert.That(report.Summary.BooksMatched, Is.EqualTo(2));
            Assert.That(_searches, Does.Contain("gr:68428").And.Contain("asin:B002UZMLXM"), "goodreads id first, then the ASIN");
            Assert.That(_searches, Does.Not.Contain("title:The Martian"), "unmonitored books are not looked up");

            Assert.That(_enqueued, Is.Empty);
            Assert.That(_clients, Is.Empty);
            Assert.That(_addedRoots, Is.Empty);
            Assert.That(_tags, Has.Count.EqualTo(1));
            Assert.That(plan.Request.ApiKey, Is.Null);
        }

        [Test]
        public void bookshelf_ids_are_not_looked_up_as_goodreads_ids()
        {
            _service.BuildPlan(Request(), ReadarrSnapshot(TalearrMigrationSources.Bookshelf));

            Assert.That(_searches.Where(s => s.StartsWith("gr:")), Is.Empty);
            Assert.That(_searches, Does.Contain("asin:B08FHBV4ZX"));
        }

        [Test]
        public void apply_creates_what_the_plan_showed_and_adds_providers_disabled()
        {
            var plan = _service.BuildPlan(Request(), ReadarrSnapshot());
            var report = _service.Apply(plan);

            Assert.That(_tags.Select(t => t.Label), Is.EquivalentTo(new[] { "fav", "kids" }));

            var root = _addedRoots.Single();
            Assert.That(root.Path, Is.EqualTo(_audioRoot));
            Assert.That(root.FolderType, Is.EqualTo(FolderType.Audiobook));
            Assert.That(root.GetAudiobookSettings().QualityProfileId, Is.EqualTo(6), "no 'Spoken' profile here, so the first audiobook profile");
            Assert.That(root.GetAudiobookSettings().MonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));

            var sab = _clients.Single();
            Assert.That(sab.Name, Is.EqualTo("SABnzbd"));
            Assert.That(sab.Enable, Is.False, "imported providers start disabled");
            Assert.That(((SabnzbdSettings)sab.Settings).ApiKey, Is.EqualTo("sab-key-123"));
            Assert.That(((SabnzbdSettings)sab.Settings).MusicCategory, Is.EqualTo("readarr"));

            Assert.That(_indexers.Select(i => i.Name), Is.EquivalentTo(new[] { "NZBgeek", "Jackett Books" }));
            var geek = _indexers.Single(i => i.Name == "NZBgeek");
            Assert.That(geek.Enable, Is.False);
            Assert.That(((NewznabSettings)geek.Settings).Categories, Is.EqualTo(new[] { 7020, 3030 }));
            Assert.That(geek.Tags, Is.EqualTo(new[] { 9 }), "source tag fav maps to the existing Talearr tag");
            Assert.That(((TorznabSettings)_indexers.Single(i => i.Name == "Jackett Books").Settings).SeedCriteria.SeedRatio, Is.EqualTo(1.5));

            Assert.That(_localBooks["hc:1001|Ebook"].EbookMonitored, Is.True, "a book already in Talearr gets monitored in place");

            var (providerId, config) = _enqueued.Single();
            Assert.That(providerId, Is.EqualTo("hc:600"));
            Assert.That(config.CreateAudiobook, Is.True);
            Assert.That(config.CreateEbook, Is.False);
            Assert.That(config.AudiobookRootFolderPath, Is.EqualTo(_audioRoot));
            Assert.That(config.AudiobookMonitored, Is.True);
            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(config.AudiobookBooksToMonitor, Is.EqualTo(new[] { "hc:2001" }));
            Assert.That(config.AudiobookBooksToSearch, Is.Null, "search on add is off by default");

            Assert.That(_pushed.OfType<ProcessPendingImportsCommand>(), Has.Exactly(1).Items);
            Assert.That(_pushed.OfType<RescanFoldersCommand>(), Is.Empty, "the new root folder is scanned by upstream when it is added");
            Assert.That(report.DryRun, Is.False);
        }

        [Test]
        public void apply_twice_does_not_duplicate_tags_or_providers()
        {
            var plan = _service.BuildPlan(Request(), ReadarrSnapshot());
            _service.Apply(plan);
            _service.Apply(plan);

            Assert.That(_tags, Has.Count.EqualTo(2));
            Assert.That(_clients, Has.Count.EqualTo(1));
            Assert.That(_indexers, Has.Count.EqualTo(2));
        }

        [Test]
        public void enable_imported_providers_keeps_the_source_switches()
        {
            var request = Request();
            request.EnableImportedProviders = true;
            _service.Apply(_service.BuildPlan(request, ReadarrSnapshot()));

            Assert.That(_clients.Single().Enable, Is.True);
            Assert.That(_indexers.All(i => i.EnableRss && i.EnableAutomaticSearch), Is.True);
        }

        [Test]
        public void lazylibrarian_books_split_into_ebook_and_audiobook_wants()
        {
            var recorded = JObject.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "lazylibrarian-api.json")));
            var snapshot = TalearrMigrationApiReader.ReadLazyLibrarian(path => recorded[path.Contains("getIndex") ? "getIndex" : "getAllBooks"].ToString(), "k");

            Assert.That(snapshot.Authors.Select(a => a.Name), Is.EqualTo(new[] { "Andy Weir", "Ignored Person", "Somebody Else" }));
            Assert.That(snapshot.Authors[1].Monitored, Is.False);
            var phm = snapshot.Books.Single(b => b.Title == "Project Hail Mary");
            Assert.That(phm.Monitored, Is.True);
            Assert.That(phm.AudiobookMonitored, Is.True);
            Assert.That(phm.ForeignEditionId, Is.EqualTo("54493401"));
            Assert.That(phm.HasFile, Is.True);
            Assert.That(snapshot.Books.Single(b => b.Title == "Artemis").Isbn13, Is.Null, "an ISBN-10 is not an ISBN-13");

            _roots.Add(new RootFolder { Id = 4, Path = "/audio", FolderType = FolderType.Audiobook, AudiobookSettings = "{\"QualityProfileId\":6,\"MetadataProfileId\":7}" });
            var plan = _service.BuildPlan(new TalearrMigrationRequest { Source = TalearrMigrationSources.LazyLibrarian }, snapshot);
            var andy = plan.Report.Authors.Where(a => a.AuthorProviderId == "hc:500").Select(a => a.MediaType).ToList();
            Assert.That(andy, Is.EquivalentTo(new[] { "ebook", "audiobook" }));
        }

        [Test]
        public void path_mappings_and_media_type_guess()
        {
            var request = Request();
            Assert.That(TalearrMigrationService.MapPath(request, "/books/Andy Weir"), Is.EqualTo("/ebooks/Andy Weir"));
            Assert.That(TalearrMigrationService.MapPath(request, "/bookshelf"), Is.EqualTo("/bookshelf"), "only whole path segments are mapped");
            Assert.That(TalearrMigrationService.MediaTypeFor(request, "/mnt/user/Audiobooks"), Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(TalearrMigrationService.MediaTypeFor(request, "/books"), Is.EqualTo(BookMediaType.Ebook));
            Assert.That(TalearrMigrationService.MediaTypeFor(new TalearrMigrationRequest { MediaType = "audiobook" }, "/books"), Is.EqualTo(BookMediaType.Audiobook));
            Assert.That(TalearrMigrationService.ParseNewItems("new"), Is.EqualTo(NewItemMonitorTypes.New));
        }

        [Test]
        public void preview_and_apply_go_through_the_command_and_keep_the_plan_on_disk()
        {
            var db = BuildDatabase();
            var request = Request();
            request.BackupPath = db;

            _service.StartPreview(request);
            var command = _pushed.OfType<TalearrMigrationCommand>().Single();
            _service.Execute(command);

            var status = _service.Status();
            Assert.That(status.State, Is.EqualTo("done"), status.Message);
            Assert.That(status.HasPlan, Is.True);
            Assert.That(File.Exists(Path.Combine(_folder, TalearrMigrationService.FolderName, "plan.json")), Is.True);

            _service.StartApply();
            _service.Execute(_pushed.OfType<TalearrMigrationCommand>().Last());

            status = _service.Status();
            Assert.That(status.State, Is.EqualTo("done"), status.Message);
            Assert.That(status.HasPlan, Is.False, "the plan (with provider secrets) is removed after apply");
            Assert.That(status.Report.DryRun, Is.False);
            Assert.That(_enqueued.Select(e => e.ProviderId), Is.EqualTo(new[] { "hc:600" }));
        }
    }
}
