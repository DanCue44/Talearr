using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.EditionLocks
{
    // Talearr: the chaptarr.db reads and writes edition locks need. Repositories, not services:
    // EditionService calls the lock service, so going through the book or edition services here
    // would make a dependency cycle.
    public interface ITalearrEditionLockLibrary
    {
        Book GetBook(int bookId);
        List<Edition> GetEditions(int bookId);
        int FileCount(int editionId);
        void SaveAnyEditionOk(Book book);
        void SaveEditions(List<Edition> editions);
    }

    public class TalearrEditionLockLibrary : ITalearrEditionLockLibrary
    {
        private readonly IBookRepository _bookRepository;
        private readonly IEditionRepository _editionRepository;
        private readonly IMediaFileRepository _mediaFileRepository;

        public TalearrEditionLockLibrary(IBookRepository bookRepository,
                                         IEditionRepository editionRepository,
                                         IMediaFileRepository mediaFileRepository)
        {
            _bookRepository = bookRepository;
            _editionRepository = editionRepository;
            _mediaFileRepository = mediaFileRepository;
        }

        public Book GetBook(int bookId)
        {
            return bookId > 0 ? _bookRepository.Find(bookId) : null;
        }

        public List<Edition> GetEditions(int bookId)
        {
            return bookId > 0 ? _editionRepository.FindByBook(new[] { bookId }) ?? new List<Edition>() : new List<Edition>();
        }

        public int FileCount(int editionId)
        {
            return editionId > 0 ? _mediaFileRepository.GetFilesByEdition(editionId)?.Count ?? 0 : 0;
        }

        public void SaveAnyEditionOk(Book book)
        {
            if (book != null && book.Id > 0)
            {
                _bookRepository.SetFields(book, b => b.AnyEditionOk);
            }
        }

        public void SaveEditions(List<Edition> editions)
        {
            var list = editions?.Where(e => e != null && e.Id > 0).ToList();
            if (list?.Count > 0)
            {
                _editionRepository.UpdateMany(list);
            }
        }
    }
}
