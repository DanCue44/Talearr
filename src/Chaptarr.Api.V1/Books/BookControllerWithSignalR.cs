using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Author;
using Chaptarr.Http.Middleware;
using Chaptarr.Http.REST;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.SignalR;

namespace Chaptarr.Api.V1.Books
{
    public abstract class BookControllerWithSignalR : RestControllerWithSignalR<BookResource, Book>
    {
        protected readonly IBookService _bookService;
        protected readonly ISeriesBookLinkService _seriesBookLinkService;
        protected readonly IAuthorStatisticsService _authorStatisticsService;
        protected readonly IUpgradableSpecification _qualityUpgradableSpecification;
        protected readonly IMapCoversToLocal _coverMapper;

        protected BookControllerWithSignalR(IBookService bookService,
                                        ISeriesBookLinkService seriesBookLinkService,
                                        IAuthorStatisticsService authorStatisticsService,
                                        IMapCoversToLocal coverMapper,
                                        IUpgradableSpecification qualityUpgradableSpecification,
                                        IBroadcastSignalRMessage signalRBroadcaster)
            : base(signalRBroadcaster)
        {
            _bookService = bookService;
            _seriesBookLinkService = seriesBookLinkService;
            _authorStatisticsService = authorStatisticsService;
            _coverMapper = coverMapper;
            _qualityUpgradableSpecification = qualityUpgradableSpecification;
        }

        protected override BookResource GetResourceById(int id)
        {
            var book = _bookService.GetBook(id);
            var resource = MapToResource(book, true);
            return resource;
        }

        protected override BookResource GetResourceByIdForBroadcast(int id)
        {
            var book = _bookService.GetBook(id);
            var resource = MapToResource(book, false);
            return resource;
        }

        protected BookResource MapToResource(Book book, bool includeAuthor)
        {
            var stats = _authorStatisticsService.AuthorStatistics(book.AuthorId)
                ?.BookStatistics
                ?.FirstOrDefault(x => x.BookId == book.Id);

            var resource = book.ToResource(new BookResourceMappingOptions
            {
                IncludeAuthor = includeAuthor,
                IncludeOverview = true,
                IncludeLinks = true,
                Statistics = stats,
                FacadeContext = HttpContext?.GetReadarrFacadeContext()
            });

            MapCoversToLocal(resource);

            return resource;
        }

        protected List<BookResource> MapToResource(List<Book> books, bool includeAuthor)
        {
            return MapToResource(books, includeAuthor, null, includeOverview: true, includeLinks: true);
        }

        protected List<BookResource> MapToResource(List<Book> books,
                                                   bool includeAuthor,
                                                   IReadOnlyDictionary<int, BookStatistics> statsByBookId,
                                                   bool includeOverview,
                                                   bool includeLinks,
                                                   bool seriesLinksLoaded = false)
        {
            var facadeContext = HttpContext?.GetReadarrFacadeContext();

            // TaleArr: the book index passes books whose series links were just loaded for the
            // whole library; loading them a second time doubled the cost of GET /book.
            if (!seriesLinksLoaded)
            {
                var seriesLinks = _seriesBookLinkService.GetLinksByBook(books.Select(x => x.Id).ToList())
                    .GroupBy(x => x.BookId)
                    .ToDictionary(x => x.Key, y => y.ToList());

                foreach (var book in books)
                {
                    if (seriesLinks.TryGetValue(book.Id, out var links))
                    {
                        book.SeriesLinks = links;
                    }
                    else
                    {
                        book.SeriesLinks = new List<SeriesBookLink>();
                    }
                }
            }

            var resolvedStatsByBookId = statsByBookId ?? BuildBookStatisticsById(GetStatisticsFor(books));

            // Convert books to resources. Statistics are passed before mapping so narrator gating can
            // use file counts even when lean list endpoints intentionally skip BookFiles hydration.
            var result = books.Select(b =>
            {
                resolvedStatsByBookId.TryGetValue(b.Id, out var stats);
                return b.ToResource(new BookResourceMappingOptions
                {
                    IncludeAuthor = includeAuthor,
                    IncludeOverview = includeOverview,
                    IncludeLinks = includeLinks,
                    Statistics = stats,
                    FacadeContext = facadeContext
                });
            }).ToList();
            BookResourceMapper.WarnFacadeIdentityGaps(result, facadeContext, "book response");

            MapCoversToLocal(result.ToArray());

            return result;
        }

        // TaleArr: a page of books (wanted, cutoff, calendar) only needs the statistics of its
        // own authors. The per-author entries are cached separately, so a cold cache costs a few
        // small queries instead of the statistics of the whole library.
        internal const int PerAuthorStatisticsLimit = 50;

        private IEnumerable<AuthorStatistics> GetStatisticsFor(List<Book> books)
        {
            var authorIds = books.Where(b => b != null).Select(b => b.AuthorId).Distinct().ToList();
            if (authorIds.Count <= PerAuthorStatisticsLimit)
            {
                return authorIds.Select(id => _authorStatisticsService.AuthorStatistics(id)).ToList();
            }

            return _authorStatisticsService.AuthorStatistics();
        }

        protected static IReadOnlyDictionary<int, BookStatistics> BuildBookStatisticsById(IEnumerable<AuthorStatistics> authorStatistics)
        {
            return authorStatistics?
                .SelectMany(x => x.BookStatistics ?? new List<BookStatistics>())
                .GroupBy(x => x.BookId)
                .ToDictionary(x => x.Key, x => x.First())
                ?? new Dictionary<int, BookStatistics>();
        }

        private void FetchAndLinkBookStatistics(BookResource resource)
        {
            LinkAuthorStatistics(resource, _authorStatisticsService.AuthorStatistics(resource.AuthorId));
        }

        private void LinkAuthorStatistics(List<BookResource> resources, List<AuthorStatistics> authorStatistics)
        {
            var bookStatsDict = authorStatistics.SelectMany(x => x.BookStatistics).ToDictionary(x => x.BookId);

            foreach (var book in resources)
            {
                // For multi-edition resources, statistics are already set in ToResourceForEdition
                if (book.Statistics == null && bookStatsDict.TryGetValue(book.Id, out var stats))
                {
                    book.Statistics = stats.ToResource();
                }

                if (book.Statistics != null)
                {
                    book.HasFiles = book.Statistics.BookFileCount > 0;
                }
            }
        }

        private void LinkAuthorStatistics(BookResource resource, AuthorStatistics authorStatistics)
        {
            if (authorStatistics?.BookStatistics != null)
            {
                var dictBookStats = authorStatistics.BookStatistics.ToDictionary(v => v.BookId);

                resource.Statistics = dictBookStats.GetValueOrDefault(resource.Id).ToResource();
                if (resource.Statistics != null)
                {
                    resource.HasFiles = resource.Statistics.BookFileCount > 0;
                }
            }
        }

        private void MapCoversToLocal(params BookResource[] books)
        {
            foreach (var bookResource in books)
            {
                _coverMapper.ConvertToLocalUrls(bookResource.Id, MediaCoverEntity.Book, bookResource.Images);
            }
        }
    }
}
