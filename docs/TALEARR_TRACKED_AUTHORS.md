# Talearr tracked authors

Readarr and Chaptarr follow an author: adding one book brings in the author's whole catalog
(every listing the metadata profile allows), just unmonitored. One request for 1984 put 773
George Orwell rows in the library. Talearr adds a per-author choice of which books are kept.

| Mode | Author page button | What the author keeps |
|---|---|---|
| **All books** (`full`) | Only my books | Every book the metadata profile allows, as before. New books appear on refresh. |
| **Only my books** (`tracked`) | Follow all books | Only books you added, requested, monitor or have files for. Refresh updates them and adds nothing else. |

Authors are **All books** unless something below made them **Only my books**.

## How authors get each mode

- **A book request creates an "Only my books" author.** Adding one book whose author is not in
  the library yet (the add-book modal, `POST /api/v1/book`, which Allseerr uses, wishlists and
  MAM filler imports) adds the author with only that book, in that format. A request that has to
  wait in the pending import queue (metadata server not ready) keeps this when it is retried.
- **A request for an author already in the library** does not change the author's mode; the book
  is added and monitored as before. For an "Only my books" author, a book in a format the author
  had no books in yet is added alone too.
- **Adding an author** (Add Author, `POST /api/v1/author`, import lists that follow authors,
  folder imports and disk scans) gives **All books**.
- A request with `addOptions.monitor = "all"` asks for the whole catalog, so its new author is
  **All books**.

## Switching

On the author page, **Only my books** (with a confirmation) and **Follow all books**. Either one
refreshes the author:

- **Follow all books**: the refresh brings the catalog in again, filtered by the metadata profile.
- **Only my books**: the refresh leaves the rest of the catalog out, so these book rows are
  removed: not monitored, no files, not added by hand, and no strict or hand-picked edition.
  Files on disk are never touched. Everything else stays and keeps being refreshed.

To keep a book that would be removed, monitor it first.

API (new routes only):

```
GET /api/v1/talearr/authors/{id}/catalog       {"authorId": 7, "mode": "full"}
PUT /api/v1/talearr/authors/{id}/catalog       {"mode": "tracked"}   400 for other values, 404 for an unknown author
```

The author resource also carries `talearrCatalogMode` (`"full"` or `"tracked"`), read only.

## How it works

- After the metadata profile, exclusions and the other normalization steps, the remote catalog
  of an "Only my books" author is cut down, per format, to books that match a kept local book
  (the same work-first identity rules refresh uses: Hardcover, Goodreads and Open Library work
  ids, then edition ids for books without one) and books the current add explicitly requested.
  A kept local book is one refresh would never prune: monitored, added by hand, strict or
  hand-picked edition, or with files.
- The same step runs on add (`AuthorLibraryService`) and on refresh (`RefreshAuthorService`,
  after the Open Library fallback books are matched to their server book). A local book that is
  not kept has no remote match left, so the existing refresh pruning removes it.
- The log shows one line per refresh: `Author 'X' tracks only its own books; returning N of M
  books (... already in the library, ... requested, ... left out of the catalog)`.

## Storage and rollback

One `talearr.authorcatalog.<authorId>` row (`tracked`) per "Only my books" author in the
`Config` table, and a short-lived `talearr.authorcatalog.pending.<providerId>` row while a
request waits in the pending import queue. No schema change. Stock Chaptarr ignores these rows,
so after a rollback every author is followed in full again and its next refresh brings the
catalog back. Deleting an author removes its row.

## Limits

- A file for a book that is not in an "Only my books" author's library is not matched by a disk
  scan or manual import of that author, because the book row does not exist. Request the book
  or switch the author to **Follow all books** first.
- Series pages only list the series books the author has.
- Merging two authors keeps the kept author's mode.
