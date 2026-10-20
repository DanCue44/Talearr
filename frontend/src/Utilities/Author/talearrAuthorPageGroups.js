import translate from 'Utilities/String/translate';

// Talearr: author page groups (docs/TALEARR_AUTHOR_PAGE.md). The server sets book.talearrGroup
// on one author's book list. In the "All" view, main works are listed first and every other
// group goes into the collapsed "Other listings" section, in this order.
export const TALEARR_OTHER_GROUPS = [
  'omnibus',
  'guide',
  'adaptation',
  'duplicate',
  'otherLanguage'
];

const GROUP_LABEL_KEYS = {
  omnibus: 'TalearrOtherListingsOmnibus',
  guide: 'TalearrOtherListingsGuide',
  adaptation: 'TalearrOtherListingsAdaptation',
  duplicate: 'TalearrOtherListingsDuplicate',
  otherLanguage: 'TalearrOtherListingsOtherLanguage'
};

export function getTalearrGroupLabel(group) {
  return translate(GROUP_LABEL_KEYS[group] || 'TalearrOtherListingsDuplicate');
}

// A book monitored for the shown format or with files is always a main work, even when the
// group the server sent is older than the change (a SignalR update does not carry the group).
export function getTalearrBookGroup(book, mediaType) {
  if (!book) {
    return 'main';
  }

  const isMonitored = mediaType === 'ebook' ? book.ebookMonitored : book.audiobookMonitored;
  const hasFiles = book.hasFiles || (book.statistics && book.statistics.bookFileCount > 0);

  if (isMonitored || hasFiles) {
    return 'main';
  }

  const group = book.talearrGroup;

  return TALEARR_OTHER_GROUPS.indexOf(group) >= 0 ? group : 'main';
}

// Splits an already sorted list into main works and the other groups (empty groups left out).
export function splitTalearrGroups(books, mediaType) {
  const main = [];
  const byGroup = {};

  books.forEach((book) => {
    const group = getTalearrBookGroup(book, mediaType);

    if (group === 'main') {
      main.push(book);
      return;
    }

    if (!byGroup[group]) {
      byGroup[group] = [];
    }

    byGroup[group].push(book);
  });

  const otherGroups = TALEARR_OTHER_GROUPS
    .filter((group) => byGroup[group] && byGroup[group].length > 0)
    .map((group) => {
      return {
        key: group,
        label: getTalearrGroupLabel(group),
        items: byGroup[group]
      };
    });

  return {
    main,
    otherGroups
  };
}
