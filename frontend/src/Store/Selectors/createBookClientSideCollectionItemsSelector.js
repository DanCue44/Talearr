import { createSelector, createSelectorCreator, defaultMemoize } from 'reselect';
import groupBookFormats from 'Book/Index/groupBookFormats';
import hasDifferentItemsOrOrder from 'Utilities/Object/hasDifferentItemsOrOrder';
import createBooksClientSideCollectionSelector from './createBooksClientSideCollectionSelector';

function createUnoptimizedSelector(uiSection) {
  return createSelector(
    createBooksClientSideCollectionSelector(uiSection),
    (books) => {
      // Talearr: with All formats, the ebook and audiobook of a work are one item
      if (uiSection === 'bookIndex' && books.selectedMediaType === 'all') {
        const items = groupBookFormats(books.items).map(({ book, groupBookIds, formats }) => {
          return {
            id: book.id,
            title: book.title,
            authorTitle: book.authorTitle,
            groupBookIds,
            formats
          };
        });

        return {
          ...books,
          items
        };
      }

      const items = books.items.map((s) => {
        const {
          id,
          title,
          authorTitle
        } = s;

        return {
          id,
          title,
          authorTitle
        };
      });

      return {
        ...books,
        items
      };
    }
  );
}

function bookListEqual(a, b) {
  return hasDifferentItemsOrOrder(a, b);
}

const createBookEqualSelector = createSelectorCreator(
  defaultMemoize,
  bookListEqual
);

function createBookClientSideCollectionItemsSelector(uiSection) {
  return createBookEqualSelector(
    createUnoptimizedSelector(uiSection),
    (book) => book
  );
}

export default createBookClientSideCollectionItemsSelector;
