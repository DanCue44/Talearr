using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Soulseek;

namespace Chaptarr.Core.Test.Talearr.Soulseek
{
    public class FakeSlskdClient : ITalearrSlskdClient
    {
        public List<SlskdSearchResponse> Responses { get; set; } = new List<SlskdSearchResponse>();
        public List<string> SearchTexts { get; } = new List<string>();
        public List<string> DeletedSearches { get; } = new List<string>();
        public List<(string User, List<SlskdQueueFile> Files, string Destination)> Enqueued { get; } = new List<(string, List<SlskdQueueFile>, string)>();
        public Dictionary<string, List<SlskdTransfer>> Transfers { get; } = new Dictionary<string, List<SlskdTransfer>>(StringComparer.OrdinalIgnoreCase);
        public List<(string User, string Id, bool Remove)> Cancelled { get; } = new List<(string, string, bool)>();
        public bool SupportsBatches { get; set; } = true;

        public SlskdApplicationState GetApplication(TalearrSoulseekSettings settings)
        {
            return new SlskdApplicationState { Version = new SlskdVersion { Full = "0.26.1" }, Server = new SlskdServerState { IsLoggedIn = true } };
        }

        public SlskdSearch StartSearch(TalearrSoulseekSettings settings, string id, string searchText)
        {
            SearchTexts.Add(searchText);
            return new SlskdSearch { Id = id };
        }

        public SlskdSearch GetSearch(TalearrSoulseekSettings settings, string id)
        {
            return new SlskdSearch { Id = id, IsComplete = true };
        }

        public List<SlskdSearchResponse> GetSearchResponses(TalearrSoulseekSettings settings, string id)
        {
            return Responses;
        }

        public void DeleteSearch(TalearrSoulseekSettings settings, string id)
        {
            DeletedSearches.Add(id);
        }

        public SlskdEnqueueResult Enqueue(TalearrSoulseekSettings settings, string username, List<SlskdQueueFile> files, string batchId, string destination)
        {
            Enqueued.Add((username, files, SupportsBatches ? destination : null));
            Transfers[username] = files.Select((f, i) => new SlskdTransfer { Id = "t" + i, Username = username, Filename = f.Filename, Size = f.Size, State = "Queued, Remotely" }).ToList();
            return new SlskdEnqueueResult { UsedBatchDestination = SupportsBatches, Destination = SupportsBatches ? destination : null };
        }

        public List<SlskdTransfer> GetDownloads(TalearrSoulseekSettings settings, string username)
        {
            return Transfers.TryGetValue(username, out var list) ? list : new List<SlskdTransfer>();
        }

        public void CancelDownload(TalearrSoulseekSettings settings, string username, string transferId, bool remove)
        {
            Cancelled.Add((username, transferId, remove));
        }

        public void SetState(string username, string state)
        {
            foreach (var t in Transfers[username])
            {
                t.State = state;
                t.BytesTransferred = state.Contains("Succeeded") ? t.Size : 0;
            }
        }
    }

    [TestFixture]
    public class TalearrSoulseekServiceFixture
    {
        private string _root;
        private Dictionary<string, string> _rows;
        private FakeSlskdClient _client;
        private TalearrSoulseekSettingsService _settings;
        private TalearrSoulseekGrabStore _store;
        private TalearrSoulseekService _service;
        private Author _author;
        private Book _book;
        private List<(string Path, ImportMode Mode, DownloadClientItem Item, RemoteBook RemoteBook)> _imports;
        private Func<List<ReleaseInfo>, List<DownloadDecision>> _decide;
        private ImportResultType _importResult;
        private int _configWritesOutsideTalearr;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-slskd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _rows = new Dictionary<string, string>();
            _configWritesOutsideTalearr = 0;
            var (repository, repoFake) = Fake<IConfigRepository>.Create();
            repoFake.On(nameof(IConfigRepository.Get), args => _rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    var key = (string)args[0];
                    if (!key.StartsWith("talearr.soulseek.", StringComparison.Ordinal))
                    {
                        _configWritesOutsideTalearr++;
                    }

                    _rows[key] = (string)args[1];
                    return new Config { Key = key, Value = (string)args[1] };
                })
                .On(nameof(IConfigRepository.All), _ => _rows.Select(kv => new Config { Key = kv.Key, Value = kv.Value }).ToList())
                .On(nameof(IConfigRepository.Delete), args =>
                {
                    if (args[0] is Config c)
                    {
                        _rows.Remove(c.Key);
                    }

                    return null;
                });

            var logger = LogManager.GetCurrentClassLogger();
            _settings = new TalearrSoulseekSettingsService(repository, logger);
            _settings.Save(new TalearrSoulseekSettings { Enabled = true, Url = "http://slskd:5030", ApiKey = "secret", DownloadsPath = _root });
            _store = new TalearrSoulseekGrabStore(repository, logger);
            _client = new FakeSlskdClient();

            _author = new Author { Id = 7, Name = "Frank Herbert" };
            _book = new Book { Id = 42, AuthorId = 7, Title = "Dune", MediaType = BookMediaType.Audiobook, Editions = new List<Edition>() };
            _book.Author = _author;

            var (books, booksFake) = Fake<IBookService>.Create();
            booksFake.On(nameof(IBookService.GetBook), args => (int)args[0] == 42 ? _book : null)
                .On(nameof(IBookService.GetBooksByAuthor), _ => new List<Book> { _book });

            var (authors, authorsFake) = Fake<IAuthorService>.Create();
            authorsFake.On(nameof(IAuthorService.GetAuthor), _ => _author);

            _decide = releases => releases.Select(r => new DownloadDecision(RemoteBookFor(r))).ToList();
            var (maker, makerFake) = Fake<IMakeDownloadDecision>.Create();
            makerFake.On(nameof(IMakeDownloadDecision.GetSearchDecision), args =>
            {
                Assert.That(args[1], Is.InstanceOf<BookSearchCriteria>());
                return _decide((List<ReleaseInfo>)args[0]);
            });

            var (prioritizer, prioritizerFake) = Fake<IPrioritizeDownloadDecision>.Create();
            prioritizerFake.On(nameof(IPrioritizeDownloadDecision.PrioritizeDecisions), args => ((List<DownloadDecision>)args[0]).OrderBy(d => d.RemoteBook.Release.Size).ToList());

            _imports = new List<(string, ImportMode, DownloadClientItem, RemoteBook)>();
            _importResult = ImportResultType.Imported;
            var (import, importFake) = Fake<IDownloadedBooksImportService>.Create();
            importFake.On(nameof(IDownloadedBooksImportService.ProcessPath), args =>
            {
                _imports.Add(((string)args[0], (ImportMode)args[1], (DownloadClientItem)args[3], (RemoteBook)args[4]));
                if (_importResult == ImportResultType.Imported)
                {
                    foreach (var file in ((DownloadClientItem)args[3]).FilePaths)
                    {
                        File.Delete(file);
                    }

                    return new List<ImportResult> { new ImportResult(new ImportDecision<LocalBook>(new LocalBook())) };
                }

                return new List<ImportResult> { new ImportResult(new ImportDecision<LocalBook>(new LocalBook()), "Wrong book") };
            });

            var (disk, diskFake) = Fake<IDiskProvider>.Create();
            diskFake.On(nameof(IDiskProvider.FolderExists), args => Directory.Exists((string)args[0]))
                .On(nameof(IDiskProvider.GetFiles), args => Directory.GetFiles((string)args[0], "*", (bool)args[1] ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                .On(nameof(IDiskProvider.GetDirectories), args => Directory.GetDirectories((string)args[0]))
                .On(nameof(IDiskProvider.DeleteFolder), args =>
                {
                    Directory.Delete((string)args[0], (bool)args[1]);
                    return null;
                });

            _service = new TalearrSoulseekService(_settings, _client, _store, books, authors, maker, prioritizer, import, disk, new CacheManager(), logger)
            {
                Sleep = _ => { }
            };
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private RemoteBook RemoteBookFor(ReleaseInfo release)
        {
            return new RemoteBook { Release = release, Author = _author, Books = new List<Book> { _book }, ParsedBookInfo = new ParsedBookInfo { ReleaseTitle = release.Title } };
        }

        private void PeersOffer(params SlskdSearchResponse[] peers)
        {
            _client.Responses = peers.ToList();
        }

        private static SlskdSearchResponse DunePeer(string user)
        {
            return TalearrSoulseekCandidatesFixture.Peer(user,
                TalearrSoulseekCandidatesFixture.File($@"Audiobooks\Frank Herbert\Dune ({user})\01.mp3", 1000),
                TalearrSoulseekCandidatesFixture.File($@"Audiobooks\Frank Herbert\Dune ({user})\02.mp3", 1000));
        }

        private void WriteDownloadedFiles(string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "01.mp3"), "a");
            File.WriteAllText(Path.Combine(folder, "02.mp3"), "b");
        }

        [Test]
        public void search_judges_candidates_with_the_decision_engine_and_cleans_up()
        {
            PeersOffer(DunePeer("alice"), DunePeer("bob"));
            _decide = releases => releases.Select(r => r.Title.Contains("bob")
                ? new DownloadDecision(RemoteBookFor(r), new Rejection("Wrong language"))
                : new DownloadDecision(RemoteBookFor(r))).ToList();

            var result = _service.Search(42);

            Assert.That(_client.SearchTexts, Is.EqualTo(new[] { "Frank Herbert Dune" }));
            Assert.That(_client.DeletedSearches, Has.Count.EqualTo(1));
            Assert.That(result.Results, Has.Count.EqualTo(2));
            Assert.That(result.Results[0].Username, Is.EqualTo("alice"));
            Assert.That(result.Results[0].Approved, Is.True);
            Assert.That(result.Results[1].Approved, Is.False);
            Assert.That(result.Results[1].Rejections, Is.EqualTo(new[] { "Wrong language" }));
        }

        [Test]
        public void search_needs_soulseek_on()
        {
            _settings.Save(new TalearrSoulseekSettings { Enabled = false, Url = "http://slskd:5030" });

            Assert.Throws<TalearrSoulseekException>(() => _service.Search(42));
            Assert.That(_client.SearchTexts, Is.Empty);
        }

        [Test]
        public void a_grab_downloads_into_its_own_folder_and_imports_for_that_book()
        {
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);

            var grab = _service.Grab(search.Token, search.Results[0].Key);

            Assert.That(grab.State, Is.EqualTo(TalearrSoulseekGrabState.Queued));
            Assert.That(_client.Enqueued.Single().Destination, Is.EqualTo("talearr-" + grab.Id));
            Assert.That(_client.Enqueued.Single().Files, Has.Count.EqualTo(2));

            _service.RefreshCore();
            Assert.That(_store.Get(grab.Id).State, Is.EqualTo(TalearrSoulseekGrabState.Queued));

            _client.SetState("alice", "InProgress");
            _service.RefreshCore();
            Assert.That(_store.Get(grab.Id).State, Is.EqualTo(TalearrSoulseekGrabState.Downloading));

            var folder = Path.Combine(_root, "talearr-" + grab.Id);
            WriteDownloadedFiles(folder);
            File.WriteAllText(Path.Combine(folder, "cover.jpg"), "c");
            _client.SetState("alice", "Completed, Succeeded");
            _service.RefreshCore();

            var stored = _store.Get(grab.Id);
            Assert.That(stored.State, Is.EqualTo(TalearrSoulseekGrabState.Imported));

            var import = _imports.Single();
            Assert.That(import.Path, Is.EqualTo(folder));
            Assert.That(import.Mode, Is.EqualTo(ImportMode.Move));
            Assert.That(import.Item.FilePaths, Has.Count.EqualTo(2));
            Assert.That(import.Item.DownloadClientInfo.Protocol, Is.EqualTo(DownloadProtocol.Unknown));
            Assert.That(import.RemoteBook.Books.Single().Id, Is.EqualTo(42));
            Assert.That(import.RemoteBook.Author.Id, Is.EqualTo(7));

            // Finished transfers are cleared in slskd and Talearr's own folder is removed.
            Assert.That(_client.Cancelled.Count(c => c.Remove), Is.EqualTo(2));
            Assert.That(Directory.Exists(folder), Is.False);
        }

        [Test]
        public void older_slskd_files_are_found_in_its_default_folder()
        {
            _client.SupportsBatches = false;
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results[0].Key);
            Assert.That(grab.Destination, Is.Null);

            var folder = Path.Combine(_root, "Dune (alice)");
            WriteDownloadedFiles(folder);
            File.WriteAllText(Path.Combine(folder, "someone-elses.txt"), "keep");
            _client.SetState("alice", "Completed, Succeeded");
            _service.RefreshCore();

            Assert.That(_store.Get(grab.Id).State, Is.EqualTo(TalearrSoulseekGrabState.Imported));
            Assert.That(_imports.Single().Path, Is.EqualTo(folder));

            // A folder slskd chose is only removed when it is empty.
            Assert.That(Directory.Exists(folder), Is.True);
        }

        [Test]
        public void a_failed_import_keeps_the_files_and_says_where_they_are()
        {
            _importResult = ImportResultType.Rejected;
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results[0].Key);
            var folder = Path.Combine(_root, "talearr-" + grab.Id);
            WriteDownloadedFiles(folder);
            _client.SetState("alice", "Completed, Succeeded");

            _service.RefreshCore();

            var stored = _store.Get(grab.Id);
            Assert.That(stored.State, Is.EqualTo(TalearrSoulseekGrabState.ImportFailed));
            Assert.That(stored.Message, Does.Contain("Wrong book").And.Contain(folder));
            Assert.That(File.Exists(Path.Combine(folder, "01.mp3")), Is.True);
        }

        [Test]
        public void a_failed_transfer_fails_the_grab_and_that_peer_is_not_offered_again()
        {
            PeersOffer(DunePeer("alice"), DunePeer("bob"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results.Single(r => r.Username == "alice").Key);

            _client.Transfers["alice"][0].State = "Completed, Errored";
            _client.Transfers["alice"][0].Exception = "Transfer rejected: File not shared.";
            _service.RefreshCore();

            var stored = _store.Get(grab.Id);
            Assert.That(stored.State, Is.EqualTo(TalearrSoulseekGrabState.Failed));
            Assert.That(stored.Message, Does.Contain("File not shared"));
            Assert.That(_imports, Is.Empty);

            var again = _service.Search(42);
            Assert.That(again.Results.Select(r => r.Username), Is.EqualTo(new[] { "bob" }));
        }

        [Test]
        public void a_grab_still_queued_after_the_timeout_is_given_up()
        {
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results[0].Key);

            var stored = _store.Get(grab.Id);
            stored.CreatedAt = DateTime.UtcNow.AddHours(-30);
            _store.Save(stored);
            _service.RefreshCore();

            Assert.That(_store.Get(grab.Id).State, Is.EqualTo(TalearrSoulseekGrabState.Failed));
            Assert.That(_client.Cancelled.All(c => c.Remove), Is.True);
            Assert.That(_client.Cancelled, Has.Count.EqualTo(2));
        }

        [Test]
        public void one_running_grab_per_book_and_per_peer()
        {
            PeersOffer(DunePeer("alice"), DunePeer("bob"));
            var search = _service.Search(42);
            _service.Grab(search.Token, search.Results.Single(r => r.Username == "alice").Key);

            var sameBook = Assert.Throws<TalearrSoulseekException>(() => _service.Grab(search.Token, search.Results.Single(r => r.Username == "bob").Key));
            Assert.That(sameBook.Message, Does.Contain("already running"));
            Assert.That(_client.Enqueued, Has.Count.EqualTo(1));
        }

        [Test]
        public void the_per_peer_limit_applies_across_books()
        {
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            _service.Grab(search.Token, search.Results[0].Key);

            // A second book's grab from the same peer.
            var other = _store.Get(_store.All().Single().Id);
            other.BookId = 99;
            _store.Save(other);

            var ex = Assert.Throws<TalearrSoulseekException>(() => _service.Grab(search.Token, search.Results[0].Key));
            Assert.That(ex.Message, Does.Contain("alice"));
        }

        [Test]
        public void an_unknown_search_token_is_refused()
        {
            Assert.Throws<TalearrSoulseekException>(() => _service.Grab("nope", "nope"));
        }

        [Test]
        public void cancel_removes_the_transfers_from_slskd()
        {
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results[0].Key);

            var cancelled = _service.Cancel(grab.Id);

            Assert.That(cancelled.State, Is.EqualTo(TalearrSoulseekGrabState.Cancelled));
            Assert.That(_client.Cancelled, Has.Count.EqualTo(2));
        }

        [Test]
        public void rollback_safety_only_talearr_soulseek_config_rows_are_written()
        {
            PeersOffer(DunePeer("alice"));
            var search = _service.Search(42);
            var grab = _service.Grab(search.Token, search.Results[0].Key);
            WriteDownloadedFiles(Path.Combine(_root, "talearr-" + grab.Id));
            _client.SetState("alice", "Completed, Succeeded");
            _service.RefreshCore();

            Assert.That(_configWritesOutsideTalearr, Is.EqualTo(0));
            Assert.That(_rows.Keys, Is.All.StartWith("talearr.soulseek."));
            Assert.That(_rows[TalearrSoulseekGrabStore.KeyPrefix + grab.Id], Does.Contain("\"state\":\"Imported\""));
        }

        [Test]
        public void settings_default_off_and_the_api_key_survives_a_masked_save()
        {
            Assert.That(TalearrSoulseekSettings.Default.Enabled, Is.False);
            Assert.That(TalearrSoulseekSettings.Default.RequireFreeUploadSlot, Is.True);
            Assert.That(TalearrSoulseekSettings.Default.MaxActiveGrabsPerPeer, Is.EqualTo(1));

            var saved = _settings.Save(new TalearrSoulseekSettings { Enabled = true, Url = "http://slskd:5030/", ApiKey = TalearrSoulseekSettings.MaskedApiKey, DownloadsPath = _root, MaxActiveGrabs = 0 });

            Assert.That(saved.ApiKey, Is.EqualTo("secret"));
            Assert.That(saved.Url, Is.EqualTo("http://slskd:5030"));
            Assert.That(saved.MaxActiveGrabs, Is.EqualTo(1));
        }

        [Test]
        public void test_warns_when_the_downloads_folder_is_not_visible()
        {
            var result = _service.Test(new TalearrSoulseekSettings { Url = "http://slskd:5030", ApiKey = TalearrSoulseekSettings.MaskedApiKey, DownloadsPath = "/nope/not/here" });

            Assert.That(result.Success, Is.True);
            Assert.That(result.Message, Does.Contain("0.26.1"));
            Assert.That(result.Warnings.Single(), Does.Contain("/nope/not/here"));
        }
    }
}
