using System.Collections.Generic;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Titles
{
    // Talearr: the chaptarr.db reads the display title needs. Repositories, not services: the
    // file name builder uses the display title, and the book services sit above it.
    public interface ITalearrTitleLibrary
    {
        Book GetBook(int bookId);
        List<Book> GetAuthorBooks(int authorId);
    }

    public class TalearrTitleLibrary : ITalearrTitleLibrary
    {
        private readonly IBookRepository _bookRepository;

        public TalearrTitleLibrary(IBookRepository bookRepository)
        {
            _bookRepository = bookRepository;
        }

        public Book GetBook(int bookId)
        {
            return bookId > 0 ? _bookRepository.Find(bookId) : null;
        }

        public List<Book> GetAuthorBooks(int authorId)
        {
            return authorId > 0 ? _bookRepository.GetBooks(authorId) ?? new List<Book>() : new List<Book>();
        }
    }
}
