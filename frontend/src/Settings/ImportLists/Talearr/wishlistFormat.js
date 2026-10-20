import { kinds } from 'Helpers/Props';

// Talearr: labels for wishlists (docs/TALEARR_WISHLISTS.md).
export const stateLabels = {
  new: { label: 'New', kind: kinds.PRIMARY },
  monitored: { label: 'Monitored', kind: kinds.SUCCESS },
  added: { label: 'Added', kind: kinds.SUCCESS },
  queued: { label: 'Adding', kind: kinds.SUCCESS },
  inLibrary: { label: 'Already wanted', kind: kinds.INFO },
  excluded: { label: 'Excluded', kind: kinds.WARNING },
  noMatch: { label: 'Not found', kind: kinds.DISABLED },
  failed: { label: 'Failed', kind: kinds.DANGER }
};

export const formatNames = {
  goodreads: 'Goodreads export',
  storygraph: 'StoryGraph export',
  audible: 'Audible wishlist',
  csv: 'CSV list',
  hardcover: 'Hardcover list'
};

export function describeFormats(wishlist) {
  if (wishlist.audiobook && wishlist.ebook) {
    return 'Audiobook and ebook';
  }

  return wishlist.audiobook ? 'Audiobook' : 'Ebook';
}

export function describeSource(wishlist) {
  if (wishlist.kind === 'hardcoverList') {
    return wishlist.url;
  }

  return wishlist.uploaded ? 'Uploaded file' : wishlist.path;
}

export function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}
