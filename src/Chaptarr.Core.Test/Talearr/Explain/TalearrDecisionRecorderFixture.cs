using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Explain;

namespace Chaptarr.Core.Test.Talearr.Explain
{
    [TestFixture]
    public class TalearrDecisionRecorderFixture
    {
        private TalearrTestFolder _folder;
        private TalearrDecisionStore _store;
        private TalearrDecisionRecorder _subject;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = new TalearrDecisionStore(_folder.CreateDatabase(), LogManager.GetCurrentClassLogger());
            _now = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            _subject = new TalearrDecisionRecorder(_store, LogManager.GetCurrentClassLogger()) { UtcNow = () => _now };
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private static readonly Book Book = new Book { Id = 7745, Title = "Bad Blood", MediaType = BookMediaType.Audiobook };

        private static DownloadDecision Decision(string title, ReleaseSourceType source, params Rejection[] rejections)
        {
            var remoteBook = new RemoteBook
            {
                Release = new ReleaseInfo { Title = title, Size = 250L * 1024 * 1024, Indexer = "MyAnonamouse" },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.MP3) },
                Books = new List<Book> { Book },
                ReleaseSource = source
            };

            return new DownloadDecision(remoteBook, rejections);
        }

        private static Rejection Reject(string reason, string source)
        {
            return new Rejection(reason) { Source = source };
        }

        [Test]
        public void should_explain_a_grab_with_pick_reasons_and_skipped_releases()
        {
            var winner = Decision("Bad Blood (Unabridged) M4B", ReleaseSourceType.Search);
            winner.AutoPickReasons = new List<DownloadDecisionReason> { new DownloadDecisionReason("Primary", "Unabridged over abridged") };
            var other = Decision("Bad Blood MP3 64k", ReleaseSourceType.Search);
            var rejectedA = Decision("Bad Blood Abridged", ReleaseSourceType.Search, Reject("Abridged recordings are not wanted", "TalearrAbridgedSpecification"));
            var rejectedB = Decision("Bad Blood German", ReleaseSourceType.Search, Reject("Language is not wanted", "ReleaseLanguageSpecification"));

            _subject.RecordSearch(new List<DownloadDecision> { winner, other, rejectedA, rejectedB }, new List<DownloadDecision> { winner }, new List<DownloadDecision>());

            var entry = _store.GetForBook(Book.Id).Single();
            Assert.That(entry.Outcome, Is.EqualTo(TalearrDecisionOutcomes.Grabbed));
            Assert.That(entry.Code, Is.EqualTo("grabbed-search"));
            Assert.That(entry.Summary, Does.Contain("Bad Blood (Unabridged) M4B"));
            Assert.That(entry.Details, Has.Some.Contains("Unabridged over abridged"));
            Assert.That(entry.Details, Has.Some.Contains("1 other release was also acceptable"));
            Assert.That(entry.Details, Has.Some.Contains("Abridged recordings are not wanted"));
            Assert.That(entry.Details, Has.Some.Contains("(Language)"));
        }

        [Test]
        public void should_explain_a_search_that_grabbed_nothing_with_a_stable_code()
        {
            var a = Decision("Bad Blood Abridged", ReleaseSourceType.UserInvokedSearch, Reject("Abridged recordings are not wanted", "TalearrAbridgedSpecification"));
            var b = Decision("Bad Blood Abridged 2", ReleaseSourceType.UserInvokedSearch, Reject("Abridged recordings are not wanted", "TalearrAbridgedSpecification"));
            var c = Decision("Bad Blood German", ReleaseSourceType.UserInvokedSearch, Reject("Language is not wanted", "ReleaseLanguageSpecification"));

            _subject.RecordSearch(new List<DownloadDecision> { a, b, c }, new List<DownloadDecision>(), new List<DownloadDecision>());

            var entry = _store.GetForBook(Book.Id).Single();
            Assert.That(entry.Outcome, Is.EqualTo(TalearrDecisionOutcomes.Skipped));
            Assert.That(entry.Code, Is.EqualTo("rejected:abridged"));
            Assert.That(entry.Summary, Is.EqualTo("Search found 3 releases but grabbed nothing"));
            Assert.That(entry.Details.First(), Does.StartWith("2 releases skipped"));
        }

        [Test]
        public void should_not_record_rss_passes_that_grab_nothing()
        {
            var a = Decision("Bad Blood German", ReleaseSourceType.Rss, Reject("Language is not wanted", "ReleaseLanguageSpecification"));

            _subject.RecordSearch(new List<DownloadDecision> { a }, new List<DownloadDecision>(), new List<DownloadDecision>());

            Assert.That(_store.GetForBook(Book.Id), Is.Empty);
        }

        [Test]
        public void should_keep_the_download_id_of_an_automatic_grab()
        {
            var winner = Decision("Bad Blood M4B", ReleaseSourceType.Rss);
            _subject.Handle(new BookGrabbedEvent(winner.RemoteBook) { DownloadId = "abc123" });

            _subject.RecordSearch(new List<DownloadDecision> { winner }, new List<DownloadDecision> { winner }, new List<DownloadDecision>());

            var entry = _store.GetForBook(Book.Id).Single();
            Assert.That(entry.Code, Is.EqualTo("grabbed-rss"));
            Assert.That(entry.DownloadId, Is.EqualTo("abc123"));
        }

        [Test]
        public void should_record_an_interactive_grab()
        {
            var pick = Decision("Bad Blood M4B", ReleaseSourceType.InteractiveSearch);
            _subject.Handle(new BookGrabbedEvent(pick.RemoteBook) { DownloadId = "xyz", DownloadClientName = "qBittorrent" });

            var entry = _store.GetForBook(Book.Id).Single();
            Assert.That(entry.Code, Is.EqualTo("grabbed-by-you"));
            Assert.That(entry.Details, Has.Some.Contains("qBittorrent"));
        }

        [Test]
        public void should_record_a_stalled_import_once_and_hide_paths()
        {
            var item = new DownloadClientItem { DownloadId = "dl-9", Title = "Bad Blood M4B" };
            var tracked = new TrackedDownload
            {
                DownloadItem = item,
                RemoteBook = new RemoteBook { Books = new List<Book> { Book } }
            };
            tracked.Warn(new TrackedDownloadStatusMessage("/downloads/books/Bad Blood/part1.mp3", new List<string> { "Not an upgrade for existing book file(s)" }));

            _subject.Handle(new BookImportIncompleteEvent(tracked));
            _now = _now.AddMinutes(5);
            _subject.Handle(new BookImportIncompleteEvent(tracked));

            var entries = _store.GetForBook(Book.Id);
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].Code, Is.EqualTo("import-blocked"));
            Assert.That(entries[0].Details.Single(), Is.EqualTo("part1.mp3: Not an upgrade for existing book file(s)"));
        }

        [Test]
        public void should_explain_an_upgrade_import()
        {
            var book = new Book { Id = Book.Id, Title = Book.Title };
            var files = new List<BookFile> { new BookFile { SceneName = "Bad Blood M4B", Quality = new QualityModel(Quality.M4B) } };
            var old = new List<BookFile> { new BookFile { Quality = new QualityModel(Quality.MP3) } };

            _subject.Handle(new BookImportedEvent(new Author(), book, files, old, true, new DownloadClientItem { DownloadId = "dl-1" }));

            var entry = _store.GetForBook(Book.Id).Single();
            Assert.That(entry.Code, Is.EqualTo("imported-upgrade"));
            Assert.That(entry.Details, Has.Some.Contains("replaced the older copy (MP3)"));
        }

        [Test]
        public void should_keep_only_the_newest_entries_per_book()
        {
            for (var i = 0; i < TalearrDecisionStore.KeepPerBook + 5; i++)
            {
                _now = _now.AddMinutes(1);
                var winner = Decision($"Release {i}", ReleaseSourceType.Search);
                _subject.RecordSearch(new List<DownloadDecision> { winner }, new List<DownloadDecision> { winner }, new List<DownloadDecision>());
            }

            var entries = _store.GetForBook(Book.Id);
            Assert.That(entries, Has.Count.EqualTo(TalearrDecisionStore.KeepPerBook));
            Assert.That(entries.First().ReleaseTitle, Is.EqualTo($"Release {TalearrDecisionStore.KeepPerBook + 4}"));
        }

        [TestCase("QualityAllowedByProfileSpecification", "quality-allowed-by-profile")]
        [TestCase("TalearrAbridgedSpecification", "abridged")]
        [TestCase("MyAnonaMouseVipAccessSpecification", "my-anona-mouse-vip-access")]
        public void should_build_stable_codes(string source, string code)
        {
            Assert.That(TalearrDecisionRecorder.CodeFromSource(source), Is.EqualTo(code));
        }

        [TestCase("Could not move /mnt/user/Media/Books/Author/Book.epub to target", "Could not move Book.epub to target")]
        [TestCase(@"Access denied: C:\Books\Author\Book.m4b", "Access denied: Book.m4b")]
        [TestCase("/downloads/books/Bad Blood/part 1.mp3 could not be read", "part 1.mp3 could not be read")]
        [TestCase("No path here", "No path here")]
        public void should_redact_paths(string input, string expected)
        {
            Assert.That(TalearrDecisionRecorder.Redact(input), Is.EqualTo(expected));
        }
    }
}
