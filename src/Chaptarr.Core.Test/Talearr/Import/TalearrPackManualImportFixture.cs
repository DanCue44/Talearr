using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Import;

namespace Chaptarr.Core.Test.Talearr.Import
{
    // Talearr (issue #13): after adding the Renegade Star Publisher's Packs, Manual Import listed
    // the pack files as "Select to Add" with the pack's own title although the packs were already
    // in the library, then refused them with "Local grouped matching could not match ..." in a
    // message that vanished after four seconds.
    [TestFixture]
    public class TalearrPackManualImportFixture
    {
        private const string Pack8 = "/audiobooks/audiobookshelf/J.N. Chaney/Renegade Star/JN Chaney-Renegade Star-#8-Publisher's Pack 8-Book 15-16/Renegade Star Publisher's Pack 8 Renegade Star, Books 15-16.m4b";
        private const string Pack1 = "/audiobooks/audiobookshelf/J.N. Chaney/Renegade Star/JN Chaney-Renegade Star-#1-Publisher's Pack1-Book 1-2/Renegade Star (Unabridged).m4b";

        private readonly Author _chaney = new Author { Id = 80, Name = "J.N. Chaney", HardcoverAuthorId = "hc:241293" };
        private List<Book> _books;
        private Dictionary<int, List<BookFile>> _filesByBook;
        private IAuthorService _authors;
        private IBookService _bookService;
        private IEditionService _editions;
        private IMediaFileService _mediaFiles;

        [SetUp]
        public void SetUp()
        {
            _books = new List<Book>
            {
                new Book { Id = 21681, AuthorId = 80, Title = "Renegade Star Publisher's Pack: Books 15-16", MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:2115997" },
                new Book { Id = 21674, AuthorId = 80, Title = "Renegade Star: Publisher's Pack 6: Books 11-12", MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:2017148" },
                new Book { Id = 30000, AuthorId = 99, Title = "Someone Else's Book", MediaType = BookMediaType.Audiobook, HardcoverBookId = "hc:777" }
            };
            _filesByBook = new Dictionary<int, List<BookFile>>();

            var (authors, authorFake) = InterfaceFake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.FindByProviderId), args => (string)args[0] == "hc" && (string)args[1] == "241293" ? _chaney : null);
            authorFake.On(nameof(IAuthorService.GetAuthor), args => (int)args[0] == _chaney.Id ? _chaney : null);
            _authors = authors;

            var (books, bookFake) = InterfaceFake<IBookService>.Create();
            bookFake.On(nameof(IBookService.FindAllByWorkProviderId), args => _books
                .Where(b => b.MediaType == (BookMediaType)args[2] && b.HardcoverBookId == $"{args[0]}:{args[1]}")
                .ToList());
            bookFake.On(nameof(IBookService.GetBooksByAuthor), args => _books.Where(b => b.AuthorId == (int)args[0]).ToList());
            _bookService = books;

            var (editions, editionFake) = InterfaceFake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEditionsByAuthor), _ => new List<Edition>());
            editionFake.On(nameof(IEditionService.GetEditionsByBook), _ => new List<Edition>());
            _editions = editions;

            var (mediaFiles, fileFake) = InterfaceFake<IMediaFileService>.Create();
            fileFake.On(nameof(IMediaFileService.GetFilesByBook), args => _filesByBook.GetValueOrDefault((int)args[0]) ?? new List<BookFile>());
            _mediaFiles = mediaFiles;
        }

        private Book Find(string path, string authorProviderId, string bookProviderId, int? restrictToAuthorId = 80, string title = null)
        {
            var file = new DiscoveredFileWithMetadata { Path = path };
            if (title != null)
            {
                file.AllTags["title"] = new List<string> { title };
                file.AllTags["album"] = new List<string> { title };
            }

            return TalearrLocalSuggestion.FindBook(file, BookMediaType.Audiobook, authorProviderId, bookProviderId, restrictToAuthorId,
                _authors, _bookService, _editions, _mediaFiles);
        }

        [Test]
        public void a_suggested_pack_already_in_the_library_is_the_row_book()
        {
            var book = Find(Pack8, "hc:241293", "hc:2115997", title: "Renegade Star: Publisher's Pack 8: Renegade Star, Books 15-16");

            Assert.That(book?.Id, Is.EqualTo(21681));
        }

        [Test]
        public void the_author_alone_is_enough_when_the_suggestion_names_no_author_in_the_library()
        {
            Assert.That(Find(Pack8, "hc:000", "hc:2115997")?.Id, Is.EqualTo(21681));
            Assert.That(Find(Pack8, "hc:000", "hc:2115997", restrictToAuthorId: null), Is.Null);
        }

        [Test]
        public void a_suggested_book_that_already_has_a_file_stays_a_suggestion()
        {
            _filesByBook[21681] = new List<BookFile> { new BookFile { Id = 1, Path = "/other.m4b" } };

            Assert.That(Find(Pack8, "hc:241293", "hc:2115997"), Is.Null);
        }

        [Test]
        public void a_pack_file_never_lands_on_another_pack()
        {
            // The preview suggested Pack 6 (Books 11-12) for the Books 1-2 file.
            Assert.That(Find(Pack1, "hc:241293", "hc:2017148", title: "Renegade Star: Renegade Star, Books 1-2"), Is.Null);
        }

        [Test]
        public void a_book_of_another_author_or_not_in_the_library_is_not_used()
        {
            Assert.That(Find(Pack8, "hc:241293", "hc:777"), Is.Null);
            Assert.That(Find(Pack8, "hc:241293", "hc:123456"), Is.Null);
            Assert.That(Find(Pack8, "hc:241293", null), Is.Null);
        }

        [Test]
        public void the_summary_names_the_file_and_the_reason()
        {
            var rejected = new ImportDecision<LocalBook>(new LocalBook { Path = Pack8 }, new Rejection("Its tags do not match 'Renegade Star Publisher's Pack: Books 15-16'. Pick the book in the Book column and import again."));
            var notImported = new List<ImportResult>
            {
                new ImportResult(rejected, "Rejected: " + rejected.Rejections.Single().Reason),
                new ImportResult(new ImportDecision<LocalBook>(new LocalBook { Path = Pack1 }), "File already exists in database and is linked to a different book - import blocked")
            };

            var summary = TalearrManualImportMessages.Summary(1, 3, notImported);

            Assert.That(summary, Is.EqualTo("Imported 1 of 3 files. Not imported: 'Renegade Star Publisher's Pack 8 Renegade Star, Books 15-16.m4b': Its tags do not match 'Renegade Star Publisher's Pack: Books 15-16'. Pick the book in the Book column and import again. (and 1 more, see System > Logs)"));
            Assert.That(TalearrManualImportMessages.Summary(2, 2, new List<ImportResult>()), Is.EqualTo("Imported 2 files"));
            Assert.That(TalearrManualImportMessages.Summary(1, 1, null), Is.EqualTo("Imported 1 file"));
        }

        [Test]
        public void a_suggested_book_mismatch_names_the_library_book()
        {
            var pack = _books[0];

            Assert.That(TalearrManualImportMessages.SuggestedBookMismatch("Renegade Star: Publisher's Pack 8", new List<Book> { pack }),
                Is.EqualTo("Its tags do not match 'Renegade Star Publisher's Pack: Books 15-16'. Pick the book in the Book column and import again."));
            Assert.That(TalearrManualImportMessages.SuggestedBookMismatch(null, null),
                Is.EqualTo("Its tags do not match the suggested book. Pick the book in the Book column and import again."));
        }
    }
}
