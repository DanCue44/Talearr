using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Services;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Import
{
    // Talearr (issue #3): an import that matched another edition of the book's pinned recording
    // (same provider edition, or the same Audible product) goes to the book itself. Before, the
    // pin protection cloned a copy row for it, so the book showed the same recording twice: the
    // pinned row Missing and the copy holding the files.
    [TestFixture]
    public class PinnedRecordingImportFixture
    {
        private Book _book;
        private Edition _pinned;
        private Edition _sameProduct;
        private Edition _otherReading;

        [SetUp]
        public void SetUp()
        {
            _book = new Book { Id = 3401, AuthorId = 7, Title = "A Burden of Chaos", TitleSlug = "a-burden-of-chaos", BaseBookId = "hc:1", MediaType = BookMediaType.Audiobook, AnyEditionOk = false };
            _pinned = new Edition { Id = 10, BookId = _book.Id, ForeignEditionId = "hc:edition:100-audiobook", HardcoverEditionId = "100", Title = "A Burden of Chaos", AudibleASIN = "B07S39LY5V", Monitored = true, ManualAdd = true, ReadingFormatId = 2 };
            _sameProduct = new Edition { Id = 11, BookId = _book.Id, ForeignEditionId = "hc:edition:101-audiobook", HardcoverEditionId = "101", Title = "A Burden of Chaos", AudibleASIN = "B07S39LY5V", ReadingFormatId = 2 };
            _otherReading = new Edition { Id = 12, BookId = _book.Id, ForeignEditionId = "hc:edition:102-audiobook", HardcoverEditionId = "102", Title = "A Burden of Chaos", AudibleASIN = "B0OTHER000", ReadingFormatId = 2 };
        }

        private List<Edition> Editions => new List<Edition> { _pinned, _sameProduct, _otherReading };

        [Test]
        public void the_same_audible_product_is_the_pinned_recording()
        {
            Assert.That(TalearrSameRecording.ProtectedTwin(_book, Editions, _sameProduct, (IReadOnlyDictionary<int, EditionAudibleFacts>)null)?.Id, Is.EqualTo(_pinned.Id));
        }

        [Test]
        public void another_reading_still_gets_its_own_row()
        {
            Assert.That(TalearrSameRecording.ProtectedTwin(_book, Editions, _otherReading, (IReadOnlyDictionary<int, EditionAudibleFacts>)null), Is.Null);
        }

        [Test]
        public void the_pinned_edition_itself_and_ebooks_need_nothing()
        {
            Assert.That(TalearrSameRecording.ProtectedTwin(_book, Editions, _pinned, (IReadOnlyDictionary<int, EditionAudibleFacts>)null), Is.Null);

            _book.MediaType = BookMediaType.Ebook;
            Assert.That(TalearrSameRecording.ProtectedTwin(_book, Editions, _sameProduct, (IReadOnlyDictionary<int, EditionAudibleFacts>)null), Is.Null);
        }

        [Test]
        public void a_copy_of_the_pinned_edition_is_the_same_recording()
        {
            var copy = new Edition { Id = 13, BookId = _book.Id, ForeignEditionId = "hc:edition:100", Title = "A Burden of Chaos", ReadingFormatId = 2 };

            Assert.That(TalearrSameRecording.ProtectedTwin(_book, Editions.Append(copy).ToList(), copy, (IReadOnlyDictionary<int, EditionAudibleFacts>)null)?.Id, Is.EqualTo(_pinned.Id));
        }

        [Test]
        public void the_unit_destination_routes_the_same_recording_to_the_book_instead_of_a_clone()
        {
            var subject = Subject();
            var key = subject.BuildRootUnitKeyWithExtension("/audiobooks/Robert W. Ross/A Burden of Chaos/A Burden of Chaos.m4b", _sameProduct.Title, _book.MediaType);

            var destination = subject.ResolveDestinationForUnit(_book, _sameProduct, key);

            Assert.That(destination, Is.EqualTo((_book.Id, _pinned.Id)));
        }

        [Test]
        public void the_unit_destination_still_clones_another_reading()
        {
            var subject = Subject();
            var key = subject.BuildRootUnitKeyWithExtension("/audiobooks/Robert W. Ross/A Burden of Chaos/A Burden of Chaos.m4b", _otherReading.Title, _book.MediaType);

            Assert.Throws<CloneAttempted>(() => subject.ResolveDestinationForUnit(_book, _otherReading, key));
        }

        private BookUnitDestinationService Subject()
        {
            var (mediaFiles, mediaFake) = InterfaceFake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), _ => new List<BookFile>());

            var (books, booksFake) = InterfaceFake<IBookService>.Create();
            booksFake.On(nameof(IBookService.GetBook), _ => _book)
                     .On(nameof(IBookService.GetBooksByAuthor), _ => new List<Book> { _book });

            var (editions, editionsFake) = InterfaceFake<IEditionService>.Create();
            editionsFake.On(nameof(IEditionService.GetEditionsByBook), _ => Editions)
                        .On(nameof(IEditionService.GetEdition), a => Editions.FirstOrDefault(e => e.Id == (int)a[0]));

            var (database, databaseFake) = InterfaceFake<IMainDatabase>.Create();
            databaseFake.On(nameof(IMainDatabase.OpenConnection), _ => throw new CloneAttempted());

            return new BookUnitDestinationService(mediaFiles, books, editions, database, LogManager.GetCurrentClassLogger());
        }

        private sealed class CloneAttempted : System.Exception
        {
        }
    }
}
