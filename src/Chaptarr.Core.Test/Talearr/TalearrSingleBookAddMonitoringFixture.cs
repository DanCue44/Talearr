using System.Collections.Generic;
using Chaptarr.Api.V1.Author;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace Chaptarr.Core.Test.Talearr
{
    [TestFixture]
    public class TalearrSingleBookAddMonitoringFixture
    {
        private static readonly List<string> RequestedIds = new() { "hc:1001" };

        private static Book Request(BookMediaType mediaType, Author author)
        {
            return new Book
            {
                Title = "Requested Book",
                HardcoverBookId = "hc:1001",
                MediaType = mediaType,
                Author = author
            };
        }

        [TestCase(BookMediaType.Audiobook)]
        [TestCase(BookMediaType.Ebook)]
        public void request_without_monitoring_options_monitors_only_the_book_and_no_future_releases(BookMediaType mediaType)
        {
            var config = AddBookService.BuildRequestedBookMonitoringConfig(
                Request(mediaType, new Author { Name = "New Author", HardcoverAuthorId = "hc:777" }),
                RequestedIds,
                searchRequested: false);

            var isAudiobook = mediaType == BookMediaType.Audiobook;

            Assert.That(isAudiobook ? config.AudiobookMonitorExistingMode : config.EbookMonitorExistingMode,
                Is.EqualTo(MonitorTypes.SpecificBook), "the back catalog is not monitored");
            Assert.That(isAudiobook ? config.AudiobookBooksToMonitor : config.EbookBooksToMonitor,
                Is.EqualTo(RequestedIds));
            Assert.That(isAudiobook ? config.AudiobookMonitorNewItems : config.EbookMonitorNewItems,
                Is.EqualTo(NewItemMonitorTypes.None), "future releases stay unmonitored by default");
            Assert.That(isAudiobook ? config.EbookMonitorNewItems : config.AudiobookMonitorNewItems,
                Is.Null, "the other format is left alone");
            Assert.That(config.CreateAudiobook, Is.EqualTo(isAudiobook));
            Assert.That(config.CreateEbook, Is.EqualTo(!isAudiobook));
        }

        [TestCase(NewItemMonitorTypes.All)]
        [TestCase(NewItemMonitorTypes.New)]
        [TestCase(NewItemMonitorTypes.None)]
        public void explicit_future_release_choice_is_kept(NewItemMonitorTypes choice)
        {
            var config = AddBookService.BuildRequestedBookMonitoringConfig(
                Request(BookMediaType.Audiobook, new Author { Name = "New Author", AudiobookMonitorNewItems = choice }),
                RequestedIds,
                searchRequested: false);

            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(choice));
        }

        [Test]
        public void explicit_all_books_request_still_monitors_the_whole_catalog()
        {
            var config = AddBookService.BuildRequestedBookMonitoringConfig(
                Request(BookMediaType.Ebook, new Author
                {
                    Name = "New Author",
                    EbookMonitorNewItems = NewItemMonitorTypes.All,
                    AddOptions = new AddAuthorOptions { Monitor = MonitorTypes.All }
                }),
                RequestedIds,
                searchRequested: true);

            Assert.That(config.EbookMonitorExistingMode, Is.EqualTo(MonitorTypes.All));
            Assert.That(config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(config.EbookBooksToSearch, Is.EqualTo(RequestedIds));
        }

        [Test]
        public void allseerr_payload_maps_to_only_this_book_and_no_future_releases()
        {
            // The shape Allseerr posts to POST /api/v1/book?mediaType=audiobook for a new author.
            var resource = new AuthorResource
            {
                AuthorName = "New Author",
                ForeignAuthorId = "hc:777",
                Monitored = true,
                MonitorNewItems = "none",
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.None,
                AudiobookQualityProfileId = 2,
                AudiobookMetadataProfileId = 1,
                AudiobookRootFolderPath = "/audiobooks",
                AddOptions = new AddAuthorOptions
                {
                    Monitor = MonitorTypes.SpecificBook,
                    BooksToMonitor = new List<string> { "hc:1001" },
                    SearchForMissingBooks = false
                }
            };

            var author = resource.ToModel();
            var config = AddBookService.BuildRequestedBookMonitoringConfig(Request(BookMediaType.Audiobook, author), RequestedIds, searchRequested: true);

            Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(config.AudiobookBooksToSearch, Is.EqualTo(RequestedIds));
        }

        [Test]
        public void legacy_client_that_sends_nothing_gets_the_safe_defaults()
        {
            // A Readarr-style client: author info only, no addOptions and no monitorNewItems.
            var author = new AuthorResource
            {
                AuthorName = "New Author",
                ForeignAuthorId = "hc:777",
                AudiobookQualityProfileId = 2,
                AudiobookRootFolderPath = "/audiobooks"
            }.ToModel();

            var config = AddBookService.BuildRequestedBookMonitoringConfig(Request(BookMediaType.Audiobook, author), RequestedIds, searchRequested: false);

            Assert.That(config.AudiobookMonitorExistingMode, Is.EqualTo(MonitorTypes.SpecificBook));
            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(config.AudiobookMonitored, Is.True, "the author side is enabled so the requested book can be grabbed");
        }

        [Test]
        public void defaults_helper_never_overwrites_a_value()
        {
            var config = new MonitoringConfig { AudiobookMonitorNewItems = NewItemMonitorTypes.New };

            TalearrAddMonitoringDefaults.ForSingleBook(config, BookMediaType.Audiobook);
            TalearrAddMonitoringDefaults.ForSingleBook(config, BookMediaType.Ebook);

            Assert.That(config.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.New));
            Assert.That(config.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(TalearrAddMonitoringDefaults.ForSingleBook(null, BookMediaType.Ebook), Is.Null);
        }
    }
}
