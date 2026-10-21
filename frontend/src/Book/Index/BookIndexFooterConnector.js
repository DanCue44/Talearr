import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import createClientSideCollectionSelector from 'Store/Selectors/createClientSideCollectionSelector';
import createDeepEqualSelector from 'Store/Selectors/createDeepEqualSelector';
import BookIndexFooter from './BookIndexFooter';
import getBookIndexQuery from './getBookIndexQuery';
import groupBookFormats from './groupBookFormats';

function createUnoptimizedSelector() {
  return createSelector(
    createClientSideCollectionSelector('books', 'bookIndex'),
    (books) => {
      return books.items.map((s) => {
        const {
          id,
          authorId,
          foreignBookId,
          mediaType,
          hasFiles,
          monitored,
          status,
          statistics
        } = s;

        return {
          id,
          authorId,
          foreignBookId,
          mediaType,
          hasFiles,
          monitored,
          status,
          statistics
        };
      });
    }
  );
}

function createBookSelector() {
  return createDeepEqualSelector(
    createUnoptimizedSelector(),
    (book) => book
  );
}

function buildClientSideFooterStatistics(book, isAllFormats) {
  const authorIds = new Set();
  let monitoredBooks = 0;
  let fileCount = 0;
  let totalFileSize = 0;

  book.forEach((item) => {
    if (item.authorId > 0) {
      authorIds.add(item.authorId);
    }

    if (item.monitored) {
      monitoredBooks += 1;
    }

    const statistics = item.statistics || {};
    fileCount += statistics.bookFileCount || 0;
    totalFileSize += statistics.sizeOnDisk || 0;
  });

  // Talearr: with All formats, Books counts works (an ebook and audiobook pair is one book)
  const works = isAllFormats ? groupBookFormats(book) : null;

  if (works) {
    const monitoredIds = new Set(book.filter((item) => item.monitored).map((item) => item.id));

    monitoredBooks = works.filter(({ groupBookIds }) => {
      return groupBookIds.some((id) => monitoredIds.has(id));
    }).length;
  }

  return {
    totalBooks: works ? works.length : book.length,
    monitoredBooks,
    fileCount,
    totalFileSize,
    authorCount: authorIds.size
  };
}

function createMapStateToProps() {
  return createSelector(
    createBookSelector(),
    (state) => state.bookIndex,
    (state) => state.bookInfiniteScroll,
    (state) => state.bookIndex.selectedMediaType,
    (book, bookIndex, bookInfiniteScroll, selectedMediaType) => {
      const view = bookIndex.view || 'posters';

      if (view === 'posters') {
        const { queryKey, useClientSidePosters } = getBookIndexQuery(bookIndex, selectedMediaType);

        if (useClientSidePosters) {
          return {
            statistics: buildClientSideFooterStatistics(book, selectedMediaType === 'all'),
            isFetchingStatistics: false
          };
        }

        const query = bookInfiniteScroll?.queries?.[queryKey];
        const hasLoadingPage = Object.values(query?.pages || {}).some((page) => page.status === 'loading');
        const isFetchingStatistics = !query?.footerStatistics &&
          (!query || query?.buckets?.status === 'loading' || hasLoadingPage || query?.totalCount == null);

        return {
          statistics: query?.footerStatistics || {},
          isFetchingStatistics
        };
      }

      return {
        statistics: buildClientSideFooterStatistics(book, selectedMediaType === 'all'),
        isFetchingStatistics: false
      };
    }
  );
}

export default connect(createMapStateToProps)(BookIndexFooter);
