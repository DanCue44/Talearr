# Migrating from Readarr, Bookshelf or LazyLibrarian

System > Migrate brings another book manager's library into TaleArr. It never changes the
source: the API is only read with GET requests, and a database file is copied to a temp folder
and only the copy is opened, read-only.

## Sources

| Source | How it is read | Ids |
|---|---|---|
| Readarr | API (URL + API key) or `readarr.db` / backup zip | Goodreads (looked up by edition id first) |
| Bookshelf | Same API and database layout as Readarr | Hardcover; not used as Goodreads ids, so ASIN, ISBN, title |
| LazyLibrarian | API: `getIndex` (authors), `getAllBooks` (books) | Numeric BookID used as a Goodreads hint; ISBN-13; title |

A Readarr backup zip is in Readarr's System > Backup; `readarr.db` is in its config folder.
Either can be uploaded from the page or read from a path on the server (for example Readarr's
appdata mounted read-only into the TaleArr container).

## What is brought over

| Source item | In TaleArr |
|---|---|
| Tags | Created by label when missing |
| Root folders | Created when the (mapped) path exists here, as an ebook or audiobook root folder; upstream scans a new root folder and matches its files |
| Quality and metadata profiles | Matched by name only. Readarr's qualities differ from Chaptarr's, so a profile with no TaleArr namesake falls back to the root folder's profile |
| Download clients, indexers | Created when TaleArr has the same implementation (they are Readarr's), with the source's settings; **disabled** unless "Enable imported clients and indexers" is on. Existing names are left alone. Indexers synced by Prowlarr are skipped: add TaleArr as an app in Prowlarr |
| Authors | Matched to a metadata author through their books (or an exact name search) and queued through upstream's pending author import, with the source's author monitoring and "monitor new items" |
| Books | Each monitored book is matched (Goodreads edition id, ASIN, ISBN-13, then title and author; an identifier hit must still fit the author). Only those books are monitored. A book already in TaleArr is monitored in place |
| Files on disk | Matched by the scan each author add (and each new root folder) runs. A rescan of the existing root folders used is queued as well |

Media type: Readarr instances usually hold one type. "auto" treats a root folder with "audio"
in its path as audiobooks; or pick ebooks or audiobooks for the whole source. LazyLibrarian's
Status and AudioStatus become ebook and audiobook wants.

Path mappings (`/books => /ebooks`, one per line) translate the source's paths when the library
is mounted elsewhere in TaleArr's container. An author whose folder is not inside a TaleArr root
folder goes to the default root folder for its media type; the report says so, and files in the
old folder are not matched.

## Preview and apply

Preview runs as a command (it can take a while: one or more metadata lookups per monitored
book) and writes `talearr-migration/plan.json` in the config folder. The report lists every
item with one action:

- `create`: will be added; `monitor`: in TaleArr, will be monitored; `map`: profile matched by name
- `exists`: already there, left alone; `skip`: not imported (option off, or Prowlarr-synced)
- `unmatched`: no metadata match or no same-name profile; add by hand afterwards
- `conflict`: needs a decision (path not found here, root folder of the other media type,
  a download client or indexer type TaleArr does not have)

Apply carries out exactly that plan (no new lookups), then removes `plan.json` (it holds the
source's client and indexer settings) and keeps `talearr-migration/last-report.json`. Apply is
safe to repeat: tags, clients, indexers and root folders that exist are skipped, and queued
authors that already exist only get their books monitored. The API key is never written to
disk; a restart between preview and apply keeps the plan.

## API

- `GET /api/v1/talearr/migration`: state (`idle`, `queued`, `running`, `done`, `failed`),
  progress, whether a plan exists, and the report.
- `POST /api/v1/talearr/migration/preview`: `{source, url, apiKey}` or `{source, backupPath}`,
  plus `mediaType`, `pathMappings: [{from, to}]`, `importRootFolders`, `importDownloadClients`,
  `importIndexers`, `enableImportedProviders`, `searchOnAdd`.
- `POST /api/v1/talearr/migration/upload`: multipart file (`.db` or `.zip`), returns `backupPath`.
- `POST /api/v1/talearr/migration/apply`, `DELETE /api/v1/talearr/migration` (discard the plan).

## Rollback

Nothing to undo for Chaptarr: everything is created through upstream's own services (authors,
books, tags, root folders, providers of types Chaptarr knows). The migration files in
`talearr-migration/` can be deleted.

## Limits

- Readarr's `/api/v1/edition?bookId=` is used for ISBN and ASIN when `/book` does not embed
  editions; on a version without it, books match by title and author only.
- Custom formats, release profiles, delay profiles, notifications, import lists, naming and
  remote path mappings are not migrated.
- Quality profiles are not recreated (Readarr's quality ids do not exist in Chaptarr).
