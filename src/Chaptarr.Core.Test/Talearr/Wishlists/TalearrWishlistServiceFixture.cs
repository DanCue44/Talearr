using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Wishlists;

namespace Chaptarr.Core.Test.Talearr.Wishlists
{
    [TestFixture]
    public class TalearrWishlistServiceFixture
    {
        private string _folder;
        private Dictionary<string, string> _config;
        private Dictionary<int, Book> _books;
        private Dictionary<int, Author> _authors;
        private List<ImportListExclusion> _exclusions;
        private List<(string ProviderId, MonitoringConfig Config)> _enqueued;
        private List<Command> _pushed;
        private List<string> _searches;
        private Func<string, List<Book>> _search;
        private List<TalearrWishlistRow> _hardcoverRows;
        private TalearrWishlistService _service;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-wishlist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
            _config = new Dictionary<string, string>();
            _authors = new Dictionary<int, Author> { [11] = new Author { Id = 11, Name = "George Orwell", AudiobookMonitored = false, EbookMonitored = true } };
            _books = new Dictionary<int, Book>
            {
                [4095] = new Book { Id = 4095, AuthorId = 11, Title = "1984", MediaType = BookMediaType.Ebook, EbookMonitored = true, GoodreadsWorkId = "gr:153313", TitleSlug = "1984" },
                [5002] = new Book { Id = 5002, AuthorId = 11, Title = "1984", MediaType = BookMediaType.Audiobook, AudiobookMonitored = false, GoodreadsWorkId = "gr:153313", TitleSlug = "1984_2" }
            };
            _exclusions = new List<ImportListExclusion>();
            _enqueued = new List<(string, MonitoringConfig)>();
            _pushed = new List<Command>();
            _searches = new List<string>();
            _hardcoverRows = new List<TalearrWishlistRow>();
            _search = q => q switch
            {
                "9780451524935" => new List<Book> { Found("1984", "George Orwell", "gr:153313", "gr:3706") },
                "project hail mary" => new List<Book> { Found("The Martian", "Andy Weir", "gr:1", "gr:6540057"), Found("Project Hail Mary", "Andy Weir", "gr:54493401", "gr:6540057") },
                _ => new List<Book>()
            };
            _service = Service();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private static Book Found(string title, string author, string workId, string authorId)
        {
            return new Book { Title = title, GoodreadsWorkId = workId, Author = new Author { Name = author, GoodreadsAuthorId = authorId } };
        }

        private TalearrWishlistService Service()
        {
            var (repository, repoFake) = Fake<IConfigRepository>.Create();
            repoFake.On(nameof(IConfigRepository.All), _ => _config.Select(kv => new Config { Key = kv.Key, Value = kv.Value }).ToList())
                .On(nameof(IConfigRepository.Get), args => _config.TryGetValue((string)args[0], out var v) ? new Config { Key = (string)args[0], Value = v } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _config[(string)args[0]] = (string)args[1];
                    return null;
                })
                .On(nameof(IConfigRepository.Delete), args =>
                {
                    _config.Remove(((Config)args[0]).Key);
                    return null;
                });

            var (hardcover, hardcoverFake) = Fake<ITalearrHardcoverListClient>.Create();
            hardcoverFake.On(nameof(ITalearrHardcoverListClient.GetList), _ => ("Audio queue", _hardcoverRows));

            var (search, searchFake) = Fake<ISearchForNewBook>.Create();
            searchFake.On(nameof(ISearchForNewBook.SearchByIsbn), args => Record((string)args[0]))
                .On(nameof(ISearchForNewBook.SearchByAsin), args => Record((string)args[0]))
                .On(nameof(ISearchForNewBook.SearchForNewBook), args => Record(((string)args[0]).ToLowerInvariant()));

            var (books, booksFake) = Fake<IBookService>.Create();
            booksFake.On("FindByProviderId", args => _books.Values.FirstOrDefault(b => b.GoodreadsWorkId == (string)args[1] && (args.Length < 3 || b.MediaType == (BookMediaType)args[2])))
                .On(nameof(IBookService.SetMonitoredForMediaType), args =>
                {
                    foreach (var id in (IEnumerable<int>)args[0])
                    {
                        _books[id].SetMonitoredForMediaType((string)args[1], (bool)args[2]);
                    }

                    return null;
                });

            var (authors, authorsFake) = Fake<IAuthorService>.Create();
            authorsFake.On(nameof(IAuthorService.GetAuthor), args => _authors[(int)args[0]])
                .On(nameof(IAuthorService.UpdateAuthor), args => args[0]);

            var (exclusions, exclusionsFake) = Fake<IImportListExclusionService>.Create();
            exclusionsFake.On(nameof(IImportListExclusionService.All), _ => _exclusions);

            var (pending, pendingFake) = Fake<IPendingAuthorImportService>.Create();
            pendingFake.On(nameof(IPendingAuthorImportService.EnqueueAsync), args =>
            {
                _enqueued.Add(((string)args[0], (MonitoringConfig)args[1]));
                return Task.FromResult(_enqueued.Count);
            });

            var audio = new RootFolder { Id = 4, Path = "/audiobooks" };
            var ebook = new RootFolder { Id = 3, Path = "/ebooks" };
            var (roots, rootsFake) = Fake<IRootFolderService>.Create();
            rootsFake.On(nameof(IRootFolderService.All), _ => new List<RootFolder> { ebook, audio });

            var (resolver, resolverFake) = Fake<IRootFolderSettingsResolver>.Create();
            resolverFake.On(nameof(IRootFolderSettingsResolver.ResolveSettings), args =>
            {
                var folder = (RootFolder)args[0];
                var mediaType = (BookMediaType)args[1];
                var fits = (folder.Id == 4 && mediaType == BookMediaType.Audiobook) || (folder.Id == 3 && mediaType == BookMediaType.Ebook);
                return fits
                    ? new ResolvedRootFolderSettings { IsConfigured = true, QualityProfileId = folder.Id == 4 ? 2 : 1, MetadataProfileId = folder.Id == 4 ? 1 : 2 }
                    : new ResolvedRootFolderSettings { IsConfigured = false };
            });

            var (config, configFake) = Fake<IConfigService>.Create();
            configFake.On("get_" + nameof(IConfigService.DefaultAudiobookRootFolderPath), _ => string.Empty)
                .On("get_" + nameof(IConfigService.DefaultEbookRootFolderPath), _ => string.Empty);

            var (queue, queueFake) = Fake<IManageCommandQueue>.Create();
            queueFake.On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });

            var (folders, foldersFake) = Fake<IAppFolderInfo>.Create();
            foldersFake.On("get_" + nameof(IAppFolderInfo.AppDataFolder), _ => _folder);

            var store = new TalearrWishlistStore(repository, LogManager.GetCurrentClassLogger());
            return new TalearrWishlistService(store, hardcover, search, books, authors, exclusions, pending, roots, resolver, config, queue, folders, LogManager.GetCurrentClassLogger());
        }

        private List<Book> Record(string query)
        {
            _searches.Add(query);
            return _search(query);
        }

        private TalearrWishlist Upload(string csv, bool audiobook = true, bool ebook = false)
        {
            return _service.Create(new TalearrWishlistRequest { Name = "StoryGraph", Kind = TalearrWishlistKinds.File, Csv = csv, Audiobook = audiobook, Ebook = ebook });
        }

        private const string Csv = "Title,Authors,ISBN/UID,Read Status\n1984,George Orwell,9780451524935,to-read\nProject Hail Mary,Andy Weir,,to-read\nNo Such Book,Nobody,,to-read\n";

        [Test]
        public void a_book_in_the_library_gets_only_the_wanted_format_monitored_and_searched()
        {
            var wishlist = Upload(Csv);
            var result = _service.Sync(wishlist.Id);

            Assert.That(_books[5002].AudiobookMonitored, Is.True);
            Assert.That(_authors[11].AudiobookMonitored, Is.True, "the author's audiobook switch is turned on");
            Assert.That(_authors[11].AudiobookMonitorNewItems, Is.Null, "future releases are left alone");
            Assert.That(_pushed.OfType<BookSearchCommand>().Single().BookIds, Is.EqualTo(new[] { 5002 }));
            Assert.That(result.Monitored, Is.EqualTo(1));
        }

        [Test]
        public void a_new_book_is_queued_through_the_author_import_monitoring_only_that_book()
        {
            var wishlist = Upload(Csv);
            var result = _service.Sync(wishlist.Id);

            var (providerId, config) = _enqueued.Single();
            Assert.That(providerId, Is.EqualTo("gr:6540057"));
            Assert.That(config.CreateAudiobook, Is.True);
            Assert.That(config.CreateEbook, Is.False);
            Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(config.AudiobookBooksToMonitor, Is.EqualTo(new[] { "gr:54493401" }), "the title match, not the first result");
            Assert.That(config.AudiobookBooksToSearch, Is.EqualTo(new[] { "gr:54493401" }));
            Assert.That(config.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
            Assert.That(config.AudiobookQualityProfileId, Is.EqualTo(2));
            Assert.That(config.RequestedBy, Is.EqualTo("TaleArr wishlist: StoryGraph"));
            Assert.That(_pushed.OfType<ProcessPendingImportsCommand>().Count(), Is.EqualTo(1));
            Assert.That(result.Queued, Is.EqualTo(1));
            Assert.That(result.NoMatch, Is.EqualTo(1));
            Assert.That(_service.Get(wishlist.Id).LastResult, Is.EqualTo("3 rows, 1 added, 1 monitored, 1 not found"));
        }

        [Test]
        public void rows_are_acted_on_once()
        {
            var wishlist = Upload(Csv);
            _service.Sync(wishlist.Id);
            _books[5002].SetMonitoredForMediaType("audiobook", false);
            _searches.Clear();
            _enqueued.Clear();

            var result = _service.Sync(wishlist.Id);

            Assert.That(_books[5002].AudiobookMonitored, Is.False, "unmonitored by hand afterwards");
            Assert.That(_enqueued, Is.Empty);
            Assert.That(_searches, Is.Empty, "not found rows wait a week before the next lookup");
            Assert.That(result.Rows, Is.EqualTo(3));
        }

        [Test]
        public void rows_not_found_are_looked_up_again_after_a_week()
        {
            var wishlist = Upload(Csv);
            _service.Sync(wishlist.Id);

            var stored = _service.Get(wishlist.Id);
            var key = stored.Handled.Single(kv => kv.Value.StartsWith("noMatch@")).Key;
            stored.Handled[key] = "noMatch@" + DateTime.UtcNow.AddDays(-8).ToString("yyyy-MM-dd");
            _config["talearr.wishlist." + wishlist.Id] = System.Text.Json.JsonSerializer.Serialize(stored, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            _searches.Clear();

            _service.Sync(wishlist.Id);

            Assert.That(_searches, Is.EqualTo(new[] { "no such book" }));
        }

        [Test]
        public void excluded_books_are_skipped()
        {
            _exclusions.Add(new ImportListExclusion { ForeignId = "gr:54493401", Name = "Project Hail Mary" });
            var wishlist = Upload(Csv);

            var result = _service.Sync(wishlist.Id);

            Assert.That(_enqueued, Is.Empty);
            Assert.That(result.Excluded, Is.EqualTo(1));
        }

        [Test]
        public void the_preview_changes_nothing()
        {
            var preview = _service.PreviewUpload(new TalearrWishlistRequest { Csv = Csv, Audiobook = true, Ebook = true });

            Assert.That(preview.Format, Is.EqualTo(TalearrWishlistFormats.StoryGraph));
            Assert.That(preview.Items.Select(i => i.State), Is.EqualTo(new[] { "new", "new", "noMatch" }));
            Assert.That(preview.Items[0].Message, Is.EqualTo("Will monitor the audiobook"), "the ebook is already monitored");
            Assert.That(preview.Items[0].EbookBookId, Is.EqualTo(4095));
            Assert.That(_books[5002].AudiobookMonitored, Is.False);
            Assert.That(_enqueued, Is.Empty);
            Assert.That(_pushed, Is.Empty);
            Assert.That(_config, Is.Empty);
        }

        [Test]
        public void a_hardcover_list_needs_no_lookup()
        {
            _hardcoverRows.Add(new TalearrWishlistRow { Key = "hc:428778", Title = "Dune", Author = "Frank Herbert", BookProviderId = "hc:428778", AuthorProviderId = "hc:2" });
            var wishlist = _service.Create(new TalearrWishlistRequest { Kind = TalearrWishlistKinds.HardcoverList, Url = "https://hardcover.app/@dan/lists/audio-queue", Audiobook = true });

            _service.Sync(wishlist.Id);

            Assert.That(_searches, Is.Empty);
            Assert.That(_enqueued.Single().ProviderId, Is.EqualTo("hc:2"));
            Assert.That(_enqueued.Single().Config.AudiobookBooksToMonitor, Is.EqualTo(new[] { "hc:428778" }));
            Assert.That(_service.Get(wishlist.Id).Name, Is.EqualTo("Hardcover list"));
        }

        [Test]
        public void lookups_per_sync_are_capped()
        {
            var csv = "Title,Author\n" + string.Join("\n", Enumerable.Range(1, TalearrWishlistService.MaxLookupsPerRun + 5).Select(i => $"Book {i},Author {i}"));
            var wishlist = Upload(csv);

            var result = _service.Sync(wishlist.Id);

            Assert.That(_searches.Count, Is.EqualTo(TalearrWishlistService.MaxLookupsPerRun));
            Assert.That(result.Deferred, Is.EqualTo(5));
        }

        [Test]
        public void an_uploaded_file_is_kept_in_the_config_folder_and_removed_with_the_wishlist()
        {
            var wishlist = Upload(Csv);
            var path = Path.Combine(_folder, "talearr-wishlists", wishlist.Id + ".csv");

            Assert.That(File.Exists(path), Is.True);
            Assert.That(wishlist.Format, Is.EqualTo(TalearrWishlistFormats.StoryGraph));

            _service.Delete(wishlist.Id);

            Assert.That(File.Exists(path), Is.False);
            Assert.That(_config, Is.Empty);
        }

        [Test]
        public void a_missing_server_file_is_reported_not_thrown()
        {
            var wishlist = _service.Create(new TalearrWishlistRequest { Kind = TalearrWishlistKinds.File, Path = Path.Combine(_folder, "nope.csv") });

            var result = _service.Sync(wishlist.Id);

            Assert.That(result.Rows, Is.EqualTo(0));
            Assert.That(_service.Get(wishlist.Id).LastError, Does.Contain("does not exist"));
        }

        [Test]
        public void a_wishlist_needs_a_format_and_a_source()
        {
            Assert.Throws<TalearrWishlistException>(() => _service.Create(new TalearrWishlistRequest { Csv = Csv, Audiobook = false, Ebook = false }));
            Assert.Throws<TalearrWishlistException>(() => _service.Create(new TalearrWishlistRequest { Kind = TalearrWishlistKinds.HardcoverList, Url = "https://example.com" }));
            Assert.Throws<TalearrWishlistException>(() => _service.Create(new TalearrWishlistRequest { Kind = TalearrWishlistKinds.File }));
        }
    }
}
