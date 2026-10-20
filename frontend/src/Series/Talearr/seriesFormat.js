import { kinds } from 'Helpers/Props';

// Talearr: shared labels for the series pages (docs/TALEARR_SERIES.md).
export function formatStatus(state) {
  if (!state || !state.bookId) {
    return { label: 'Not in library', kind: kinds.DISABLED, title: 'Talearr has no entry of this format for the book yet. An author refresh usually adds it.' };
  }

  if (state.hasFiles) {
    return { label: 'Downloaded', kind: kinds.SUCCESS, title: 'Files are in the library.' };
  }

  if (!state.monitored) {
    return { label: 'Not monitored', kind: kinds.DISABLED, title: 'Talearr does not look for this format.' };
  }

  if (!state.authorMonitored) {
    return { label: 'Author paused', kind: kinds.WARNING, title: 'Monitored, but the author is not monitored for this format, so Talearr does not search. Turn the format on for the author to start searching.' };
  }

  return { label: 'Missing', kind: kinds.DANGER, title: 'Monitored and searched for.' };
}

export function describeRule(rule) {
  if (!rule) {
    return 'Not monitored';
  }

  if (rule.audiobook && rule.ebook) {
    return 'Audiobooks and ebooks';
  }

  return rule.audiobook ? 'Audiobooks' : 'Ebooks';
}

export function skipText(entry, allowBundles) {
  if (entry.skipReason === 'bundle') {
    return allowBundles ? null : 'Bundle: skipped while the grab policy does not allow bundles';
  }

  if (entry.skipReason === 'extra') {
    return 'Novella or extra: included only with "Include novellas and extras"';
  }

  return null;
}

export function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}
