# TaleArr wishlists

Settings > Import Lists > **Wishlists** adds the books on a wishlist to TaleArr, monitoring only
those books, in the formats you choose.

## What upstream already has

Chaptarr's own import lists stay as they are and cover:

| Source | Upstream import list |
|---|---|
| Hardcover Want to Read, Owned, Currently Reading, Read | Hardcover Library |
| Goodreads shelves (including to-read), Goodreads lists and series | Goodreads Bookshelf, Goodreads List, Goodreads Series |
| Another Chaptarr, LazyLibrarian | Chaptarr, LazyLibrarian |

Wishlists fill the gaps:

| Source | How |
|---|---|
| **StoryGraph to-read** | StoryGraph has no public API. Export the library (Manage account > Export StoryGraph library) and upload the CSV, or keep the file on the server and point the wishlist at it. Rows with Read Status `to-read` are taken. |
| **Audible wishlist** | Audible has no official API for wishlists, and the unofficial one needs your Amazon login and a registered device, which TaleArr does not handle. Export the wishlist with [audible-cli](https://github.com/mkb79/audible-cli) (`audible wishlist export --format csv`) and upload it; every row counts and the ASIN is used for matching. |
| **Hardcover lists** | Any of your other Hardcover lists by URL (`https://hardcover.app/@you/lists/name`), read with the Hardcover API token from Settings > Metadata. |
| **Goodreads export** | The full "My Books" CSV export (My Books > Import and export), for libraries too big for the shelf RSS. Rows on the `to-read` exclusive shelf are taken. |
| **Any CSV** | A file with a `title` column and optional `author`/`authors`, `isbn`/`isbn13`, `asin` and `status` columns; comma, semicolon or tab separated. |

## What a wishlist does

Each new row is matched to a book: known ids first (Hardcover lists), then the ASIN, the ISBN,
then the title and author through the metadata server's search (a title search has to agree on the
title and the author's last name). Then, in the wishlist's formats:

- a book already in the library gets that format monitored (and the author's switch for that
  format turned on when it was off, so it is searched),
- a book that is not gets its author queued through upstream's pending author import, exactly as
  an import list does, with "Only this book": only that book is monitored, "monitor new items"
  is None, so the rest of the author's catalog and future releases stay unmonitored (the Phase 1
  rule),
- with **Search when added** (on) the book is searched.

A row is acted on once. If you unmonitor or delete the book later, the wishlist leaves it alone.
Rows that match nothing are looked up again after a week. Rows on the import list exclusions are
skipped. A sync does at most 60 metadata lookups; the rest wait for the next sync.

New books use the default root folder for the format (Settings > Media Management), else the
first root folder set up for it, with that root folder's quality and metadata profiles.

**Preview** (in the add dialog and on each wishlist) is a dry run: it shows every row, the book it
matched and what a sync would do, and changes nothing. **Sync now** adds the new rows at once;
with **Sync automatically** on, the Sync Wishlists task does it every 12 hours. An uploaded file
is synced as it was uploaded; a server path is read again each time, so a newer export dropped in
place is picked up.

## Storage

- Each wishlist is one row in the existing `Config` table, key `talearr.wishlist.<id>`, holding
  JSON: the source, formats, options, last sync and result, and which rows it has acted on.
- An uploaded CSV is kept as `<config folder>/talearr-wishlists/<id>.csv`.
- The Sync Wishlists task is a `ScheduledTasks` row like TaleArr's other tasks.

No schema change, no migrations, `config.xml` untouched, and no new import list provider:
stock Chaptarr deletes provider rows whose implementation it does not know, so wishlists are not
providers.

## API

All additive under `/api/v1/talearr/wishlists`.

| Route | What it does |
|---|---|
| `GET /api/v1/talearr/wishlists` | The wishlists. |
| `POST /api/v1/talearr/wishlists` | `{"name", "kind": "file" \| "hardcoverList", "csv" or "path" or "url", "audiobook", "ebook", "searchOnAdd", "toReadOnly", "enabled"}`. `400` with a message when the file or URL cannot be used. |
| `PUT /api/v1/talearr/wishlists/{id}` | Changes a wishlist (fields left out stay). |
| `DELETE /api/v1/talearr/wishlists/{id}` | Removes it. Books it added stay. |
| `POST /api/v1/talearr/wishlists/preview` | Dry run for an unsaved file or list (same body as `POST`). |
| `GET /api/v1/talearr/wishlists/{id}/preview` | Dry run for a saved wishlist. |
| `POST /api/v1/talearr/wishlists/{id}/sync` | Adds the new rows now and returns what happened per row. |

## Rolling back to Chaptarr

Chaptarr ignores the `talearr.wishlist.*` rows and the `talearr-wishlists` folder and drops the
scheduled task row on start. Books and authors the wishlists added are ordinary library entries
and stay. What stops is the syncing; rolling forward again resumes where it left off, without
adding a row twice.
