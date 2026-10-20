import { get, set } from 'lodash';

// Talearr: the Books page shows the library (monitored books) instead of every catalog book.
// This runs once per browser: a saved "All" filter from before is switched to "Monitored", and
// the marker keeps any choice made after that.
const MARKER_PATH = 'bookIndex.talearrLibraryFirst';
const FILTER_PATH = 'bookIndex.selectedFilterKey';

export default function migrateTalearrLibraryFirst(persistedState) {
  if (get(persistedState, MARKER_PATH)) {
    return;
  }

  if (get(persistedState, FILTER_PATH) === 'all') {
    set(persistedState, FILTER_PATH, 'monitored');
  }

  set(persistedState, MARKER_PATH, 1);
}
