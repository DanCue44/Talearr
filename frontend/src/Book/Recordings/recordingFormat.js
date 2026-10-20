// Talearr: display helpers for the Recordings section.

export function formatRuntime(minutes) {
  if (!minutes || minutes <= 0) {
    return '-';
  }

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  return hours ? `${hours}h ${String(rest).padStart(2, '0')}m` : `${rest}m`;
}

export function formatNarrators(narrators) {
  return narrators && narrators.length ? narrators.join(', ') : 'Unknown narrator';
}

export function formatYear(releaseDate) {
  return releaseDate ? String(releaseDate).substring(0, 4) : '';
}

const abridgedSources = {
  audible: 'Audible',
  metadata: 'edition metadata',
  runtime: 'runtime',
  files: 'file length',
  none: 'no answer'
};

export function formatAbridgedSource(source) {
  return abridgedSources[source] || source || 'no answer';
}

export function abridgedLabel(abridged) {
  if (abridged === 'abridged') {
    return 'Abridged';
  }

  if (abridged === 'unabridged') {
    return 'Unabridged';
  }

  return 'Unknown';
}

export function describeRecording(recording) {
  const parts = [formatNarrators(recording.narrators), abridgedLabel(recording.abridged)];
  const year = formatYear(recording.releaseDate);

  if (year) {
    parts.push(year);
  }

  return parts.join(', ');
}

export function formatFiles(recording) {
  const files = recording.files;

  if (!recording.hasFiles || !files) {
    return recording.monitored ? 'Missing' : 'Not monitored';
  }

  const details = [files.quality];

  if (files.bitrateKbps) {
    details.push(`${files.bitrateKbps} kbps`);
  }

  if (files.count > 1) {
    details.push(`${files.count} files`);
  }

  return details.filter(Boolean).join(', ');
}
