import { kinds } from 'Helpers/Props';

export function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

export function formatSize(bytes) {
  if (!bytes) {
    return '';
  }

  const mb = bytes / (1024 * 1024);
  return mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
}

export function volumeLabel(number) {
  const whole = Math.trunc(number);
  const padded = String(whole).padStart(2, '0');
  return number === whole ? `Vol. ${padded}` : `Vol. ${padded}${String(number - whole).replace(/^0/, '')}`;
}

export function formatDate(value) {
  if (!value) {
    return '';
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString();
}

export const stateLabels = {
  downloaded: 'Downloaded',
  downloading: 'Downloading',
  missing: 'Missing',
  unreleased: 'Not released yet',
  unmonitored: 'Not monitored'
};

export const stateKinds = {
  downloaded: kinds.SUCCESS,
  downloading: kinds.PURPLE,
  missing: kinds.DANGER,
  unreleased: kinds.INFO,
  unmonitored: kinds.DISABLED
};

export const kindOptions = [
  { key: 'manga', value: 'Manga' },
  { key: 'comic', value: 'Comics' }
];

export const monitorOptions = [
  { key: 'all', value: 'All volumes' },
  { key: 'future', value: 'Only volumes released from now on' },
  { key: 'none', value: 'Nothing (add without monitoring)' }
];

export function ageRatingOptions(ratings, emptyLabel) {
  return [{ key: '', value: emptyLabel }].concat((ratings || []).map((rating) => ({ key: rating, value: rating })));
}

export const sourceLabels = {
  digital: 'Digital',
  scan: 'Scan',
  c2c: 'c2c scan',
  unknown: 'Unknown'
};
