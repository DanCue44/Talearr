using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.BookEdits;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.BookEdits
{
    // An in-memory chaptarr.db for the edit service: books, editions, series and links.
    internal sealed class FakeBookEditLibrary : ITalearrBookEditLibrary
    {
        private int _nextLinkId = 1000;
        private int _nextSeriesId = 500;

        public List<Book> Books { get; } = new List<Book>();
        public List<Edition> Editions { get; } = new List<Edition>();
        public List<Series> Series { get; } = new List<Series>();
        public List<SeriesBookLink> Links { get; } = new List<SeriesBookLink>();

        public Book GetBook(int bookId) => Books.FirstOrDefault(b => b.Id == bookId);
        public List<Book> GetBooksByAuthor(int authorId) => Books.Where(b => b.AuthorId == authorId).ToList();
        public List<Edition> GetEditions(int bookId) => Editions.Where(e => e.BookId == bookId).ToList();

        public void SaveBooks(List<Book> books)
        {
        }

        public void SaveEditions(List<Edition> editions)
        {
        }

        public List<SeriesBookLink> GetLinks(int bookId) => Links.Where(l => l.BookId == bookId).ToList();
        public List<SeriesBookLink> GetLinksBySeries(int seriesId) => Links.Where(l => l.SeriesId == seriesId).ToList();

        public void AddLinks(List<SeriesBookLink> links)
        {
            foreach (var link in links)
            {
                link.Id = _nextLinkId++;
                Links.Add(link);
            }
        }

        public void UpdateLinks(List<SeriesBookLink> links)
        {
        }

        public void DeleteLinks(List<SeriesBookLink> links)
        {
            Links.RemoveAll(l => links.Any(x => x.Id == l.Id));
        }

        public Series GetSeries(int seriesId) => Series.FirstOrDefault(s => s.Id == seriesId);

        public List<Series> GetAuthorSeries(int authorId)
        {
            var bookIds = GetBooksByAuthor(authorId).Select(b => b.Id).ToHashSet();
            var seriesIds = Links.Where(l => bookIds.Contains(l.BookId)).Select(l => l.SeriesId).ToHashSet();
            return Series.Where(s => seriesIds.Contains(s.Id)).ToList();
        }

        public Series AddSeries(Series series)
        {
            series.Id = _nextSeriesId++;
            Series.Add(series);
            return series;
        }

        public void DeleteSeries(int seriesId)
        {
            Series.RemoveAll(s => s.Id == seriesId);
        }

        public Series AddProviderSeries(string title, BookMediaType mediaType, string goodreadsId)
        {
            return AddSeries(new Series { Title = title, MediaType = mediaType, GoodreadsSeriesId = goodreadsId });
        }

        public SeriesBookLink Link(Series series, Book book, string position)
        {
            var link = new SeriesBookLink
            {
                SeriesId = series.Id,
                BookId = book.Id,
                Position = position,
                SeriesPosition = int.TryParse(position, out var p) ? p : 0,
                IsPrimary = true,
                SeriesInstanceType = "original"
            };

            AddLinks(new List<SeriesBookLink> { link });
            return link;
        }
    }

    internal sealed class TestableEditRefreshBookService : RefreshBookService
    {
        public TestableEditRefreshBookService(ITalearrBookEditService bookEdits, Logger logger)
            : base(bookService: null,
                authorService: null,
                rootFolderService: null,
                editionService: null,
                authorInfo: null,
                bookInfo: null,
                refreshEditionService: null,
                mediaFileService: null,
                historyService: null,
                eventAggregator: null,
                checkIfBookShouldBeRefreshed: null,
                editionSelector: new EditionSelector(logger),
                editionMetadataProfileFilter: new NzbDrone.Core.Books.Services.EditionMetadataProfileFilter(new Chaptarr.Core.Test.Books.TestTermMatcherService()),
                mediaCoverService: null,
                logger: logger,
                bookEdits: bookEdits)
        {
        }

        public UpdateResult UpdateEntityPublic(Book local, Book remote) => UpdateEntity(local, remote);

        public List<Edition> RemoteEditions(Book local, Book remote) => GetRemoteChildren(local, remote);

        public bool UpToDate(Edition local, Edition remote) => AreChildrenUpToDate(local, remote);
    }

    [TestFixture]
    public class TalearrBookEditFixture
    {
        private const int AuthorId = 7;

        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrBookEditStore _store;
        private FakeBookEditLibrary _library;
        private TalearrBookEditService _service;

        private Book _audiobook;
        private Book _ebook;
        private Edition _audioEdition;
        private Edition _otherAudioEdition;
        private Edition _ebookEdition;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrBookEditStore(_database, LogManager.GetLogger("TalearrTest"));
            _library = new FakeBookEditLibrary();
            _service = new TalearrBookEditService(_store, _library, LogManager.GetLogger("TalearrTest"));

            _audiobook = AddBook(1, BookMediaType.Audiobook, "The Hollow Crwn");
            _ebook = AddBook(2, BookMediaType.Ebook, "The Hollow Crwn");

            _audioEdition = AddEdition(11, _audiobook, "hc:edition:100", "The Hollow Crwn (Unabridged)", monitored: true);
            _otherAudioEdition = AddEdition(12, _audiobook, "hc:edition:101", "The Hollow Crwn", monitored: false);
            _otherAudioEdition.Narrator = "Someone Else";
            _otherAudioEdition.NarratorNames = new List<string> { "Someone Else" };
            _ebookEdition = AddEdition(21, _ebook, "hc:edition:200", "The Hollow Crwn", monitored: true);
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private Book AddBook(int id, BookMediaType mediaType, string title)
        {
            var book = new Book
            {
                Id = id,
                Title = title,
                MediaType = mediaType,
                BaseBookId = "hc:4242"
            };

            book.AuthorId = AuthorId;
            _library.Books.Add(book);
            return book;
        }

        private Edition AddEdition(int id, Book book, string foreignEditionId, string title, bool monitored)
        {
            var edition = new Edition
            {
                Id = id,
                BookId = book.Id,
                ForeignEditionId = foreignEditionId,
                Title = title,
                Monitored = monitored,
                ReadingFormatId = book.MediaType == BookMediaType.Audiobook ? 2 : 3
            };

            _library.Editions.Add(edition);
            return edition;
        }

        private TalearrBookDetails Save(int bookId, TalearrBookEditChange change)
        {
            var result = _service.Save(bookId, change);
            Assert.That(result.Status, Is.EqualTo(TalearrBookEditStatus.Ok), result.Message);
            return result.Details;
        }

        [Test]
        public void talearr_db_schema_eleven_adds_the_edit_tables()
        {
            using var connection = _database.OpenConnection();

            Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.GreaterThanOrEqualTo(11));
            foreach (var table in new[] { "BookDetailEdits", "NarratorEdits", "UserSeries" })
            {
                Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }), Is.EqualTo(1), table);
            }
        }

        [Test]
        public void title_edit_is_applied_to_every_row_and_edition_of_the_work()
        {
            var details = Save(_audiobook.Id, new TalearrBookEditChange { Title = "  The Hollow Crown " });

            Assert.Multiple(() =>
            {
                Assert.That(details.Title.Edited, Is.True);
                Assert.That(details.Title.Value, Is.EqualTo("The Hollow Crown"));
                Assert.That(details.Title.ProviderValue, Is.EqualTo("The Hollow Crwn"));
                Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crown"));
                Assert.That(_ebook.Title, Is.EqualTo("The Hollow Crown"));
                Assert.That(_library.Editions.Select(e => e.Title), Is.All.EqualTo("The Hollow Crown"));
            });

            // Read back from talearr.db, not from the service's memory.
            var stored = _store.Get("hc:4242");
            Assert.That(stored.Title, Is.EqualTo("The Hollow Crown"));
            Assert.That(stored.Provider[_audiobook.Id].EditionTitles["hc:edition:100"], Is.EqualTo("The Hollow Crwn (Unabridged)"));
        }

        [Test]
        public void title_edit_survives_a_book_refresh_and_clearing_brings_back_the_newest_provider_title()
        {
            Save(_audiobook.Id, new TalearrBookEditChange { Title = "The Hollow Crown" });
            var refresh = new TestableEditRefreshBookService(_service, LogManager.GetLogger("TalearrTest"));

            // The provider renamed the book since the edit; the edit still wins and nothing changes.
            var remote = new Book { Title = "The Hollow Crown: A Novel", MediaType = BookMediaType.Audiobook, BaseBookId = "hc:4242" };
            var result = refresh.UpdateEntityPublic(_audiobook, remote);

            Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crown"));
            Assert.That(result, Is.Not.EqualTo(RefreshEntityServiceBase<Book, Edition>.UpdateResult.UpdateTags));
            Assert.That(remote.Title, Is.EqualTo("The Hollow Crown: A Novel"), "the shared remote book is not changed");

            var details = _service.Clear(_audiobook.Id, new[] { "title" }).Details;

            Assert.Multiple(() =>
            {
                Assert.That(details.Title.Edited, Is.False);
                Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crown: A Novel"));
                Assert.That(_ebook.Title, Is.EqualTo("The Hollow Crwn"));
                Assert.That(_audioEdition.Title, Is.EqualTo("The Hollow Crwn (Unabridged)"));
                Assert.That(_store.Get("hc:4242"), Is.Null);
            });
        }

        [Test]
        public void edited_titles_and_narrators_survive_an_edition_refresh()
        {
            Save(_audiobook.Id, new TalearrBookEditChange { Title = "The Hollow Crown", Narrators = new List<string> { "Jane Reader", " Tom Voice ", "jane reader" } });
            var refresh = new TestableEditRefreshBookService(_service, LogManager.GetLogger("TalearrTest"));

            _audiobook.Editions = _library.GetEditions(_audiobook.Id);
            _audiobook.BookFiles = new List<NzbDrone.Core.MediaFiles.BookFile>();

            var remoteEdition = new Edition
            {
                ForeignEditionId = "hc:edition:100",
                Title = "The Hollow Crwn (Unabridged)",
                ReadingFormatId = 2,
                Narrator = "Wrong Person",
                NarratorNames = new List<string> { "Wrong Person" }
            };

            var remote = new Book { Title = "The Hollow Crwn", MediaType = BookMediaType.Audiobook, Editions = new List<Edition> { remoteEdition } };
            var editions = refresh.RemoteEditions(_audiobook, remote);
            var refreshed = editions.Single(e => e.ForeignEditionId == "hc:edition:100");

            Assert.Multiple(() =>
            {
                Assert.That(refreshed.Title, Is.EqualTo("The Hollow Crown"));
                Assert.That(refreshed.NarratorNames, Is.EqualTo(new[] { "Jane Reader", "Tom Voice" }));
                Assert.That(refreshed.Narrator, Is.EqualTo("Jane Reader, Tom Voice"));
                Assert.That(remoteEdition.Title, Is.EqualTo("The Hollow Crwn (Unabridged)"), "the shared remote edition is not changed");
                Assert.That(_audioEdition.NarratorNames, Is.EqualTo(new[] { "Jane Reader", "Tom Voice" }));
                Assert.That(_otherAudioEdition.NarratorNames, Is.EqualTo(new[] { "Someone Else" }), "only the edition the book follows");
            });

            // Clearing the narrators puts back what the provider sent in the last refresh.
            _service.Clear(_audiobook.Id, new[] { "narrators" });
            Assert.That(_audioEdition.NarratorNames, Is.EqualTo(new[] { "Wrong Person" }));
            Assert.That(_audioEdition.Narrator, Is.EqualTo("Wrong Person"));
        }

        [Test]
        public void narrator_edit_is_shared_by_a_recording_copy_of_the_same_edition()
        {
            var copy = AddBook(3, BookMediaType.Audiobook, "The Hollow Crwn");
            copy.TitleSlug = "hollow_copy_1";
            var copyEdition = AddEdition(31, copy, "hc:edition:100", "The Hollow Crwn (Unabridged)", monitored: true);

            var details = Save(_audiobook.Id, new TalearrBookEditChange { Narrators = new List<string> { "Jane Reader" } });

            Assert.That(details.Narrators.Edited, Is.True);
            Assert.That(details.Narrators.ProviderValue, Is.Empty);
            Assert.That(details.NarratorEditionId, Is.EqualTo(_audioEdition.Id));
            Assert.That(copyEdition.NarratorNames, Is.EqualTo(new[] { "Jane Reader" }));
            Assert.That(_service.Get(copy.Id).Details.Narrators.Value, Is.EqualTo(new[] { "Jane Reader" }));
        }

        [Test]
        public void series_can_be_added_removed_and_renumbered()
        {
            var main = _library.AddProviderSeries("Crown Saga", BookMediaType.Audiobook, "gr:1");
            var mainEbook = _library.AddProviderSeries("Crown Saga", BookMediaType.Ebook, "gr:1");
            _library.Link(main, _audiobook, null);
            _library.Link(mainEbook, _ebook, null);
            _audiobook.SeriesName = "Crown Saga";
            _ebook.SeriesName = "Crown Saga";

            // Number the provider series and add a series that does not exist yet.
            var details = Save(_audiobook.Id, new TalearrBookEditChange
            {
                Series = new List<TalearrSeriesEntry>
                {
                    new TalearrSeriesEntry { SeriesId = main.Id, Position = "2" },
                    new TalearrSeriesEntry { Title = "Kingdoms Cycle", Position = "4.5" }
                }
            });

            var created = _library.Series.Where(s => s.Title == "Kingdoms Cycle").ToList();
            Assert.Multiple(() =>
            {
                Assert.That(details.Series.Edited, Is.True);
                Assert.That(details.Series.Value.Select(s => s.Title), Is.EqualTo(new[] { "Crown Saga", "Kingdoms Cycle" }));
                Assert.That(created.Select(s => s.MediaType), Is.EquivalentTo(new[] { BookMediaType.Audiobook, BookMediaType.Ebook }));
                Assert.That(created.All(s => _service.IsUserSeries(s.Id)), Is.True);
                Assert.That(_library.GetLinks(_audiobook.Id).Single(l => l.SeriesId == main.Id).Position, Is.EqualTo("2"));
                Assert.That(_library.GetLinks(_audiobook.Id).Single(l => l.SeriesId == main.Id).SeriesPosition, Is.EqualTo(2));
                Assert.That(_library.GetLinks(_ebook.Id).Single(l => l.SeriesId == mainEbook.Id).Position, Is.EqualTo("2"));
                Assert.That(_library.GetLinks(_audiobook.Id).Count, Is.EqualTo(2));
                Assert.That(_audiobook.SeriesName, Is.EqualTo("Crown Saga"));
                Assert.That(_audiobook.SeriesPosition, Is.EqualTo("2"));
                Assert.That(_ebook.SeriesPosition, Is.EqualTo("2"));
            });

            // Remove the provider series; the new one becomes the main series.
            Save(_audiobook.Id, new TalearrBookEditChange
            {
                Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Title = "kingdoms cycle", Position = "4.5" } }
            });

            Assert.Multiple(() =>
            {
                Assert.That(_library.GetLinks(_audiobook.Id).Select(l => l.SeriesId), Is.EqualTo(new[] { created.Single(s => s.MediaType == BookMediaType.Audiobook).Id }));
                Assert.That(_library.GetLinks(_audiobook.Id).Single().SeriesPosition, Is.EqualTo(0), "4.5 is not a whole number");
                Assert.That(_audiobook.SeriesName, Is.EqualTo("Kingdoms Cycle"));
                Assert.That(_audiobook.SeriesPosition, Is.EqualTo("4.5"));
                Assert.That(_library.Series.Count(s => s.Title == "Kingdoms Cycle"), Is.EqualTo(2), "matched by title, not created again");
            });

            // Clearing restores the provider's links and removes the series Talearr created.
            var cleared = _service.Clear(_audiobook.Id, new[] { "series" }).Details;

            Assert.Multiple(() =>
            {
                Assert.That(cleared.Series.Edited, Is.False);
                Assert.That(_library.GetLinks(_audiobook.Id).Select(l => (l.SeriesId, l.Position)), Is.EqualTo(new[] { (main.Id, (string)null) }));
                Assert.That(_library.GetLinks(_ebook.Id).Select(l => l.SeriesId), Is.EqualTo(new[] { mainEbook.Id }));
                Assert.That(_audiobook.SeriesName, Is.EqualTo("Crown Saga"));
                Assert.That(_audiobook.SeriesPosition, Is.Null);
                Assert.That(_library.Series.Any(s => s.Title == "Kingdoms Cycle"), Is.False);
                Assert.That(_store.GetUserSeries(), Is.Empty);
            });
        }

        [Test]
        public void series_number_survives_a_book_refresh()
        {
            Save(_audiobook.Id, new TalearrBookEditChange
            {
                Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Title = "Crown Saga", Position = "3" } }
            });

            var refresh = new TestableEditRefreshBookService(_service, LogManager.GetLogger("TalearrTest"));
            var remote = new Book { Title = _audiobook.Title, MediaType = BookMediaType.Audiobook, SeriesName = "Crown Saga", SeriesPosition = null };
            refresh.UpdateEntityPublic(_audiobook, remote);

            Assert.That(_audiobook.SeriesName, Is.EqualTo("Crown Saga"));
            Assert.That(_audiobook.SeriesPosition, Is.EqualTo("3"));
            Assert.That(_store.Get("hc:4242").Provider[_audiobook.Id].SeriesName, Is.EqualTo("Crown Saga"), "the provider's latest value is kept for a reset");
        }

        [Test]
        public void a_series_refresh_keeps_edited_links()
        {
            var main = _library.AddProviderSeries("Crown Saga", BookMediaType.Audiobook, "gr:1");
            var other = _library.AddProviderSeries("Old Omnibus", BookMediaType.Audiobook, "gr:2");
            var mainLink = _library.Link(main, _audiobook, "1");
            _library.Link(other, _audiobook, "9");

            Save(_audiobook.Id, new TalearrBookEditChange
            {
                Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Title = "Crown Saga", Position = "2" } }
            });

            var untouched = new SeriesBookLink { Id = 77, BookId = 99, SeriesId = main.Id, Position = "5" };

            // The provider says position 1: the refresh would update the link back.
            var updated = new List<SeriesBookLink> { mainLink, untouched };
            var upToDate = new List<SeriesBookLink>();
            var deleted = new List<SeriesBookLink>();
            var added = new List<SeriesBookLink>();
            _service.FilterSeriesLinks(main, added, updated, deleted, upToDate);

            Assert.That(updated, Is.EqualTo(new[] { untouched }), "other books refresh as usual");
            Assert.That(upToDate, Is.EqualTo(new[] { mainLink }));
            Assert.That(mainLink.Position, Is.EqualTo("2"));

            // The provider still lists the book in the series the user removed it from.
            added = new List<SeriesBookLink> { new SeriesBookLink { BookId = _audiobook.Id, SeriesId = other.Id, Position = "9" } };
            _service.FilterSeriesLinks(other, added, new List<SeriesBookLink>(), new List<SeriesBookLink>(), new List<SeriesBookLink>());
            Assert.That(added, Is.Empty);

            // The provider dropped the book from the series the user keeps it in.
            deleted = new List<SeriesBookLink> { mainLink };
            upToDate = new List<SeriesBookLink>();
            _service.FilterSeriesLinks(main, new List<SeriesBookLink>(), new List<SeriesBookLink>(), deleted, upToDate);
            Assert.That(deleted, Is.Empty);
            Assert.That(upToDate, Is.EqualTo(new[] { mainLink }));
        }

        [Test]
        public void reapply_puts_edits_back_after_something_overwrote_them()
        {
            Save(_audiobook.Id, new TalearrBookEditChange
            {
                Title = "The Hollow Crown",
                Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Title = "Crown Saga", Position = "1" } },
                Narrators = new List<string> { "Jane Reader" }
            });

            // Some other refresh path wrote the provider's details over the edits.
            _audiobook.Title = "The Hollow Crwn";
            _audiobook.SeriesPosition = null;
            _audioEdition.Title = "The Hollow Crwn (Unabridged)";
            _audioEdition.NarratorNames = new List<string>();
            _library.Links.Clear();

            _service.ReapplyAuthor(AuthorId);

            Assert.Multiple(() =>
            {
                Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crown"));
                Assert.That(_audiobook.SeriesPosition, Is.EqualTo("1"));
                Assert.That(_audioEdition.Title, Is.EqualTo("The Hollow Crown"));
                Assert.That(_audioEdition.NarratorNames, Is.EqualTo(new[] { "Jane Reader" }));
                Assert.That(_library.GetLinks(_audiobook.Id).Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void reset_clears_every_field_back_to_the_provider()
        {
            Save(_audiobook.Id, new TalearrBookEditChange
            {
                Title = "The Hollow Crown",
                Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Title = "Crown Saga", Position = "1" } },
                Narrators = new List<string> { "Jane Reader" }
            });

            var details = _service.Clear(_ebook.Id, null).Details;

            Assert.Multiple(() =>
            {
                Assert.That(details.Title.Edited, Is.False);
                Assert.That(details.Series.Edited, Is.False);
                Assert.That(details.Narrators, Is.Null, "an ebook has no narrators");
                Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crwn"));
                Assert.That(_audiobook.SeriesName, Is.Null);
                Assert.That(_library.Links, Is.Empty);
                Assert.That(_store.GetAll(), Is.Empty);
            });

            // Narrators belong to the audiobook's edition; reset them from the audiobook.
            Assert.That(_service.Get(_audiobook.Id).Details.Narrators.Edited, Is.True);
            var reset = Save(_audiobook.Id, new TalearrBookEditChange { Reset = new List<string> { "Narrators" } });
            Assert.That(reset.Narrators.Edited, Is.False);
            Assert.That(_audioEdition.NarratorNames, Is.Empty);
        }

        [Test]
        public void bad_requests_are_refused_without_writing()
        {
            Assert.Multiple(() =>
            {
                Assert.That(_service.Save(999, new TalearrBookEditChange { Title = "x" }).Status, Is.EqualTo(TalearrBookEditStatus.NotFound));
                Assert.That(_service.Save(_audiobook.Id, new TalearrBookEditChange { Title = "   " }).Status, Is.EqualTo(TalearrBookEditStatus.Invalid));
                Assert.That(_service.Save(_ebook.Id, new TalearrBookEditChange { Narrators = new List<string> { "A" } }).Status, Is.EqualTo(TalearrBookEditStatus.Invalid));
                Assert.That(_service.Save(_audiobook.Id, new TalearrBookEditChange { Series = new List<TalearrSeriesEntry> { new TalearrSeriesEntry { Position = "1" } } }).Status, Is.EqualTo(TalearrBookEditStatus.Invalid));
                Assert.That(_service.Save(_audiobook.Id, new TalearrBookEditChange { Title = "Fine", Reset = new List<string> { "cover" } }).Status, Is.EqualTo(TalearrBookEditStatus.Invalid));
                Assert.That(_service.Clear(_audiobook.Id, new[] { "cover" }).Status, Is.EqualTo(TalearrBookEditStatus.Invalid));
            });

            Assert.That(_store.GetAll(), Is.Empty);
            Assert.That(_audiobook.Title, Is.EqualTo("The Hollow Crwn"));
        }

        [Test]
        public void hooks_do_nothing_without_edits()
        {
            var remote = new Book { Title = "Provider", SeriesName = "S" };
            _service.ApplyToRemoteBook(_audiobook, remote);
            var editions = new List<Edition> { new Edition { ForeignEditionId = "hc:edition:100", Title = "Provider" } };

            Assert.That(remote.Title, Is.EqualTo("Provider"));
            Assert.That(_service.ApplyToRemoteEditions(_audiobook, editions), Is.SameAs(editions));
            Assert.That(_service.IsUserSeries(1), Is.False);
        }
    }
}
