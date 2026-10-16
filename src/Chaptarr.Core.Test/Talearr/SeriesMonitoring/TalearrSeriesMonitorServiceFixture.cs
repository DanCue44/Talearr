using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.SeriesMonitoring;

namespace Chaptarr.Core.Test.Talearr.SeriesMonitoring
{
    // Theodore Boone as it is in the live library: one series row per media type (gr:61743), a
    // box set and a novella among the books, the ebook side of the author paused.
    [TestFixture]
    public class TalearrSeriesMonitorServiceFixture
    {
        private const int AudioSeries = 15;
        private const int EbookSeries = 8;

        private Dictionary<int, Series> _series;
        private List<SeriesBookLink> _links;
        private Dictionary<int, Book> _books;
        private Dictionary<int, Author> _authors;
        private HashSet<int> _withFiles;
        private HashSet<int> _noRootFolder;
        private Dictionary<string, string> _config;
        private List<Command> _pushed;
        private List<Author> _updatedAuthors;
        private TalearrGrabPolicy _policy;
        private TalearrSeriesMonitorService _service;

        [SetUp]
        public void SetUp()
        {
            _series = new Dictionary<int, Series>
            {
                [AudioSeries] = new Series { Id = AudioSeries, Title = "Theodore Boone", GoodreadsSeriesId = "gr:61743", MediaType = BookMediaType.Audiobook },
                [EbookSeries] = new Series { Id = EbookSeries, Title = "Theodore Boone", GoodreadsSeriesId = "gr:61743", MediaType = BookMediaType.Ebook, Description = "Kid lawyer." },
                [99] = new Series { Id = 99, Title = "The Firm", GoodreadsSeriesId = "gr:366986", MediaType = BookMediaType.Audiobook }
            };

            _authors = new Dictionary<int, Author>
            {
                [7] = new Author { Id = 7, Name = "John Grisham", AudiobookMonitored = true, EbookMonitored = false }
            };

            _books = new Dictionary<int, Book>();
            _links = new List<SeriesBookLink>();

            AddWork("hc:350745", "Theodore Boone, Kid Lawyer", "1", 3410, 3138);
            AddWork("hc:701182", "The Abduction", "2", 3415, 3143);
            AddWork("hc:39636", "Theodore Boone: The Accused", "3", 3409, 3137);
            AddWork("hc:2318117", "Theodore Boone box set #1-3", "1-3", 3427, 3153, omnibus: true);
            AddWork("hc:900001", "Theodore Boone: The Recital", "2.5", 3500, 3501);

            _withFiles = new HashSet<int> { 3410 };
            _noRootFolder = new HashSet<int>();
            _config = new Dictionary<string, string>();
            _pushed = new List<Command>();
            _updatedAuthors = new List<Author>();
            _policy = new TalearrGrabPolicy();

            _service = Service();
        }

        private void AddWork(string baseId, string title, string position, int audioId, int ebookId, bool omnibus = false)
        {
            _books[audioId] = new Book { Id = audioId, AuthorId = 7, Title = title, BaseBookId = baseId, MediaType = BookMediaType.Audiobook, TitleSlug = "a-" + audioId, IsOmnibus = omnibus };
            _books[ebookId] = new Book { Id = ebookId, AuthorId = 7, Title = title, BaseBookId = baseId, MediaType = BookMediaType.Ebook, TitleSlug = "e-" + ebookId, IsOmnibus = omnibus };
            _links.Add(new SeriesBookLink { SeriesId = AudioSeries, BookId = audioId, Position = position, IsPrimary = true });
            _links.Add(new SeriesBookLink { SeriesId = EbookSeries, BookId = ebookId, Position = position, IsPrimary = true });
        }

        private TalearrSeriesMonitorService Service()
        {
            var (series, seriesFake) = Fake<ISeriesService>.Create();
            seriesFake.On(nameof(ISeriesService.GetAllSeries), _ => _series.Values.ToList())
                .On(nameof(ISeriesService.GetSeries), args => _series.TryGetValue((int)args[0], out var s) ? s : null);

            var (links, linksFake) = Fake<ISeriesBookLinkService>.Create();
            linksFake.On(nameof(ISeriesBookLinkService.GetLinksBySeries), args => _links.Where(l => l.SeriesId == (int)args[0]).ToList());

            var (books, booksFake) = Fake<IBookService>.Create();
            booksFake.On(nameof(IBookService.GetBooks), args => ((IEnumerable<int>)args[0]).Where(_books.ContainsKey).Select(id => _books[id]).ToList())
                .On(nameof(IBookService.GetBooksByAuthor), args => _books.Values.Where(b => b.AuthorId == (int)args[0]).ToList())
                .On(nameof(IBookService.SetMonitoredForMediaType), args =>
                {
                    foreach (var id in (IEnumerable<int>)args[0])
                    {
                        if (!_noRootFolder.Contains(id))
                        {
                            _books[id].SetMonitoredForMediaType((string)args[1], (bool)args[2]);
                        }
                    }

                    return null;
                });

            var (authors, authorsFake) = Fake<IAuthorService>.Create();
            authorsFake.On(nameof(IAuthorService.GetAuthor), args => _authors[(int)args[0]])
                .On(nameof(IAuthorService.UpdateAuthor), args =>
                {
                    _updatedAuthors.Add((Author)args[0]);
                    return args[0];
                });

            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBooks), args => ((List<int>)args[0])
                .Where(_withFiles.Contains)
                .Select(id => new BookFile { Path = "/x/" + id, Edition = new Edition { BookId = id } })
                .ToList());

            var (repository, repoFake) = Fake<IConfigRepository>.Create();
            repoFake.On(nameof(IConfigRepository.All), _ => _config.Select(kv => new Config { Key = kv.Key, Value = kv.Value }).ToList())
                .On(nameof(IConfigRepository.Get), args => _config.TryGetValue((string)args[0], out var v) ? new Config { Key = (string)args[0], Value = v } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _config[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                })
                .On(nameof(IConfigRepository.Delete), args =>
                {
                    _config.Remove(((Config)args[0]).Key);
                    return null;
                });

            var (grabPolicy, grabFake) = Fake<ITalearrGrabPolicyService>.Create();
            grabFake.On(nameof(ITalearrGrabPolicyService.Get), _ => _policy);

            var (queue, queueFake) = Fake<IManageCommandQueue>.Create();
            queueFake.On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });

            var store = new TalearrSeriesRuleStore(repository, LogManager.GetCurrentClassLogger());
            return new TalearrSeriesMonitorService(series, links, books, authors, media, store, grabPolicy, queue, LogManager.GetCurrentClassLogger());
        }

        private List<int> Searched() => _pushed.OfType<BookSearchCommand>().SelectMany(c => c.BookIds).ToList();

        [Test]
        public void both_media_type_rows_are_one_series()
        {
            var all = _service.GetSeries();

            Assert.That(all.Select(s => s.Title), Is.EqualTo(new[] { "The Firm", "Theodore Boone" }));
            var boone = all.Single(s => s.Title == "Theodore Boone");
            Assert.That(boone.SeriesKey, Is.EqualTo("gr:61743"));
            Assert.That(boone.SeriesIds, Is.EquivalentTo(new[] { AudioSeries, EbookSeries }));
            Assert.That(boone.BookCount, Is.EqualTo(3), "the box set and the 2.5 novella are not main books");
            Assert.That(boone.AudiobookFiles, Is.EqualTo(1));
            Assert.That(_service.GetSeries(monitoredOnly: true), Is.Empty);
        }

        [Test]
        public void entries_are_ordered_by_position_with_one_row_per_format()
        {
            var detail = _service.GetDetail(EbookSeries);

            Assert.That(detail.Entries.Select(e => e.Position), Is.EqualTo(new[] { "1", "1-3", "2", "2.5", "3" }));
            var first = detail.Entries.First();
            Assert.That(first.Audiobook.BookId, Is.EqualTo(3410));
            Assert.That(first.Ebook.BookId, Is.EqualTo(3138));
            Assert.That(first.Audiobook.HasFiles, Is.True);
            Assert.That(first.Ebook.AuthorMonitored, Is.False);
            Assert.That(detail.Entries.Single(e => e.Position == "1-3").SkipReason, Is.EqualTo("bundle"));
            Assert.That(detail.Entries.Single(e => e.Position == "2.5").SkipReason, Is.EqualTo("extra"));
            Assert.That(detail.Description, Is.EqualTo("Kid lawyer."));
        }

        [Test]
        public void monitoring_audiobooks_monitors_the_main_books_and_searches_the_missing_ones()
        {
            var detail = _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });

            Assert.That(new[] { 3410, 3415, 3409 }.All(id => _books[id].AudiobookMonitored), Is.True);
            Assert.That(_books[3427].AudiobookMonitored, Is.False, "box sets follow the grab policy (bundles off)");
            Assert.That(_books[3500].AudiobookMonitored, Is.False, "novellas only with Include extras");
            Assert.That(_books.Values.Where(b => b.MediaType == BookMediaType.Ebook).Any(b => b.EbookMonitored), Is.False, "ebooks were not asked for");
            Assert.That(Searched(), Is.EquivalentTo(new[] { 3415, 3409 }), "the downloaded book is not searched");
            Assert.That(detail.Series.Rule.LastResult, Does.StartWith("3 newly monitored"));
            Assert.That(_config.Keys, Is.EquivalentTo(new[] { "talearr.series.monitor.gr:61743" }));
        }

        [Test]
        public void a_new_book_in_the_series_is_monitored_and_searched_after_a_refresh()
        {
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            _pushed.Clear();

            AddWork("hc:701180", "The Fugitive", "4", 3414, 3142);
            _service.Execute(new ApplySeriesMonitoringCommand { AuthorId = 7 });

            Assert.That(_books[3414].AudiobookMonitored, Is.True);
            Assert.That(Searched(), Is.EqualTo(new[] { 3414 }), "only the new book is searched");
        }

        [Test]
        public void a_book_you_unmonitor_stays_unmonitored()
        {
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            _books[3415].SetMonitoredForMediaType("audiobook", false);
            _pushed.Clear();

            _service.ApplyAll();

            Assert.That(_books[3415].AudiobookMonitored, Is.False);
            Assert.That(Searched(), Is.Empty);
        }

        [Test]
        public void bundles_are_included_when_the_grab_policy_allows_them_and_extras_on_request()
        {
            _policy.AllowBundles = true;
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true, IncludeExtras = true });

            Assert.That(_books[3427].AudiobookMonitored, Is.True);
            Assert.That(_books[3500].AudiobookMonitored, Is.True);
        }

        [Test]
        public void ebooks_of_a_paused_author_are_monitored_but_not_searched()
        {
            var detail = _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Ebook = true });

            Assert.That(_books[3138].EbookMonitored, Is.True);
            Assert.That(Searched(), Is.Empty, "the author's ebook side is paused, so TaleArr would not search");
            Assert.That(detail.Series.Rule.LastResult, Does.Contain("3 waiting on the author's format"));
        }

        [Test]
        public void turning_the_format_on_for_the_author_searches_the_waiting_books()
        {
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Ebook = true });
            _books[3143].SetMonitoredForMediaType("ebook", false);

            var detail = _service.EnableAuthors(AudioSeries, "ebook");

            Assert.That(_updatedAuthors.Single().EbookMonitored, Is.True);
            Assert.That(_updatedAuthors.Single().AudiobookMonitored, Is.True, "the other format is untouched");
            Assert.That(Searched(), Is.EquivalentTo(new[] { 3138, 3137 }), "a book unmonitored meanwhile is not searched");
            Assert.That(detail.Entries.First().Ebook.AuthorMonitored, Is.True);

            _pushed.Clear();
            _service.ApplyAll();
            Assert.That(Searched(), Is.Empty, "searched once");
        }

        [Test]
        public void a_book_without_a_root_folder_for_the_format_is_retried_later()
        {
            _noRootFolder.Add(3409);
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });

            Assert.That(_books[3409].AudiobookMonitored, Is.False);
            Assert.That(Searched(), Does.Not.Contain(3409));

            _noRootFolder.Clear();
            _pushed.Clear();
            _service.ApplyAll();

            Assert.That(_books[3409].AudiobookMonitored, Is.True);
            Assert.That(Searched(), Is.EqualTo(new[] { 3409 }));
        }

        [Test]
        public void additional_recordings_are_left_alone()
        {
            _books[3600] = new Book { Id = 3600, AuthorId = 7, Title = "The Abduction", BaseBookId = "hc:701182", MediaType = BookMediaType.Audiobook, TitleSlug = "the-abduction_wanted_2" };

            var detail = _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });

            Assert.That(_books[3600].AudiobookMonitored, Is.False);
            Assert.That(detail.Entries.Single(e => e.Position == "2").Audiobook.Copies, Is.EqualTo(1));
            Assert.That(detail.Entries.Single(e => e.Position == "2").Audiobook.BookId, Is.EqualTo(3415));
        }

        [Test]
        public void stopping_a_series_keeps_the_books_monitored()
        {
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            var detail = _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest());

            Assert.That(detail.Series.Rule, Is.Null);
            Assert.That(_config, Is.Empty);
            Assert.That(_books[3415].AudiobookMonitored, Is.True);
        }

        [Test]
        public void adding_a_format_later_applies_to_the_whole_series()
        {
            _authors[7].EbookMonitored = true;
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            _pushed.Clear();

            _service.SetRule(EbookSeries, new TalearrSeriesRuleRequest { Audiobook = true, Ebook = true });

            Assert.That(new[] { 3138, 3143, 3137 }.All(id => _books[id].EbookMonitored), Is.True);
            Assert.That(Searched(), Is.EquivalentTo(new[] { 3138, 3143, 3137 }));
        }

        [Test]
        public void a_refresh_queues_the_apply_only_when_a_series_is_monitored()
        {
            _service.HandleAsync(new AuthorRefreshCompleteEvent(_authors[7]));
            Assert.That(_pushed, Is.Empty);

            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            _pushed.Clear();
            _service.HandleAsync(new AuthorRefreshCompleteEvent(_authors[7]));

            Assert.That(_pushed.OfType<ApplySeriesMonitoringCommand>().Single().AuthorId, Is.EqualTo(7));
        }

        [Test]
        public void rules_for_other_authors_are_not_applied_for_an_author_refresh()
        {
            _service.SetRule(AudioSeries, new TalearrSeriesRuleRequest { Audiobook = true });
            AddWork("hc:701180", "The Fugitive", "4", 3414, 3142);

            _service.Execute(new ApplySeriesMonitoringCommand { AuthorId = 8 });

            Assert.That(_books[3414].AudiobookMonitored, Is.False);
        }

        [TestCase("1", false)]
        [TestCase("2.5", true)]
        [TestCase("0.5", true)]
        [TestCase(null, false)]
        [TestCase("Prequel", false)]
        public void extras_are_fractional_positions(string position, bool extra)
        {
            Assert.That(TalearrSeriesMonitorService.IsExtraEntry(true, position), Is.EqualTo(extra));
            Assert.That(TalearrSeriesMonitorService.IsExtraEntry(false, position), Is.True);
        }

        [TestCase("1-3", true)]
        [TestCase("1-6", true)]
        [TestCase("1 & 2", true)]
        [TestCase("3", false)]
        [TestCase("1.5", false)]
        public void range_positions_are_bundles(string position, bool bundle)
        {
            Assert.That(TalearrSeriesMonitorService.IsBundleEntry(new Book { Title = "Book" }, position), Is.EqualTo(bundle));
        }

        [Test]
        public void series_keys_prefer_the_provider_id()
        {
            Assert.That(TalearrSeriesMonitorService.KeyOf(new Series { GoodreadsSeriesId = "gr:61743" }), Is.EqualTo("gr:61743"));
            Assert.That(TalearrSeriesMonitorService.KeyOf(new Series { HardcoverSeriesId = "4242" }), Is.EqualTo("hc:4242"));
            Assert.That(TalearrSeriesMonitorService.KeyOf(new Series { Title = "The Wheel of Time" }), Is.EqualTo("title:the-wheel-of-time"));
            Assert.That(TalearrSeriesMonitorService.SortPositionOf("2.5"), Is.EqualTo(2.5m));
            Assert.That(TalearrSeriesMonitorService.SortPositionOf("Book 10"), Is.EqualTo(10m));
        }
    }
}
