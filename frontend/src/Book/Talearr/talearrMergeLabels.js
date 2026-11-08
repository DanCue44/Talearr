// Talearr: wording for the change counts of a merge report (POST /talearr/merge/...), shared by
// the Duplicates page and the author page's Merge Author modal.
const changeLabels = {
  books: 'books moved',
  booksRemoved: 'duplicate book rows removed',
  authorsRemoved: 'duplicate author rows removed',
  editions: 'editions moved',
  editionsFolded: 'identical editions folded together',
  bookFiles: 'book files re-linked (files on disk untouched)',
  bookFilesWithBooks: 'book files that come along with their books (files on disk untouched)',
  history: 'history entries moved',
  downloadHistory: 'download history entries moved',
  pendingReleases: 'pending releases moved',
  pendingImports: 'pending imports moved',
  metadataFiles: 'metadata file records moved',
  extraFiles: 'extra file records moved',
  otherFiles: 'other file records moved',
  legacyBlacklist: 'legacy blacklist rows moved',
  seriesLinks: 'series links moved',
  series: 'author series links moved',
  narratorLinks: 'narrator links moved',
  blocklist: 'blocklist entries extended'
};

export function describeChange(key, count) {
  return `${count} ${changeLabels[key] || key}`;
}

export function readMergeError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}
