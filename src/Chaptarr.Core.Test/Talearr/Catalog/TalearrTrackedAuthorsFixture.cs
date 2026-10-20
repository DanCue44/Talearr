using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Books;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // Talearr: Tracked authors keep only the user's books (docs/TALEARR_TRACKED_AUTHORS.md).
    [TestFixture]
    public class TalearrTrackedAuthorsFixture
    {
        private const int NewAuthorId = 500;
        private const int ExistingAuthorId = 77;

        private static readonly Logger Log = LogManager.GetLogger("TalearrTest");

        // Store

        [Test]
        public void store_defaults_to_full_and_round_trips_tracked_in_a_talearr_config_row()
        {
            var (store, values) = CreateStore();

            Assert.That(store.Get(12), Is.EqualTo(TalearrAuthorCatalogMode.Full));

            store.Set(12, TalearrAuthorCatalogMode.Tracked);
            Assert.That(values["talearr.authorcatalog.12"], Is.EqualTo("tracked"));
            Assert.That(store.Get(12), Is.EqualTo(TalearrAuthorCatalogMode.Tracked));
            Assert.That(store.GetTrackedAuthorIds(), Is.EquivalentTo(new[] { 12 }));

            store.Set(12, TalearrAuthorCatalogMode.Full);
            Assert.That(values, Is.Empty, "Full authors have no row");
            Assert.That(store.Get(12), Is.EqualTo(TalearrAuthorCatalogMode.Full));
        }

        [Test]
        public void store_forgets_a_deleted_author()
        {
            var (store, values) = CreateStore();
            store.Set(9, TalearrAuthorCatalogMode.Tracked);

            store.Handle(new AuthorDeletedEvent(new Author { Id = 9 }, false, false));

            Assert.That(values, Is.Empty);
            Assert.That(store.Get(9), Is.EqualTo(TalearrAuthorCatalogMode.Full));
        }

        [Test]
        public void pending_request_marker_is_taken_once_and_a_full_request_clears_it()
        {
            var (store, _) = CreateStore();

            store.MarkPendingRequest("HC:42", true);
            Assert.That(store.TakePendingRequest("hc:42"), Is.True);
            Assert.That(store.TakePendingRequest("hc:42"), Is.False);

            store.MarkPendingRequest("hc:43", true);
            store.MarkPendingRequest("hc:43", false);
            Assert.That(store.TakePendingRequest("hc:43"), Is.False);
            Assert.That(store.GetTrackedAuthorIds(), Is.Empty, "pending markers are not author modes");
        }

        [TestCase("tracked", TalearrAuthorCatalogMode.Tracked, true)]
        [TestCase(" Full ", TalearrAuthorCatalogMode.Full, true)]
        [TestCase("everything", TalearrAuthorCatalogMode.Full, false)]
        public void api_mode_names_parse(string value, TalearrAuthorCatalogMode expected, bool valid)
        {
            Assert.That(TalearrAuthorCatalogModes.TryParse(value, out var mode), Is.EqualTo(valid));
            Assert.That(mode, Is.EqualTo(expected));
        }

        // Filter

        [Test]
        public void filter_keeps_kept_local_and_requested_books_per_media_type()
        {
            var localAudio = Kept(Audiobook("Owned", "hc:1"));
            var remote = new List<Book>
            {
                Audiobook("Owned", "hc:1"),
                Ebook("Owned", "hc:1"),
                Audiobook("Requested", "hc:2"),
                Ebook("Requested", "hc:2"),
                Audiobook("Catalog", "hc:3")
            };

            var result = TalearrTrackedCatalogFilter.Apply(
                remote,
                new[] { localAudio },
                book => book.MediaType == BookMediaType.Ebook && book.HardcoverBookId == "hc:2",
                Log,
                new Author { Name = "Author" },
                "Test");

            Assert.That(result.Select(Describe), Is.EquivalentTo(new[] { "Audiobook hc:1", "Ebook hc:2" }));
        }

        [Test]
        public void kept_local_books_follow_the_refresh_pruning_rules()
        {
            Assert.That(TalearrTrackedCatalogFilter.IsKeptLocalBook(Plain(Audiobook("A", "hc:1")), hasFiles: false), Is.False);
            Assert.That(TalearrTrackedCatalogFilter.IsKeptLocalBook(Plain(Audiobook("A", "hc:1")), hasFiles: true), Is.True);

            var monitored = Plain(Audiobook("A", "hc:1"));
            monitored.AudiobookMonitored = true;
            Assert.That(TalearrTrackedCatalogFilter.IsKeptLocalBook(monitored, false), Is.True);

            var manual = Plain(Audiobook("A", "hc:1"));
            manual.AddOptions = new AddBookOptions { AddType = BookAddType.Manual };
            Assert.That(TalearrTrackedCatalogFilter.IsKeptLocalBook(manual, false), Is.True);

            var strict = Plain(Audiobook("A", "hc:1"));
            strict.AnyEditionOk = false;
            Assert.That(TalearrTrackedCatalogFilter.IsKeptLocalBook(strict, false), Is.True);
        }

        // Add

        [Test]
        public async Task tracked_add_of_a_new_author_inserts_only_the_requested_book_and_stores_tracked()
        {
            var world = new LibraryWorld(Catalog());

            await world.Service.AddAuthorAsync("hc:author-1", RequestConfig(BookMediaType.Audiobook, "hc:2", tracked: true));

            Assert.That(world.Inserted.Select(Describe), Is.EqualTo(new[] { "Audiobook hc:2" }));
            Assert.That(world.Store.Get(NewAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Tracked));
        }

        [Test]
        public async Task full_add_of_a_new_author_inserts_the_catalog_and_stays_full()
        {
            var world = new LibraryWorld(Catalog());

            await world.Service.AddAuthorAsync("hc:author-1", RequestConfig(BookMediaType.Audiobook, "hc:2", tracked: false));

            Assert.That(world.Inserted.Select(Describe), Is.EquivalentTo(new[] { "Audiobook hc:1", "Audiobook hc:2", "Audiobook hc:3" }));
            Assert.That(world.Store.Get(NewAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Full));
        }

        [Test]
        public async Task add_author_without_a_book_request_stays_full()
        {
            var world = new LibraryWorld(Catalog());
            var config = RequestConfig(BookMediaType.Audiobook, null, tracked: false);

            await world.Service.AddAuthorAsync("hc:author-1", config);

            Assert.That(world.Inserted, Has.Count.EqualTo(3));
            Assert.That(world.Store.GetTrackedAuthorIds(), Is.Empty);
        }

        [Test]
        public async Task queued_request_becomes_tracked_through_the_pending_marker()
        {
            var world = new LibraryWorld(Catalog());
            world.Store.MarkPendingRequest("hc:author-1", true);

            // The pending queue rebuilds the config from its row, without TalearrTrackedCatalog.
            var config = RequestConfig(BookMediaType.Audiobook, "hc:2", tracked: false);
            config.IsFromQueue = true;
            await world.Service.AddAuthorAsync("hc:author-1", config);

            Assert.That(world.Inserted.Select(Describe), Is.EqualTo(new[] { "Audiobook hc:2" }));
            Assert.That(world.Store.Get(NewAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Tracked));
            Assert.That(world.Store.TakePendingRequest("hc:author-1"), Is.False, "the marker is used up");
        }

        [Test]
        public async Task direct_full_add_ignores_a_stale_pending_marker()
        {
            var world = new LibraryWorld(Catalog());
            world.Store.MarkPendingRequest("hc:author-1", true);

            await world.Service.AddAuthorAsync("hc:author-1", RequestConfig(BookMediaType.Audiobook, null, tracked: false));

            Assert.That(world.Store.Get(NewAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Full));
            Assert.That(world.Store.TakePendingRequest("hc:author-1"), Is.False);
        }

        [Test]
        public async Task request_for_an_existing_full_author_leaves_it_full()
        {
            var existing = ExistingAuthor();
            var world = new LibraryWorld(Catalog(), existing, new List<Book> { Plain(Audiobook("Owned", "hc:1"), ExistingAuthorId) });

            await world.Service.AddAuthorAsync("hc:author-1", RequestConfig(BookMediaType.Audiobook, "hc:2", tracked: true));

            Assert.That(world.Store.Get(ExistingAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Full));
            Assert.That(world.Store.GetTrackedAuthorIds(), Is.Empty);
        }

        [Test]
        public async Task request_for_a_new_media_type_of_a_tracked_author_adds_only_that_book()
        {
            var existing = ExistingAuthor();
            var remote = Catalog();
            remote.Books.AddRange(new[] { Ebook("Owned", "hc:1"), Ebook("Requested", "hc:2"), Ebook("Catalog", "hc:3") });
            var world = new LibraryWorld(remote, existing, new List<Book> { Kept(Audiobook("Owned", "hc:1"), ExistingAuthorId) });
            world.Store.Set(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked);

            await world.Service.AddAuthorAsync("hc:author-1", RequestConfig(BookMediaType.Ebook, "hc:2", tracked: true));

            Assert.That(world.Inserted.Select(Describe), Is.EqualTo(new[] { "Ebook hc:2" }));
            Assert.That(world.Store.Get(ExistingAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Tracked));
        }

        [Test]
        public void single_book_request_config_asks_for_a_tracked_author_unless_it_wants_every_book()
        {
            var ids = new List<string> { "hc:2" };
            var request = new Book { Title = "Requested", HardcoverBookId = "hc:2", MediaType = BookMediaType.Audiobook, Author = new Author { Name = "A" } };
            Assert.That(AddBookService.BuildRequestedBookMonitoringConfig(request, ids, false).TalearrTrackedCatalog, Is.True);

            request.Author.AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.All };
            Assert.That(AddBookService.BuildRequestedBookMonitoringConfig(request, ids, false).TalearrTrackedCatalog, Is.False);
        }

        // Refresh

        [Test]
        public void tracked_refresh_keeps_local_books_and_adds_no_catalog_books()
        {
            var world = new RefreshWorld(new List<Book> { Kept(Audiobook("Owned", "hc:1"), ExistingAuthorId) });
            world.Store.Set(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked);

            var result = world.Service.RemoteChildren(ExistingAuthor(), Catalog());

            Assert.That(result.Select(Describe), Is.EqualTo(new[] { "Audiobook hc:1" }));
        }

        [Test]
        public void full_refresh_adds_the_catalog()
        {
            var world = new RefreshWorld(new List<Book> { Kept(Audiobook("Owned", "hc:1"), ExistingAuthorId) });

            var result = world.Service.RemoteChildren(ExistingAuthor(), Catalog());

            Assert.That(result.Select(Describe), Is.EquivalentTo(new[] { "Audiobook hc:1", "Audiobook hc:2", "Audiobook hc:3" }));
        }

        [Test]
        public void switching_to_tracked_prunes_unmonitored_fileless_rows_but_keeps_monitored_manual_and_filed_books()
        {
            var monitored = Plain(Audiobook("Monitored", "hc:1"), ExistingAuthorId);
            monitored.Id = 11;
            monitored.AudiobookMonitored = true;
            var manual = Plain(Audiobook("Manual", "hc:2"), ExistingAuthorId);
            manual.Id = 12;
            manual.AddOptions = new AddBookOptions { AddType = BookAddType.Manual };
            var filed = Plain(Audiobook("Filed", "hc:3"), ExistingAuthorId);
            filed.Id = 13;
            var catalogRow = Plain(Audiobook("Catalog", "hc:4"), ExistingAuthorId);
            catalogRow.Id = 14;

            var locals = new List<Book> { monitored, manual, filed, catalogRow };
            var world = new RefreshWorld(locals);
            world.AddFile(filed);
            var remote = Catalog();
            remote.Books.Add(Audiobook("Catalog", "hc:4"));
            remote.Books.Add(Audiobook("New", "hc:5"));

            Assert.That(world.Service.RemoteChildren(ExistingAuthor(), remote), Has.Count.EqualTo(5), "Full keeps every row");

            world.Store.Set(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked);
            var trackedRemote = world.Service.RemoteChildren(ExistingAuthor(), remote);
            Assert.That(trackedRemote.Select(Describe), Is.EquivalentTo(new[] { "Audiobook hc:1", "Audiobook hc:2", "Audiobook hc:3" }));

            // The book refresh then decides per local row, against the author-scoped snapshot.
            var bookRefresh = new TestableRefreshBookService(world.MediaFiles);
            foreach (var kept in new[] { monitored, manual, filed })
            {
                Assert.That(bookRefresh.MatchRemote(kept, trackedRemote), Is.Not.Null, kept.Title);
            }

            Assert.That(bookRefresh.MatchRemote(catalogRow, trackedRemote), Is.Null);
            Assert.That(bookRefresh.ShouldDeletePublic(catalogRow), Is.True, "the unmonitored, fileless catalog row is pruned");
            Assert.That(bookRefresh.ShouldDeletePublic(filed), Is.False, "a book with files is never pruned");
        }

        [Test]
        public void requested_books_of_an_add_reconcile_survive_a_tracked_refresh()
        {
            var world = new RefreshWorld(new List<Book> { Kept(Audiobook("Owned", "hc:1"), ExistingAuthorId) });
            world.Store.Set(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked);

            var result = world.Service.RemoteChildrenRequesting(ExistingAuthor(), Catalog(), book => book.HardcoverBookId == "hc:2");

            Assert.That(result.Select(Describe), Is.EquivalentTo(new[] { "Audiobook hc:1", "Audiobook hc:2" }));
        }

        // Requested books pass the metadata profile

        [TestCase(false)]
        [TestCase(true)]
        public async Task requested_omnibus_is_added_for_a_new_author_with_skip_omnibus_on(bool tracked)
        {
            var world = new LibraryWorld(OmnibusCatalog(), profiles: SkipOmnibusProfiles());

            await world.Service.AddAuthorAsync("hc:234021", RequestConfig(BookMediaType.Ebook, "hc:2675413", tracked));

            var inserted = world.Inserted.Select(Describe).ToList();
            Assert.That(inserted, Does.Contain("Ebook hc:2675413"));
            Assert.That(inserted, Has.No.Member("Ebook hc:777"), "other omnibus books are still skipped");
            Assert.That(inserted, Has.Count.EqualTo(tracked ? 1 : 3));
        }

        [Test]
        public async Task pending_retry_config_carries_the_requested_omnibus_for_a_new_author()
        {
            var world = new LibraryWorld(OmnibusCatalog(), profiles: SkipOmnibusProfiles());

            await world.Service.AddAuthorAsync("hc:234021", PendingRetryConfig("hc:2675413"));

            Assert.That(world.Inserted.Select(Describe), Does.Contain("Ebook hc:2675413"));
            Assert.That(world.Inserted.Select(Describe), Has.No.Member("Ebook hc:777"));
        }

        [Test]
        public async Task existing_author_request_and_pending_retry_pass_the_requested_predicate_to_the_reconcile()
        {
            var existing = ExistingAuthor();
            existing.EbookQualityProfileId = 4;
            existing.EbookRootFolderPath = "/ebooks";
            var catalog = OmnibusCatalog();
            var requested = catalog.Books.Single(book => book.HardcoverBookId == "hc:2675413");
            var otherOmnibus = catalog.Books.Single(book => book.HardcoverBookId == "hc:777");

            foreach (var config in new[] { RequestConfig(BookMediaType.Ebook, "hc:2675413", tracked: true), PendingRetryConfig("hc:2675413") })
            {
                var refresh = new CapturingRefreshAuthorService();
                var world = new LibraryWorld(catalog, existing, new List<Book> { Kept(Ebook("Owned", "hc:1"), ExistingAuthorId) }, SkipOmnibusProfiles(), refresh);

                await world.Service.AddAuthorAsync("hc:234021", config);

                Assert.That(refresh.IsRequested, Is.Not.Null, "the reconcile ran with the requested books");
                Assert.That(refresh.IsRequested(requested), Is.True);
                Assert.That(refresh.IsRequested(otherOmnibus), Is.False);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void reconcile_refresh_keeps_the_requested_omnibus_with_skip_omnibus_on(bool tracked)
        {
            var world = new RefreshWorld(new List<Book> { Kept(Ebook("Owned", "hc:1"), ExistingAuthorId) }, SkipOmnibusProfiles());
            if (tracked)
            {
                world.Store.Set(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked);
            }

            var result = world.Service.RemoteChildrenRequesting(ExistingAuthor(), OmnibusCatalog(), book => book.HardcoverBookId == "hc:2675413");

            var described = result.Select(Describe).ToList();
            Assert.That(described, Does.Contain("Ebook hc:2675413"));
            Assert.That(described, Has.No.Member("Ebook hc:777"));
            Assert.That(described, Has.Count.EqualTo(tracked ? 2 : 3));
        }

        [Test]
        public void scheduled_refresh_without_a_request_still_skips_the_omnibus()
        {
            var world = new RefreshWorld(new List<Book> { Kept(Ebook("Owned", "hc:1"), ExistingAuthorId) }, SkipOmnibusProfiles());

            var result = world.Service.RemoteChildren(ExistingAuthor(), OmnibusCatalog());

            Assert.That(result.Select(Describe), Has.No.Member("Ebook hc:2675413"));
        }

        // Switching

        [Test]
        public void switching_mode_stores_it_and_queues_a_refresh_once()
        {
            var (store, _) = CreateStore();
            var commands = new List<Command>();
            var queue = Proxy<IManageCommandQueue>.Create((name, args) =>
            {
                if (name == nameof(IManageCommandQueue.Push) && args[0] is Command command)
                {
                    commands.Add(command);
                }

                return Proxy<IManageCommandQueue>.Default;
            });
            var authors = Proxy<IAuthorService>.Create((name, args) => name == nameof(IAuthorService.GetAuthor)
                ? ((int)args[0] == ExistingAuthorId ? ExistingAuthor() : throw new NzbDrone.Core.Datastore.ModelNotFoundException(typeof(Author), (int)args[0]))
                : Proxy<IAuthorService>.Default);
            var service = new TalearrAuthorCatalogService(store, authors, queue, Log);

            Assert.That(service.SetMode(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked), Is.True);
            Assert.That(service.SetMode(ExistingAuthorId, TalearrAuthorCatalogMode.Tracked), Is.True, "no change, no second refresh");
            Assert.That(service.SetMode(404, TalearrAuthorCatalogMode.Tracked), Is.False);

            Assert.That(store.Get(ExistingAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Tracked));
            Assert.That(commands, Has.Count.EqualTo(1));
            var refresh = (RefreshAuthorCommand)commands.Single();
            Assert.That(refresh.AuthorId, Is.EqualTo(ExistingAuthorId));
            Assert.That(refresh.ForceRefresh, Is.True);

            Assert.That(service.SetMode(ExistingAuthorId, TalearrAuthorCatalogMode.Full), Is.True);
            Assert.That(store.Get(ExistingAuthorId), Is.EqualTo(TalearrAuthorCatalogMode.Full));
            Assert.That(commands, Has.Count.EqualTo(2));
        }

        // Helpers

        private static (TalearrAuthorCatalogStore Store, Dictionary<string, string> Values) CreateStore()
        {
            var repository = DispatchProxy.Create<IConfigRepository, CatalogConfigProxy>();
            var proxy = (CatalogConfigProxy)(object)repository;
            return (new TalearrAuthorCatalogStore(repository, Log), proxy.Values);
        }

        private static string Describe(Book book) => $"{book.MediaType} {book.HardcoverBookId}";

        private static Author ExistingAuthor()
        {
            return new Author
            {
                Id = ExistingAuthorId,
                Name = "Existing Author",
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AudiobookMonitored = true,
                EbookMetadataProfileId = 3
            };
        }

        private static Author Catalog()
        {
            return new Author
            {
                Name = "Existing Author",
                Books = new List<Book>
                {
                    Audiobook("Owned", "hc:1"),
                    Audiobook("Requested", "hc:2"),
                    Audiobook("Catalog", "hc:3")
                },
                Series = new List<Series>()
            };
        }

        private static MonitoringConfig RequestConfig(BookMediaType mediaType, string requestedId, bool tracked)
        {
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            var requested = requestedId == null ? null : new List<string> { requestedId };
            return new MonitoringConfig
            {
                AuthorName = "Existing Author",
                CreateAudiobook = isAudiobook,
                CreateEbook = !isAudiobook,
                AudiobookQualityProfileId = isAudiobook ? 2 : null,
                AudiobookMetadataProfileId = isAudiobook ? 1 : null,
                AudiobookRootFolderPath = isAudiobook ? "/audiobooks" : null,
                AudiobookMonitored = isAudiobook ? true : null,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                AudiobookMonitorExistingMode = isAudiobook && requested != null ? MonitorTypes.SpecificBook : null,
                AudiobookBooksToMonitor = isAudiobook ? requested : null,
                EbookQualityProfileId = isAudiobook ? null : 4,
                EbookMetadataProfileId = isAudiobook ? null : 3,
                EbookRootFolderPath = isAudiobook ? null : "/ebooks",
                EbookMonitored = isAudiobook ? null : true,
                EbookMonitorNewItems = NewItemMonitorTypes.None,
                EbookMonitorExistingMode = !isAudiobook && requested != null ? MonitorTypes.SpecificBook : null,
                EbookBooksToMonitor = isAudiobook ? null : requested,
                MonitorMode = requested != null ? MonitorTypes.SpecificBook : null,
                SpecificBookProviderIds = requested != null ? new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase) : null,
                SpecificBookMediaType = requested != null ? mediaType : null,
                TalearrTrackedCatalog = tracked
            };
        }

        private static Book Plain(Book book, int authorId = ExistingAuthorId)
        {
            book.AuthorId = authorId;
            book.AnyEditionOk = true;
            book.AddOptions = new AddBookOptions { AddType = BookAddType.Automatic };
            return book;
        }

        private static Book Kept(Book book, int authorId = ExistingAuthorId)
        {
            Plain(book, authorId);
            book.Id = 10;
            // Per-format flag only; the legacy Book.Monitored column stays false as in real data.
            book.SetMonitored(true);
            book.Monitored = false;
            return book;
        }

        private static Book Audiobook(string title, string hardcoverBookId) => Build(title, hardcoverBookId, BookMediaType.Audiobook);

        private static Book Ebook(string title, string hardcoverBookId) => Build(title, hardcoverBookId, BookMediaType.Ebook);

        private static Book Build(string title, string hardcoverBookId, BookMediaType mediaType)
        {
            var isAudiobook = mediaType == BookMediaType.Audiobook;
            return new Book
            {
                Title = title,
                CleanTitle = title.ToLowerInvariant(),
                TitleSlug = title.ToLowerInvariant().Replace(" ", "-"),
                MediaType = mediaType,
                HardcoverBookId = hardcoverBookId,
                BaseBookId = hardcoverBookId,
                AnyEditionOk = true,
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Title = title,
                        ForeignEditionId = $"{hardcoverBookId}:{(isAudiobook ? "audio" : "ebook")}",
                        ReadingFormatId = isAudiobook ? 2 : 3,
                        Language = "eng"
                    }
                }
            };
        }

        private static RootFolder Root(string path, FolderType type)
        {
            var root = new RootFolder { Path = path, FolderType = type };
            var settings = new MediaTypeSettings
            {
                QualityProfileId = type == FolderType.Audiobook ? 2 : 4,
                MetadataProfileId = type == FolderType.Audiobook ? 1 : 3,
                MonitorExistingBooks = false,
                MonitorNewItems = NewItemMonitorTypes.None
            };

            if (type == FolderType.Audiobook)
            {
                root.SetAudiobookSettings(settings);
            }
            else
            {
                root.SetEbookSettings(settings);
            }

            return root;
        }

        // Skip Omnibus on: drops omnibus books unless the caller's "requested" predicate keeps them.
        private static IMetadataProfileService SkipOmnibusProfiles()
        {
            return Proxy<IMetadataProfileService>.Create((name, args) =>
            {
                switch (name)
                {
                    case nameof(IMetadataProfileService.Exists):
                        return true;
                    case nameof(IMetadataProfileService.FilterBooks):
                        var isRequested = args.Length > 2 ? args[2] as Func<Book, bool> : null;
                        return (((Author)args[0])?.Books ?? new List<Book>())
                            .Where(book => !book.IsOmnibus || isRequested?.Invoke(book) == true)
                            .ToList();
                    default:
                        return Proxy<IMetadataProfileService>.Default;
                }
            });
        }

        private static Author OmnibusCatalog()
        {
            var requested = Ebook("Bound and Unbound", "hc:2675413");
            requested.IsOmnibus = true;
            var otherOmnibus = Ebook("Another Duology", "hc:777");
            otherOmnibus.IsOmnibus = true;
            return new Author
            {
                Name = "Existing Author",
                Books = new List<Book> { Ebook("Owned", "hc:1"), Ebook("Catalog", "hc:3"), requested, otherOmnibus },
                Series = new List<Series>()
            };
        }

        // What ProcessPendingImportsCommandHandler.BuildMonitoringConfig rebuilds from the pending
        // row: per-format targets only, no SpecificBook fields and no TalearrTrackedCatalog.
        private static MonitoringConfig PendingRetryConfig(string requestedId)
        {
            return new MonitoringConfig
            {
                CreateAudiobook = false,
                CreateEbook = true,
                EbookQualityProfileId = 4,
                EbookMetadataProfileId = 3,
                EbookRootFolderPath = "/ebooks",
                EbookMonitored = true,
                EbookMonitorNewItems = NewItemMonitorTypes.None,
                EbookMonitorExistingMode = MonitorTypes.SpecificBook,
                EbookBooksToMonitor = new List<string> { requestedId },
                QueueIfUnavailable = false,
                IsFromQueue = true
            };
        }

        private sealed class CapturingRefreshAuthorService : IRefreshAuthorService
        {
            public Func<Book, bool> IsRequested { get; private set; }

            public bool ReconcileAuthorBlob(Author localAuthor, Author authoritativeRemoteAuthor, Func<Book, bool> talearrIsRequested)
            {
                IsRequested = talearrIsRequested;
                return true;
            }
        }

        private static IMetadataProfileService PassThroughProfiles()
        {
            return Proxy<IMetadataProfileService>.Create((name, args) => name switch
            {
                nameof(IMetadataProfileService.Exists) => true,
                nameof(IMetadataProfileService.Get) => new MetadataProfile { Id = (int)args[0], Name = "Profile" },
                nameof(IMetadataProfileService.FilterBooks) => ((Author)args[0])?.Books?.ToList() ?? new List<Book>(),
                _ => Proxy<IMetadataProfileService>.Default
            });
        }

        private static IEditionService Editions(List<Edition> editions)
        {
            var nextId = 2000;
            return Proxy<IEditionService>.Create((name, args) =>
            {
                switch (name)
                {
                    case nameof(IEditionService.InsertMany) when args.Length == 1:
                        foreach (var edition in (List<Edition>)args[0])
                        {
                            edition.Id = nextId++;
                            editions.Add(edition);
                        }

                        return null;
                    case nameof(IEditionService.GetEditionsByBook):
                        var ids = args[0] is int single ? new List<int> { single } : ((IEnumerable<int>)args[0]).ToList();
                        return editions.Where(e => ids.Contains(e.BookId)).ToList();
                    default:
                        return Proxy<IEditionService>.Default;
                }
            });
        }

        private sealed class LibraryWorld
        {
            public LibraryWorld(Author remote, Author existingAuthor = null, List<Book> existingBooks = null, IMetadataProfileService profiles = null, IRefreshAuthorService refreshAuthorService = null)
            {
                var (store, _) = CreateStore();
                Store = store;
                var books = existingBooks ?? new List<Book>();
                var nextBookId = 1000;
                Author current = existingAuthor;

                var authors = Proxy<IAuthorService>.Create((name, args) =>
                {
                    switch (name)
                    {
                        case nameof(IAuthorService.FindByProviderId):
                            return current?.Id == ExistingAuthorId ? current : null;
                        case nameof(IAuthorService.AddAuthor):
                            current = (Author)args[0];
                            current.Id = NewAuthorId;
                            return current;
                        case nameof(IAuthorService.UpdateAuthor):
                        case nameof(IAuthorService.UpdateAuthorProgressiveSettings):
                            current = (Author)args[0];
                            return current;
                        case nameof(IAuthorService.GetAuthor):
                            return current?.Id == (int)args[0] ? current : null;
                        default:
                            return Proxy<IAuthorService>.Default;
                    }
                });

                var bookService = Proxy<IBookService>.Create((name, args) =>
                {
                    switch (name)
                    {
                        case nameof(IBookService.InsertMany) when args.Length == 1:
                            foreach (var book in (List<Book>)args[0])
                            {
                                book.Id = nextBookId++;
                                Inserted.Add(book);
                                books.Add(book);
                            }

                            return null;
                        case nameof(IBookService.GetBooksByAuthor):
                            return books.Where(b => b.AuthorId == (int)args[0]).ToList();
                        default:
                            return Proxy<IBookService>.Default;
                    }
                });

                Service = new AuthorLibraryService(
                    authors,
                    new StubAuthorInfo(remote),
                    bookService,
                    refreshSeriesService: null,
                    editionService: Editions(new List<Edition>()),
                    narratorLinkService: null,
                    metadataProfileService: profiles ?? PassThroughProfiles(),
                    qualityProfileService: new TestQualityProfileService(),
                    authorPathBuilder: Proxy<IBuildAuthorPaths>.Create((name, args) => name == nameof(IBuildAuthorPaths.BuildPath) ? "/authors/" + ((Author)args[0])?.Name : Proxy<IBuildAuthorPaths>.Default),
                    rootFolderService: new StubRootFolderService(Root("/audiobooks", FolderType.Audiobook), Root("/ebooks", FolderType.Ebook)),
                    commandQueueManager: null,
                    eventAggregator: Proxy<IEventAggregator>.Create((name, args) => Proxy<IEventAggregator>.Default),
                    pendingImportService: null,
                    mainDatabase: null,
                    importListExclusionService: null,
                    editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                    syncMetadataService: null,
                    logger: Log,
                    editionSelector: new EditionSelector(Log),
                    refreshAuthorService: refreshAuthorService,
                    catalogStore: store);
            }

            public TalearrAuthorCatalogStore Store { get; }
            public List<Book> Inserted { get; } = new List<Book>();
            public AuthorLibraryService Service { get; }
        }

        private sealed class RefreshWorld
        {
            public RefreshWorld(List<Book> localBooks, IMetadataProfileService profiles = null)
            {
                var (store, _) = CreateStore();
                Store = store;

                var books = Proxy<IBookService>.Create((name, args) => name switch
                {
                    nameof(IBookService.GetBooksForRefresh) => localBooks.ToList(),
                    nameof(IBookService.GetBooksByAuthor) => localBooks.ToList(),
                    _ => Proxy<IBookService>.Default
                });

                var nextEditionId = 100;
                var localEditions = localBooks
                    .SelectMany(book => (book.Editions ?? new List<Edition>()).Select(edition =>
                    {
                        edition.Id = nextEditionId++;
                        edition.BookId = book.Id;
                        return edition;
                    }))
                    .ToList();

                // Files are linked to editions, as in the database; hydration maps them to books.
                MediaFiles = Proxy<IMediaFileService>.Create((name, args) => name switch
                {
                    nameof(IMediaFileService.GetFilesByBooks) => Files.ToList(),
                    nameof(IMediaFileService.GetFilesByBook) => Files.Where(f => localEditions.Any(e => e.Id == f.EditionId && e.BookId == (int)args[0])).ToList(),
                    _ => Proxy<IMediaFileService>.Default
                });

                Service = new TestableRefreshAuthorService(books, Editions(localEditions), profiles ?? PassThroughProfiles(), MediaFiles, store);
            }

            public TalearrAuthorCatalogStore Store { get; }
            public List<BookFile> Files { get; } = new List<BookFile>();
            public IMediaFileService MediaFiles { get; }
            public TestableRefreshAuthorService Service { get; }

            public void AddFile(Book book)
            {
                Files.Add(new BookFile { Id = Files.Count + 1, EditionId = book.Editions.First().Id });
            }
        }

        private sealed class TestableRefreshAuthorService : RefreshAuthorService
        {
            public TestableRefreshAuthorService(IBookService bookService, IEditionService editionService, IMetadataProfileService profiles, IMediaFileService mediaFiles, ITalearrAuthorCatalogStore store)
                : base(authorInfo: null,
                    authorService: null,
                    bookService: bookService,
                    editionService: editionService,
                    metadataProfileService: profiles,
                    refreshBookService: null,
                    refreshSeriesService: null,
                    eventAggregator: null,
                    commandQueueManager: null,
                    mediaFileService: mediaFiles,
                    historyService: null,
                    rootFolderService: null,
                    checkIfAuthorShouldBeRefreshed: null,
                    monitorNewBookService: null,
                    configService: null,
                    importListExclusionService: null,
                    syncMetadataService: null,
                    syncQueueService: null,
                    rootFolderSettingsResolver: null,
                    logger: Log,
                    catalogStore: store)
            {
            }

            public List<Book> RemoteChildren(Author local, Author remote) => GetRemoteChildren(local, remote);

            // As ReconcileAuthorBlob sets it for the duration of an add's refresh.
            public List<Book> RemoteChildrenRequesting(Author local, Author remote, Func<Book, bool> isRequested)
            {
                var field = typeof(RefreshAuthorService).GetField("_talearrRequestedBooks", BindingFlags.NonPublic | BindingFlags.Instance);
                field.SetValue(this, isRequested);
                try
                {
                    return GetRemoteChildren(local, remote);
                }
                finally
                {
                    field.SetValue(this, null);
                }
            }
        }

        private sealed class TestableRefreshBookService : RefreshBookService
        {
            public TestableRefreshBookService(IMediaFileService mediaFileService)
                : base(bookService: null,
                    authorService: null,
                    rootFolderService: null,
                    editionService: null,
                    authorInfo: null,
                    bookInfo: null,
                    refreshEditionService: null,
                    mediaFileService: mediaFileService,
                    historyService: null,
                    eventAggregator: null,
                    checkIfBookShouldBeRefreshed: null,
                    editionSelector: new EditionSelector(Log),
                    editionMetadataProfileFilter: new EditionMetadataProfileFilter(new TestTermMatcherService()),
                    mediaCoverService: null,
                    logger: Log)
            {
            }

            public Book MatchRemote(Book local, List<Book> remote) => GetRemoteData(local, remote, null).Entity;

            public bool ShouldDeletePublic(Book local) => ShouldDelete(local);
        }

        private sealed class StubAuthorInfo : IProvideAuthorInfo
        {
            private readonly Author _author;

            public StubAuthorInfo(Author author)
            {
                _author = author;
            }

            public Author GetAuthorInfo(string chaptarrId, bool useCache = true) => _author;
            public RefreshResult RefreshAuthorInfo(string authorId, string etag = null, bool forceRefresh = false, string expectedPublishedETag = null, bool bypassEtag = false) => throw new NotImplementedException();
        }

        private sealed class StubRootFolderService : IRootFolderService
        {
            private readonly List<RootFolder> _rootFolders;

            public StubRootFolderService(params RootFolder[] rootFolders)
            {
                _rootFolders = rootFolders.ToList();
            }

            public List<RootFolder> All() => _rootFolders;
            public List<RootFolder> AllWithSpaceStats() => _rootFolders;
            public RootFolder Add(RootFolder rootFolder) => throw new NotImplementedException();
            public RootFolder Update(RootFolder rootFolder) => throw new NotImplementedException();
            public void Remove(int id) => throw new NotImplementedException();
            public RootFolder Get(int id) => throw new NotImplementedException();
            public List<RootFolder> AllForTag(int tagId) => throw new NotImplementedException();
            public RootFolder GetBestRootFolder(string path) => GetBestRootFolder(path, _rootFolders);
            public string GetBestRootFolderPath(string path) => GetBestRootFolder(path)?.Path;
            public string GetBestRootFolderPath(string path, List<RootFolder> allRootFolders) => GetBestRootFolder(path, allRootFolders)?.Path;

            public RootFolder GetBestRootFolder(string path, List<RootFolder> allRootFolders)
            {
                return (allRootFolders ?? new List<RootFolder>())
                    .FirstOrDefault(r => !string.IsNullOrWhiteSpace(path) &&
                                         path.StartsWith(r.Path.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));
            }
        }

        internal class CatalogConfigProxy : DispatchProxy
        {
            public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IConfigRepository.Get):
                        var key = (string)args[0];
                        return Values.TryGetValue(key, out var value) ? new Config { Key = key, Value = value } : null;
                    case nameof(IConfigRepository.Upsert):
                        Values[(string)args[0]] = (string)args[1];
                        return new Config { Key = (string)args[0], Value = (string)args[1] };
                    case "Delete":
                        if (args[0] is Config config)
                        {
                            Values.Remove(config.Key);
                        }

                        return null;
                    case "All":
                        return Values.Select(pair => new Config { Key = pair.Key, Value = pair.Value }).ToList();
                    default:
                        throw new NotImplementedException($"Test proxy does not implement IConfigRepository.{targetMethod?.Name}");
                }
            }
        }

        // Answers what the handler knows; anything else returns an empty value of the return type.
        public class Proxy<T> : DispatchProxy
            where T : class
        {
            public static readonly object Default = new object();

            public Func<string, object[], object> Handler { get; set; }

            public static T Create(Func<string, object[], object> handler)
            {
                var proxy = DispatchProxy.Create<T, Proxy<T>>();
                ((Proxy<T>)(object)proxy).Handler = handler;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                var result = Handler(targetMethod.Name, args);
                return ReferenceEquals(result, Default) ? EmptyValue(targetMethod.ReturnType) : result;
            }

            private static object EmptyValue(Type type)
            {
                if (type == typeof(void))
                {
                    return null;
                }

                if (type == typeof(Task))
                {
                    return Task.CompletedTask;
                }

                if (type.IsValueType)
                {
                    return Activator.CreateInstance(type);
                }

                if (type.IsGenericType && !type.IsInterface && typeof(IEnumerable).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null)
                {
                    return Activator.CreateInstance(type);
                }

                return null;
            }
        }
    }
}
