import _ from 'lodash';
import { createSelector } from 'reselect';
import filterCollection from 'Utilities/Array/filterCollection';
import sortCollection from 'Utilities/Array/sortCollection';
import createCustomFiltersSelector from './createCustomFiltersSelector';

function createBooksClientSideCollectionSelector(uiSection) {
  return createSelector(
    (state) => _.get(state, 'books'),
    (state) => _.get(state, 'authors'),
    (state) => _.get(state, uiSection),
    createCustomFiltersSelector('books', uiSection),
    (state) => state.talearrBookTags.byBookId,
    (bookState, authorState, uiSectionState = {}, customFilters, talearrBookTagsByBookId) => {
      // TaleArr: book tags live outside the book resource; filter predicates read them here.
      const state = Object.assign({}, bookState, uiSectionState, { customFilters, talearrBookTagsByBookId });

      const books = state.items;
      for (const book of books) {
        book.author = authorState.items[authorState.itemMap[book.authorId]];
      }

      const filtered = filterCollection(books, state);
      const sorted = sortCollection(filtered, state);

      return {
        ...bookState,
        ...uiSectionState,
        customFilters,
        items: sorted,
        totalItems: state.items.length
      };
    }
  );
}

export default createBooksClientSideCollectionSelector;
