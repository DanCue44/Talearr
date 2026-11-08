using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;

namespace NzbDrone.Core.Talearr.BookEdits
{
    // Talearr: the chaptarr.db reads and writes book detail edits need, in one place. Edits are
    // applied to the existing Books, Editions, Series and SeriesBookLink rows (no schema change),
    // so the existing book and series routes return them as they are.
    public interface ITalearrBookEditLibrary
    {
        Book GetBook(int bookId);
        List<Book> GetBooksByAuthor(int authorId);
        List<Edition> GetEditions(int bookId);
        void SaveBooks(List<Book> books);
        void SaveEditions(List<Edition> editions);

        List<SeriesBookLink> GetLinks(int bookId);
        List<SeriesBookLink> GetLinksBySeries(int seriesId);
        void AddLinks(List<SeriesBookLink> links);
        void UpdateLinks(List<SeriesBookLink> links);
        void DeleteLinks(List<SeriesBookLink> links);

        Series GetSeries(int seriesId);
        List<Series> GetAuthorSeries(int authorId);
        Series AddSeries(Series series);
        void DeleteSeries(int seriesId);
    }

    public class TalearrBookEditLibrary : ITalearrBookEditLibrary
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _linkService;

        public TalearrBookEditLibrary(IBookService bookService,
                                      IEditionService editionService,
                                      ISeriesService seriesService,
                                      ISeriesBookLinkService linkService)
        {
            _bookService = bookService;
            _editionService = editionService;
            _seriesService = seriesService;
            _linkService = linkService;
        }

        public Book GetBook(int bookId)
        {
            return _bookService.GetBooks(new[] { bookId }).FirstOrDefault();
        }

        public List<Book> GetBooksByAuthor(int authorId)
        {
            return authorId > 0 ? _bookService.GetBooksByAuthor(authorId) ?? new List<Book>() : new List<Book>();
        }

        public List<Edition> GetEditions(int bookId)
        {
            return _editionService.GetEditionsByBook(bookId) ?? new List<Edition>();
        }

        public void SaveBooks(List<Book> books)
        {
            if (books?.Count > 0)
            {
                // UpdateMany, like a refresh, so no BookEditedEvent (monitoring) side effects.
                _bookService.UpdateMany(books);
            }
        }

        public void SaveEditions(List<Edition> editions)
        {
            if (editions?.Count > 0)
            {
                _editionService.UpdateMany(editions);
            }
        }

        public List<SeriesBookLink> GetLinks(int bookId)
        {
            return _linkService.GetLinksByBook(new List<int> { bookId }) ?? new List<SeriesBookLink>();
        }

        public List<SeriesBookLink> GetLinksBySeries(int seriesId)
        {
            return _linkService.GetLinksBySeries(seriesId) ?? new List<SeriesBookLink>();
        }

        public void AddLinks(List<SeriesBookLink> links)
        {
            if (links?.Count > 0)
            {
                _linkService.InsertMany(links);
            }
        }

        public void UpdateLinks(List<SeriesBookLink> links)
        {
            if (links?.Count > 0)
            {
                _linkService.UpdateMany(links);
            }
        }

        public void DeleteLinks(List<SeriesBookLink> links)
        {
            if (links?.Count > 0)
            {
                _linkService.DeleteMany(links);
            }
        }

        public Series GetSeries(int seriesId)
        {
            try
            {
                return _seriesService.GetSeries(seriesId);
            }
            catch (ModelNotFoundException)
            {
                return null;
            }
        }

        public List<Series> GetAuthorSeries(int authorId)
        {
            return authorId > 0 ? _seriesService.GetByAuthorId(authorId) ?? new List<Series>() : new List<Series>();
        }

        public Series AddSeries(Series series)
        {
            return _seriesService.AddSeries(series);
        }

        public void DeleteSeries(int seriesId)
        {
            _seriesService.Delete(seriesId);
        }
    }
}
