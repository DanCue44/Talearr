// Talearr: an ebook and an audiobook of the same work are separate book rows. With All formats
// selected, the Books page shows them as one item: same author and foreignBookId.

function getGroupKey(book) {
  if (!book.foreignBookId) {
    return `id:${book.id}`;
  }

  return `${book.authorId}|${book.foreignBookId}`;
}

function hasFile(book) {
  return !!(book.hasFiles || book.statistics?.bookFileCount > 0);
}

// The row a combined item opens: the format with files, else the audiobook, else the ebook.
function pickPrimary(books) {
  return books.find(hasFile) ||
    books.find((b) => b.mediaType !== 'ebook') ||
    books[0];
}

// Groups already filtered and sorted rows; a group sits where its first row was.
export default function groupBookFormats(books) {
  const groups = new Map();

  books.forEach((book) => {
    const key = getGroupKey(book);
    const group = groups.get(key);

    if (group) {
      group.push(book);
    } else {
      groups.set(key, [book]);
    }
  });

  return Array.from(groups.values()).map((group) => {
    const primary = pickPrimary(group);

    return {
      book: primary,
      groupBookIds: group.map((b) => b.id),
      formats: group.map((b) => ({
        mediaType: b.mediaType === 'ebook' ? 'ebook' : 'audiobook',
        hasFile: hasFile(b)
      }))
    };
  });
}

// Expands combined item ids to every row in their group.
export function expandGroupIds(ids, items) {
  const groupById = new Map();

  items.forEach((item) => {
    if (item.groupBookIds) {
      groupById.set(item.id, item.groupBookIds);
    }
  });

  return ids.flatMap((id) => groupById.get(id) || [id]);
}
