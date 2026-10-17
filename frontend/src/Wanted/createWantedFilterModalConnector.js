import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import FilterModal from 'Components/Filter/FilterModal';
import { customFilterTypes, filterBuilderProps } from './talearrWantedFilters';

// TaleArr: the custom filter editor for a Wanted page ('missing' or 'cutoffUnmet').
export default function createWantedFilterModalConnector(sectionKey, setFilterAction) {
  function createMapStateToProps() {
    return createSelector(
      (state) => state.books.items,
      (state) => state.wanted[sectionKey].items,
      (books, pageItems) => {
        return {
          // Genre suggestions from the whole library when it is loaded, else from this page.
          sectionItems: books.length ? books : pageItems,
          filterBuilderProps,
          customFilterType: customFilterTypes[sectionKey]
        };
      }
    );
  }

  return connect(createMapStateToProps, { dispatchSetFilter: setFilterAction })(FilterModal);
}
