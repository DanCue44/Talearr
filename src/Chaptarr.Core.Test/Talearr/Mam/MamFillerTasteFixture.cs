using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Chaptarr.Core.Test.Talearr.Recordings;
using Chaptarr.Core.Test.Talearr.Seeding;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.MamFiller;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    // Talearr: filler taste preferences (preferred categories and authors, exclusions, the
    // preferred category search and the library import of preferred picks).
    public partial class MamFreeleechFillerFixture
    {
        private static JObject Tasted(int id, string catname, int? category = null, string author = null, int leechers = 5, int seeders = 3, int mainCat = 13)
        {
            var json = Torrent(id, leechers: leechers, seeders: seeders, mainCat: mainCat);
            json["catname"] = catname;
            if (category.HasValue)
            {
                json["category"] = category.Value;
            }

            if (author != null)
            {
                json["author_info"] = new JObject { ["77"] = author }.ToString(Newtonsoft.Json.Formatting.None);
            }

            return json;
        }

        private static TalearrMamFillerTasteProfile Taste(string preferred = "True Crime", string excluded = TalearrMamFillerTaste.DefaultExcludedCategories, string authors = "", bool onlyPreferred = false, params string[] library)
        {
            var settings = new MyAnonaMouseSettings
            {
                FillerPreferredCategories = preferred,
                FillerExcludedCategories = excluded,
                FillerPreferredAuthors = authors,
                FillerOnlyPreferred = onlyPreferred
            };

            return TalearrMamFillerTaste.Build(settings, library);
        }

        [Test]
        public void defaults_prefer_true_crime_and_exclude_religion()
        {
            var settings = new MyAnonaMouseSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.FillerPreferredCategories, Is.EqualTo("True Crime"));
                Assert.That(settings.FillerExcludedCategories, Is.EqualTo("Religion, Spirituality, Christian, Pol/Soc/Relig"));
                Assert.That(settings.FillerPreferLibraryAuthors, Is.True);
                Assert.That(settings.FillerPreferredAuthors, Is.Empty);
                Assert.That(settings.FillerOnlyPreferred, Is.False);
                Assert.That(settings.FillerImportPreferred, Is.True);
            });
        }

        [TestCase("Audiobooks - True Crime", "True Crime", true)]
        [TestCase("Ebooks - True Crime", "true crime", true)]
        [TestCase("AUDIOBOOKS - TRUE CRIME", "True  Crime", true)]
        [TestCase("Audiobooks - Crime/Thriller", "True Crime", false)]
        [TestCase("Ebooks - Pol/Soc/Relig", "Pol/Soc/Relig", true)]
        [TestCase("Ebooks - Religion/Spirituality", "Spirituality", true)]
        [TestCase("Audiobooks - Christian Fiction", "Christian", true)]
        [TestCase("Ebooks - Martial Arts", "Art", false)]
        [TestCase(null, "True Crime", false)]
        public void category_names_match_by_whole_words_in_any_case(string categoryName, string entry, bool expected)
        {
            Assert.That(TalearrMamFillerTaste.CategoryMatches(categoryName, entry), Is.EqualTo(expected));
        }

        [TestCase("J.K. Rowling", "J K Rowling")]
        [TestCase("JK Rowling", "j. k. rowling")]
        [TestCase("Rowling, J.K.", "J.K. Rowling")]
        [TestCase("Ann Rulé", "ann rule")]
        [TestCase("Harry Connick, Jr.", "Harry Connick Jr")]
        public void author_names_are_normalized(string a, string b)
        {
            Assert.That(TalearrMamFillerTaste.AuthorKey(a), Is.EqualTo(TalearrMamFillerTaste.AuthorKey(b)));
        }

        [Test]
        public void parse_reads_category_name_id_and_authors()
        {
            var candidate = TalearrMamFillerRules.Parse(Tasted(1, "Audiobooks - True Crime", 100, "Ann Rule"));

            Assert.Multiple(() =>
            {
                Assert.That(candidate.CategoryId, Is.EqualTo(100));
                Assert.That(candidate.CategoryName, Is.EqualTo("Audiobooks - True Crime"));
                Assert.That(candidate.Authors, Is.EqualTo(new[] { "Ann Rule" }));
            });
        }

        [Test]
        public void known_category_ids_are_used_when_catname_is_missing()
        {
            var json = Torrent(1);
            json["category"] = 104;

            var reason = TalearrMamFillerTaste.PreferredReason(TalearrMamFillerRules.Parse(json), Taste());

            Assert.That(reason, Is.EqualTo("Category: Ebooks - True Crime"));
        }

        [Test]
        public void preferred_category_ids_cover_both_true_crime_categories_and_seen_ids()
        {
            Assert.That(TalearrMamFillerTaste.PreferredCategoryIds(new[] { "True Crime" }), Is.EqualTo(new[] { 100, 104 }));
            Assert.That(TalearrMamFillerTaste.PreferredCategoryIds(new[] { "True Crime" }, new Dictionary<int, string> { [130] = "Audiobooks - True Crime (Podcast)" }), Is.EqualTo(new[] { 100, 104, 130 }));
            Assert.That(TalearrMamFillerTaste.PreferredCategoryIds(new string[0]), Is.Empty);
        }

        [Test]
        public void ranking_puts_preferred_matches_first_then_the_usual_order()
        {
            var taste = Taste(library: "Michelle McNamara");
            var candidates = new[]
            {
                Tasted(1, "Audiobooks - Romance", leechers: 40),
                Tasted(2, "Ebooks - True Crime", mainCat: 14, leechers: 1),
                Tasted(3, "Audiobooks - Mystery", author: "Michelle McNamara", leechers: 2),
                Tasted(4, "Audiobooks - Romance", leechers: 20)
            }.Select(TalearrMamFillerRules.Parse).ToList();

            foreach (var candidate in candidates)
            {
                candidate.PreferredReason = TalearrMamFillerTaste.PreferredReason(candidate, taste);
            }

            Assert.That(TalearrMamFillerRules.Rank(candidates, true).Select(c => c.TorrentId), Is.EqualTo(new[] { "3", "2", "1", "4" }));
            Assert.That(candidates[1].PreferredReason, Is.EqualTo("Category: Ebooks - True Crime"));
            Assert.That(candidates[2].PreferredReason, Is.EqualTo("Author in your library: Michelle McNamara"));
        }

        [Test]
        public void exclusions_beat_preferences_and_only_preferred_rejects_the_rest()
        {
            var taste = Taste(authors: "Scott Hahn", onlyPreferred: true);

            Assert.Multiple(() =>
            {
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(1, "Audiobooks - Pol/Soc/Relig", author: "Scott Hahn")), taste), Is.EqualTo("excluded category"));
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(2, "Ebooks - Christian Fiction")), taste), Is.EqualTo("excluded category"));
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(3, "Audiobooks - Romance")), taste), Is.EqualTo("not preferred"));
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(4, "Audiobooks - Romance", author: "scott  hahn")), taste), Is.Null);
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(5, "Audiobooks - True Crime")), taste), Is.Null);
                Assert.That(TalearrMamFillerTaste.Reject(TalearrMamFillerRules.Parse(Tasted(6, "Audiobooks - Romance")), Taste(excluded: string.Empty)), Is.Null, "empty lists keep 0.18.0 behavior");
            });
        }

        [Test]
        public void preferred_search_asks_mam_for_the_preferred_category_ids()
        {
            _settings.FillerDryRun = true;
            _settings.FillerPreferredCategories = "True Crime";
            ReplySearch(Tasted(1, "Audiobooks - Romance", 46, leechers: 30));
            ReplySearch(Tasted(2, "Audiobooks - True Crime", 100, "Ann Rule", leechers: 1));

            var status = Service.Run(true);

            var body = JObject.Parse(Encoding.UTF8.GetString(_http.Requests[1].ContentData));
            Assert.Multiple(() =>
            {
                Assert.That(_http.Requests, Has.Count.EqualTo(2));
                Assert.That(body["tor"]["cat"].Select(t => (string)t), Is.EqualTo(new[] { "100", "104" }));
                Assert.That(body["tor"]["main_cat"], Is.Null);
                Assert.That((string)body["tor"]["searchType"], Is.EqualTo("fl"));
                Assert.That(status.Preview.Select(p => p.TorrentId), Is.EqualTo(new[] { "2", "1" }));
                Assert.That(status.Preview[0].Preferred, Is.True);
                Assert.That(status.Preview[0].PreferredReason, Is.EqualTo("Category: Audiobooks - True Crime"));
                Assert.That(status.Preview[0].Author, Is.EqualTo("Ann Rule"));
                Assert.That(status.Preview[1].Preferred, Is.False);
                Assert.That(status.LastResult, Does.Contain("1 preferred"));
            });
        }

        [Test]
        public void library_authors_are_preferred_and_excluded_categories_skipped()
        {
            _settings.FillerDryRun = true;
            _libraryAuthors.Add("J.K. Rowling");
            ReplySearch(
                Tasted(1, "Audiobooks - Romance", leechers: 30),
                Tasted(2, "Audiobooks - Fantasy", author: "J K Rowling", leechers: 1),
                Tasted(3, "Audiobooks - Pol/Soc/Relig", leechers: 50));

            var status = Service.Run(true);

            Assert.Multiple(() =>
            {
                Assert.That(status.Preview.Select(p => p.TorrentId), Is.EqualTo(new[] { "2", "1" }));
                Assert.That(status.Preview[0].PreferredReason, Is.EqualTo("Author in your library: J K Rowling"));
                Assert.That(status.LastResult, Does.Contain("1 excluded category"));
            });
        }

        [Test]
        public void library_authors_are_ignored_when_the_option_is_off()
        {
            _settings.FillerDryRun = true;
            _settings.FillerPreferLibraryAuthors = false;
            _libraryAuthors.Add("J.K. Rowling");
            ReplySearch(Tasted(1, "Audiobooks - Romance", leechers: 30), Tasted(2, "Audiobooks - Fantasy", author: "J K Rowling", leechers: 1));

            var status = Service.Run(true);

            Assert.That(status.Preview.Select(p => p.TorrentId), Is.EqualTo(new[] { "1", "2" }));
            Assert.That(_library.CallCount("LibraryAuthorNames"), Is.Zero);
        }

        [Test]
        public void only_preferred_leaves_slots_empty()
        {
            _settings.FillerOnlyPreferred = true;
            _settings.FillerPreferredAuthors = "Ann Rule";
            ReplySearch(Tasted(1, "Audiobooks - Romance", leechers: 30), Tasted(2, "Audiobooks - Romance", author: "Ann Rule"));
            ReplyTorrent("A");

            var status = Service.Run(true);

            Assert.Multiple(() =>
            {
                Assert.That(_added.Select(a => a.Hash), Is.EqualTo(new[] { "HASHA" }));
                Assert.That(_store.Torrents.Keys, Is.EqualTo(new[] { "2" }));
                Assert.That(status.LastResult, Does.Contain("1 not preferred"));
            });
        }

        [Test]
        public void only_preferred_picks_are_queued_for_the_library_import()
        {
            _settings.FillerPreferredAuthors = "Ann Rule";
            ReplySearch(Tasted(1, "Audiobooks - Romance", leechers: 30), Tasted(2, "Audiobooks - Romance", author: "Ann Rule"));
            ReplyTorrent("A");
            ReplyTorrent("B");

            Service.Run(true);

            Assert.Multiple(() =>
            {
                Assert.That(_store.Torrents.Keys.OrderBy(k => k), Is.EqualTo(new[] { "1", "2" }));
                Assert.That(_store.Torrents.Values.Select(t => t.Category).Distinct(), Is.EqualTo(new[] { "mam-seed" }), "both keep seeding in the filler category");
                Assert.That(_store.Picks.Keys, Is.EqualTo(new[] { "2" }));
                Assert.That(_store.Picks["2"].LibraryState, Is.EqualTo(TalearrFillerPick.StateWaiting));
                Assert.That(_store.Picks["2"].Author, Is.EqualTo("Ann Rule"));
            });
        }

        [Test]
        public void no_library_import_when_the_option_is_off_or_a_dry_run()
        {
            _settings.FillerPreferredAuthors = "Ann Rule";
            _settings.FillerImportPreferred = false;
            ReplySearch(Tasted(2, "Audiobooks - Romance", author: "Ann Rule"));
            ReplyTorrent("A");

            Service.Run(true);

            Assert.That(_store.Torrents.Keys, Is.EqualTo(new[] { "2" }));
            Assert.That(_store.Picks, Is.Empty);

            _settings.FillerImportPreferred = true;
            _settings.FillerDryRun = true;
            ReplySearch(Tasted(3, "Audiobooks - Romance", author: "Ann Rule"));

            Service.Run(true);

            Assert.That(_store.Picks, Is.Empty);
        }

        [Test]
        public void open_picks_are_advanced_each_hour_with_the_client_download()
        {
            _store.Save(new TalearrFillerTorrent { TorrentId = "2", ClientHash = "AAA", Category = "mam-seed", State = TalearrFillerTorrent.StateAdded, AddedAt = _now.AddHours(-1) });
            _store.SavePick(new TalearrFillerPick { TorrentId = "2", Title = "Book 2", LibraryState = TalearrFillerPick.StateWaiting });
            _store.SavePick(new TalearrFillerPick { TorrentId = "3", Title = "Book 3", LibraryState = TalearrFillerPick.StateImported });
            _store.Status = new TalearrMamFillerStatus { LastRunAt = _now, Preview = new List<TalearrMamFillerPreviewItem> { new TalearrMamFillerPreviewItem { TorrentId = "2" } } };
            var download = new TalearrFillerDownload { Completed = true, OutputPath = "/downloads/mam-seed/Book 2" };
            var advanced = new List<(string Id, TalearrFillerDownload Download)>();
            _client
                .On("GetCategoryTorrents", _ => new List<NzbDrone.Core.Talearr.Seeding.TalearrSeedingClientTorrent> { new NzbDrone.Core.Talearr.Seeding.TalearrSeedingClientTorrent { DownloadId = "AAA" } })
                .On("GetFillerDownload", args => (string)args[0] == "AAA" && (string)args[1] == "mam-seed" ? download : null);
            _library.On("Advance", args =>
            {
                var pick = (TalearrFillerPick)args[0];
                advanced.Add((pick.TorrentId, (TalearrFillerDownload)args[1]));
                pick.LibraryState = TalearrFillerPick.StateImported;
                pick.LibraryMessage = "Imported";
                return true;
            });

            // Not due: the filler itself does not run, the housekeeping does.
            Service.Run(false);

            Assert.Multiple(() =>
            {
                Assert.That(_http.Requests, Is.Empty);
                Assert.That(advanced, Has.Count.EqualTo(1));
                Assert.That(advanced[0].Id, Is.EqualTo("2"));
                Assert.That(advanced[0].Download, Is.SameAs(download));
                Assert.That(_store.Picks["2"].LibraryState, Is.EqualTo(TalearrFillerPick.StateImported));
                Assert.That(_store.Status.Preview.Single().LibraryState, Is.EqualTo(TalearrFillerPick.StateImported));
                Assert.That(_store.Torrents["2"].State, Is.EqualTo(TalearrFillerTorrent.StateAdded), "still a tracked filler torrent");
            });
        }

        [Test]
        public void filler_search_payload_without_categories_is_unchanged()
        {
            var plain = MyAnonaMouse.BuildFillerSearchPayload(0);
            var filtered = MyAnonaMouse.BuildFillerSearchPayload(1, new[] { 104, 100, 100 });
            var plainTor = (Dictionary<string, object>)plain["tor"];
            var filteredTor = (Dictionary<string, object>)filtered["tor"];

            Assert.Multiple(() =>
            {
                Assert.That(plainTor["cat"], Is.EqualTo(new[] { "0" }));
                Assert.That(plainTor["main_cat"], Is.EqualTo(new[] { "13", "14" }));
                Assert.That(filteredTor["cat"], Is.EqualTo(new[] { "100", "104" }));
                Assert.That(filteredTor.ContainsKey("main_cat"), Is.False);
                Assert.That(filteredTor["startNumber"], Is.EqualTo("100"));
            });
        }

        [Test]
        public void store_round_trips_picks_in_talearr_db_without_a_schema_change()
        {
            using var folder = new TalearrTestFolder();
            var database = folder.CreateDatabase();
            var store = new TalearrMamFillerStore(database, LogManager.GetCurrentClassLogger());

            store.SaveStatus(new TalearrMamFillerStatus { LastResult = "ok" });
            store.SavePick(new TalearrFillerPick { TorrentId = "7", Title = "I'll Be Gone in the Dark", Author = "Michelle McNamara", LibraryState = TalearrFillerPick.StateWaiting });
            store.SavePick(new TalearrFillerPick { TorrentId = "7", Title = "I'll Be Gone in the Dark", LibraryState = TalearrFillerPick.StateAdding, BookProviderId = "hc:1" });

            var pick = store.AllPicks().Single();
            Assert.Multiple(() =>
            {
                Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateAdding));
                Assert.That(pick.BookProviderId, Is.EqualTo("hc:1"));
                Assert.That(store.GetStatus().LastResult, Is.EqualTo("ok"));
                Assert.That(database.SchemaVersion, Is.EqualTo(8));
            });
        }
    }

    // Talearr: the library side of "Filler Import Preferred Picks", with fakes.
    [TestFixture]
    public class MamFillerLibraryFixture
    {
        private Dictionary<int, Book> _books;
        private Dictionary<int, List<BookFile>> _files;
        private List<Book> _searchResults;
        private List<(string AuthorId, MonitoringConfig Config)> _enqueued;
        private List<(string Path, ImportMode Mode, Author Author, RemoteBook RemoteBook)> _imports;
        private List<ImportResult> _importResults;
        private List<Command> _pushed;
        private List<(int[] Ids, string Format)> _monitored;
        private Author _author;
        private TalearrMamFillerLibraryService _service;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _now = new DateTime(2026, 10, 17, 22, 0, 0, DateTimeKind.Utc);
            _author = new Author { Id = 5, Name = "Michelle McNamara", Aliases = new List<string> { "M. McNamara" } };
            _books = new Dictionary<int, Book>();
            _files = new Dictionary<int, List<BookFile>>();
            _searchResults = new List<Book>
            {
                new Book { Title = "I'll Be Gone in the Dark", HardcoverBookId = "hc:11", Author = new Author { Name = "Michelle McNamara", HardcoverAuthorId = "hc:22" } }
            };
            _enqueued = new List<(string, MonitoringConfig)>();
            _imports = new List<(string, ImportMode, Author, RemoteBook)>();
            _importResults = new List<ImportResult>();
            _pushed = new List<Command>();
            _monitored = new List<(int[], string)>();

            SeedingFake<IAuthorService>.Create(out var authors)
                .On("GetAllAuthors", _ => new List<Author> { _author, new Author { Name = "Ann Rule" } })
                .On("GetAuthor", _ => _author)
                .On("UpdateAuthor", args => args[0]);
            SeedingFake<IBookService>.Create(out var books)
                .On("FindByProviderId", args => _books.Values.FirstOrDefault(b => b.HardcoverBookId == (string)args[1] && (args.Length < 3 || b.MediaType == (BookMediaType)args[2])))
                .On("SetMonitoredForMediaType", args =>
                {
                    _monitored.Add((((IEnumerable<int>)args[0]).ToArray(), (string)args[1]));
                    return null;
                });
            SeedingFake<ISearchForNewBook>.Create(out var search).On("SearchForNewBook", _ => _searchResults);
            SeedingFake<IMediaFileService>.Create(out var files).On("GetFilesByBook", args => _files.TryGetValue((int)args[0], out var list) ? list : new List<BookFile>());
            SeedingFake<IImportListExclusionService>.Create(out var exclusions).On("All", _ => new List<ImportListExclusion>());
            SeedingFake<IPendingAuthorImportService>.Create(out var pending).On("EnqueueAsync", args =>
            {
                _enqueued.Add(((string)args[0], (MonitoringConfig)args[1]));
                return Task.FromResult(_enqueued.Count);
            });
            var audio = new RootFolder { Id = 4, Path = "/audiobooks" };
            SeedingFake<IRootFolderService>.Create(out var roots).On("All", _ => new List<RootFolder> { audio });
            SeedingFake<IRootFolderSettingsResolver>.Create(out var resolver).On("ResolveSettings", _ =>
                new ResolvedRootFolderSettings { IsConfigured = true, QualityProfileId = 2, MetadataProfileId = 1 });
            SeedingFake<IConfigService>.Create(out var config);
            SeedingFake<IManageCommandQueue>.Create(out var queue).On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });
            SeedingFake<IDownloadedBooksImportService>.Create(out var import).On("ProcessPath", args =>
            {
                _imports.Add(((string)args[0], (ImportMode)args[1], (Author)args[2], (RemoteBook)args[4]));
                return _importResults;
            });

            _service = new TalearrMamFillerLibraryService(authors, books, search, files, exclusions, pending, roots, resolver, config, queue, import, LogManager.GetCurrentClassLogger());
        }

        private static TalearrFillerPick Pick(string state = TalearrFillerPick.StateWaiting)
        {
            return new TalearrFillerPick { TorrentId = "9", Title = "I'll Be Gone in the Dark", Author = "Michelle McNamara", IsAudiobook = true, PreferredReason = "Category: Audiobooks - True Crime", LibraryState = state };
        }

        private static TalearrFillerDownload Done => new TalearrFillerDownload { Completed = true, OutputPath = "/downloads/mam-seed/Gone" };

        private Book LocalBook(bool monitored = false)
        {
            var book = new Book { Id = 31, Title = "I'll Be Gone in the Dark", HardcoverBookId = "hc:11", MediaType = BookMediaType.Audiobook, Author = _author, AudiobookMonitored = monitored };
            _books[book.Id] = book;
            return book;
        }

        private ImportResult Imported()
        {
            return new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Path = "/downloads/mam-seed/Gone/01.m4b" }), ImportResultType.Imported);
        }

        [Test]
        public void library_author_names_include_aliases()
        {
            Assert.That(_service.LibraryAuthorNames(), Is.EquivalentTo(new[] { "Michelle McNamara", "M. McNamara", "Ann Rule" }));
        }

        [Test]
        public void nothing_happens_until_the_download_has_finished()
        {
            var pick = Pick();

            Assert.That(_service.Advance(pick, new TalearrFillerDownload { Completed = false }, _now), Is.False);
            Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateWaiting));
            Assert.That(_enqueued, Is.Empty);
            Assert.That(_imports, Is.Empty);
        }

        [Test]
        public void a_torrent_gone_from_the_client_closes_the_pick()
        {
            var pick = Pick();

            Assert.That(_service.Advance(pick, null, _now), Is.True);
            Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateGone));
            Assert.That(_imports, Is.Empty);
        }

        [Test]
        public void a_new_author_is_added_with_only_that_book_monitored_and_no_search()
        {
            var pick = Pick();

            _service.Advance(pick, Done, _now);

            var (authorId, config) = _enqueued.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateAdding));
                Assert.That(pick.BookProviderId, Is.EqualTo("hc:11"));
                Assert.That(authorId, Is.EqualTo("hc:22"));
                Assert.That(config.CreateAudiobook, Is.True);
                Assert.That(config.CreateEbook, Is.False);
                Assert.That(config.MonitorMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
                Assert.That(config.AudiobookBooksToMonitor, Is.EqualTo(new[] { "hc:11" }));
                Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
                Assert.That(config.SearchForMissingBooks, Is.False);
                Assert.That(config.AudiobookBooksToSearch, Is.Null);
                Assert.That(config.AudiobookRootFolderPath, Is.EqualTo("/audiobooks"));
                Assert.That(_pushed.Single(), Is.InstanceOf<NzbDrone.Core.Books.Commands.ProcessPendingImportsCommand>());
                Assert.That(_imports, Is.Empty, "the book is not in the library yet");
            });

            // Next pass: the author add has finished, so the download is imported by copy.
            LocalBook();
            _importResults.Add(Imported());

            _service.Advance(pick, Done, _now.AddHours(1));

            var import = _imports.Single();
            Assert.Multiple(() =>
            {
                Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateImported));
                Assert.That(pick.BookId, Is.EqualTo(31));
                Assert.That(import.Path, Is.EqualTo("/downloads/mam-seed/Gone"));
                Assert.That(import.Mode, Is.EqualTo(ImportMode.Copy), "copy or hardlink, never move: the torrent keeps seeding");
                Assert.That(import.Author, Is.SameAs(_author));
                Assert.That(import.RemoteBook.Books.Single().Id, Is.EqualTo(31));
            });
        }

        [Test]
        public void a_book_already_in_the_library_is_monitored_and_imported_in_one_pass()
        {
            LocalBook();
            _importResults.Add(Imported());
            var pick = Pick();

            _service.Advance(pick, Done, _now);

            Assert.Multiple(() =>
            {
                Assert.That(_enqueued, Is.Empty);
                Assert.That(_monitored.Single().Ids, Is.EqualTo(new[] { 31 }));
                Assert.That(_monitored.Single().Format, Is.EqualTo("audiobook"));
                Assert.That(_imports.Single().Mode, Is.EqualTo(ImportMode.Copy));
                Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateImported));
            });
        }

        [Test]
        public void a_book_that_already_has_files_is_left_seed_only()
        {
            LocalBook(monitored: true);
            _files[31] = new List<BookFile> { new BookFile { MediaType = "audiobook" } };
            var pick = Pick();

            _service.Advance(pick, Done, _now);

            Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateInLibrary));
            Assert.That(_imports, Is.Empty);
            Assert.That(_monitored, Is.Empty);
        }

        [Test]
        public void no_metadata_match_means_seed_only_and_no_import()
        {
            _searchResults.Clear();
            var pick = Pick();

            _service.Advance(pick, Done, _now);

            Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateNoMatch));
            Assert.That(_enqueued, Is.Empty);
            Assert.That(_imports, Is.Empty);
        }

        [Test]
        public void a_failed_import_is_retried_then_given_up()
        {
            LocalBook(monitored: true);
            _importResults.Add(new ImportResult(new ImportDecision<LocalBook>(new LocalBook(), new NzbDrone.Core.DecisionEngine.Rejection("Wrong book")), "Wrong book"));
            var pick = Pick();

            for (var i = 0; i < TalearrMamFillerLibraryService.MaxImportAttempts; i++)
            {
                _service.Advance(pick, Done, _now.AddHours(i));
            }

            Assert.That(_imports, Has.Count.EqualTo(TalearrMamFillerLibraryService.MaxImportAttempts));
            Assert.That(pick.LibraryState, Is.EqualTo(TalearrFillerPick.StateFailed));
            Assert.That(pick.LibraryMessage, Does.Contain("Wrong book"));
            Assert.That(_service.Advance(pick, Done, _now.AddDays(1)), Is.False, "closed picks stay closed");
        }
    }
}
