import { get, set } from 'lodash';

// Talearr: Chaptarr's "Add book" modal defaulted to monitoring every book by the author
// and all new books. Talearr defaults to "Only this book" and no new books. This runs once
// per browser: saved defaults from before Talearr are switched to the safe values, and the
// marker keeps any choice made after that.
const MARKER_PATH = 'search.bookDefaults.talearrSafeBookDefaults';

const monitorPaths = [
  'search.bookDefaults.monitor',
  'search.bookDefaults.audiobookMonitor',
  'search.bookDefaults.ebookMonitor'
];

const monitorNewItemsPaths = [
  'search.bookDefaults.monitorNewItems',
  'search.bookDefaults.audiobookMonitorNewItems',
  'search.bookDefaults.ebookMonitorNewItems'
];

export default function migrateTalearrBookDefaults(persistedState) {
  if (!get(persistedState, 'search.bookDefaults') || get(persistedState, MARKER_PATH)) {
    return;
  }

  monitorPaths.forEach((path) => set(persistedState, path, 'specificBook'));
  monitorNewItemsPaths.forEach((path) => set(persistedState, path, 'none'));
  set(persistedState, MARKER_PATH, 1);
}
