# Talearr and Audiobookshelf

Talearr knows what is already in your Audiobookshelf (ABS) library. It reads the library every
hour, matches the items to your books, skips releases for books ABS already has, and shows
"In Audiobookshelf" on the book page with a link to the item. Telling ABS about new imports
stays the job of the existing connection, which already does it.

## Setup

Settings > Connect > Audiobookshelf. This is Chaptarr's own Audiobookshelf connection; Talearr
adds three settings to it.

| Setting | Default | What it does |
|---|---|---|
| Host, Port, Use SSL, Url Base, API Key | | Chaptarr's connection settings. The API key's ABS user needs access to the libraries. |
| Notification triggers: On Release Import, On Upgrade, On Rename | on | Chaptarr's scan after import: a targeted watcher update for the imported folder, or a library scan when the ABS watcher is off, so new books appear right away. |
| **Sync Library** | on | Read the mapped ABS libraries every hour (GET requests only) and when the connection is saved. |
| **Skip Books Already in Audiobookshelf** | on | Do not grab a book ABS already has. See [What counts](#what-counts). |
| **Link URL** (advanced) | empty | The address your browser uses for ABS, for the book page links. Empty: built from Host, Port and Url Base, which may be a Docker name your browser cannot reach. |
| Root folder mappings | | Chaptarr's mapping of each Talearr root folder to the ABS library folder that holds the same files. Talearr also uses it to translate ABS paths into its own paths, because the containers usually see the share under different names. |

Test checks the connection as before and, with Sync Library on, that every library Talearr
would read can be listed with this key.

Example (Unraid): ABS mounts `/mnt/user/Media/Books` as `/books` and
`/mnt/user/Media/Audiobooks` as `/audiobooks`; Talearr mounts them as `/ebooks` and
`/audiobooks`. Map the Talearr `/ebooks` root to the ABS `Ebooks - /books` folder and
`/audiobooks` to `Audiobooks - /audiobooks`. An ABS item at `/books/R.F. Kuang/The Poppy War`
is then `/ebooks/R.F. Kuang/The Poppy War` to Talearr.

Without mappings Talearr reads every ABS book library (or the single library picked in older
Chaptarr versions) and uses ABS paths as they are.

## Sync

System > Tasks > Sync Audiobookshelf Library runs every 60 minutes; the connection dialog shows
the last run and has **Sync now**. For each library it reads every item (title, subtitle,
author, narrator, ASIN, ISBN, duration, abridged flag, whether it has audio files or an ebook,
and the path), skipping missing items and podcasts. If ABS cannot be reached, the previous copy
is kept and the error is shown in the dialog, so a short outage never makes Talearr forget the
library and grab everything again.

Each item is matched to Talearr books, first match wins:

1. **Path**: the item's folder (through the mapping) holds files Talearr imported. These are
   Talearr's own books; they show the badge but never stop a grab, so upgrades keep working.
2. **ASIN**, then **ISBN** (ISBN-10 and ISBN-13 compare equal): an edition with the same
   identifier.
3. **Title and author**: an author with the same name (initials and spacing ignored, so
   "R.F. Kuang" is "R. F. Kuang"), then the release title scorer from
   [TALEARR_MATCHING.md](TALEARR_MATCHING.md) with the item's title, and with title plus
   subtitle. ABS titles are clean, so nothing may be left over: "Dune Messiah" never matches
   "Dune", and "The Exchange: After the Firm" never matches "The Firm".

A match covers every row of the same work and media type: the audiobook and its recordings for
an item with audio files, the ebook for an item with an ebook file.

## What counts

A release is skipped ("In Audiobookshelf" in interactive search, a soft filter you can still
grab) when every book it is for is in ABS and:

- the connection has Skip Books Already in Audiobookshelf on;
- Talearr has no files for the book (with files, upgrades and the
  [recordings](TALEARR_RECORDINGS.md) upgrade guard decide as before);
- the ABS item is not Talearr's own import;
- for an ebook: the item has an ebook file. An audiobook never stands in for an ebook, or the
  other way round;
- for an audiobook: the item is that recording.
  - Abridged or unabridged must agree. The item's answer is ABS's abridged flag, "abridged" or
    "unabridged" in its title, subtitle or folder name, else its duration against the wanted
    edition's (under 60% is abridged, 90% or more is unabridged). The wanted answer is the
    recordings resolver's. An unmarked item counts as unabridged, like an unmarked release under
    the grab policy. So an abridged copy in ABS does not stop Talearr grabbing a wanted
    unabridged recording, and an unabridged copy does not satisfy a wanted abridged one. With
    the grab policy's abridged setting at "Any", this check is off.
  - With more than one recording, the item goes to the recording with the same narrator, else
    to the primary recording when the narrator is unknown. With one recording, any narrator
    counts.

## Sidecar files

Chaptarr can write Audiobookshelf's `metadata.json` and `cover.jpg` next to each book. Both are
off by default and set per root folder, separately for audiobooks and ebooks: Settings > Media
Management > Root Folders > edit a folder > **Write AudioBookShelf metadata.json** and **Write
AudioBookShelf cover file**. They are written after an import and after an author refresh or
rescan, once per book folder (from the first file of a multi-file audiobook). Talearr changes what
goes into them, not when they are written.

### metadata.json

- **Title.** The book's own title without format noise, the same one the folder is named after
  (see [TALEARR_AUDIOBOOKS.md](TALEARR_AUDIOBOOKS.md#book-title-not-the-edition-title)). Chaptarr
  used the matched edition's title: the 1984 import on 2026-10-16 wrote
  `"title": "Nineteen Eighty-Four (MP3 Book)"`; it is now `"1984"`.
- **Subtitle.** The edition's subtitle, else the book's, kept only when it is a real subtitle. A
  subtitle that is only format noise (`Unabridged`, `MP3 Book`), only a genre (`A Novel`,
  `A Thriller`) or the title again is left out.
- **Names.** Authors, narrators, series and genres have runs of spaces (including no-break
  spaces) collapsed and are trimmed, and duplicates that differ only in spacing or case are
  dropped: `"Samuel  West"` is written as `"Samuel West"`.
- Everything else (description, ISBN, ASIN, dates, chapters) is unchanged. The same name cleanup
  applies to the author and narrator tags of a merged M4B. Chaptarr's own audio tag writing
  (Settings > Metadata > Tag Audio Files with Metadata) is upstream behaviour and unchanged.

### cover.jpg

Chaptarr wrote the matched edition's first cover URL, whatever its size. For the 1984 import that
was a Goodreads thumbnail (`9834606._SX50_.jpg`, 50x67 pixels, 1.5 KB), and Audiobookshelf used it
as the book's cover. Talearr now:

1. **Collects candidates**, in this order: the edition's covers, the book's covers, then the covers
   of other editions of the same recording (audiobooks: an edition with the same narrator, Audible
   editions first; ebooks: the same ISBN-13). A Goodreads or Amazon URL with a size token
   (`._SX50_`, `._SY75_`, `._SL500_`, `._AC_SY400_`) is also tried without it, first, because the
   CDN then returns the stored original (the 1984 thumbnail becomes 318x424).
2. **Downloads** up to 6 of them and reads each image's size. Failed downloads, non-images and
   known placeholder images are skipped; a listed thumbnail is skipped when its full-size form
   worked.
3. **Picks** the first Audible image, or image of the edition itself, that is at least 500 px on
   the short side (it stops downloading there). Otherwise it picks the largest image found, so an
   image under 300 px is never written while a larger one is available.
4. **Never writes a thumbnail.** When the largest image is under 150 px on the short side, nothing
   is written, so Audiobookshelf keeps the embedded cover or its own match instead of a postage
   stamp. A book in that state is not downloaded again for 12 hours.
5. **Never downgrades.** When the folder already has a cover that Talearr may replace (see below),
   it is replaced only by an image at least as large (short side, then area). A PNG or WebP chosen
   for `cover.jpg` is re-encoded as JPEG. The file is written to `cover.jpg.partial~` first and
   then moved into place.

When the candidate list changes (an edition gains a cover, or Talearr's rules change), the cover
is looked at once more under these rules. A cover written by an older Talearr or Chaptarr is
therefore re-checked once after the update and replaced only when a larger image is found.

### What is never overwritten

Upstream's rules decide whether a sidecar may be replaced at all, and Talearr keeps them:

- A `metadata.json` or `cover.jpg` that this provider did not write (from Audiobookshelf with
  "Store metadata with item" on, from the download, or put there by you) is never touched, not even
  when it is smaller.
- A file this provider wrote and that was changed since (Audiobookshelf saved its own cover or
  metadata over it, or you edited it) is released: Chaptarr stops tracking its content and never
  writes it again.
- After an import into a folder that already holds either file, the file is left alone.
- To have Talearr write a file again, delete it and refresh the author.

Code: `src/NzbDrone.Core/Talearr/Metadata/` (`TalearrSidecarText`, `TalearrSidecarCover`,
`TalearrSidecarCoverWriter`, `TalearrSidecarImage`), hooked from `AudioBookShelfMetadata`
(`BuildMetadata`, `AddStringArray`, `BookImages`) and `MetadataService.ProcessBookImages`. No
setting, no schema change; the tracked hash in the existing `MetadataFiles` table now covers the
candidate list. After a rollback Chaptarr writes its own title and first cover URL again, under
the same never-overwrite rules.

## API

Additive routes; nothing upstream changes.

| Route | What it does |
|---|---|
| `GET /api/v1/talearr/audiobookshelf/book/{bookId}` | `inAudiobookshelf`, `skipsGrabs`, `skipReason` and the matched `items` (connection, ABS item id, title, narrators, duration, `abridged`, `hasAudio`, `hasEbook`, `matchedBy`, `managedByTalearr`, `url`). |
| `GET /api/v1/talearr/audiobookshelf/status` | Per connection: sync and skip settings, last sync, last error, item and match counts. |
| `POST /api/v1/talearr/audiobookshelf/sync?connectionId=` | Queues a sync of one connection or all (`202`). |

## Storage and rollback

- The connection is Chaptarr's own `AudioBookShelf` row in the `Notifications` table. Talearr
  adds `talearrSyncLibrary`, `talearrSkipOwned` and `talearrLinkUrl` to its `Settings` JSON.
  Stock Chaptarr reads that JSON with System.Text.Json, which skips keys it does not know, so
  after a rollback Chaptarr keeps using the connection (scans, mappings) unchanged. If Chaptarr
  saves the connection, the three keys are dropped and Talearr later uses the defaults (on, on,
  empty).
- Talearr deliberately adds no connection type of its own: on start, Chaptarr deletes every
  connection whose implementation it does not know
  (`ProviderFactory.RemoveMissingImplementations`), which would lose the setup on a rollback.
- The library cache is in `talearr.db` (schema version 2: `AbsItems`, `AbsItemBooks`,
  `AbsSyncState`). Delete it and the next sync refills it. No `chaptarr.db` schema change, no
  migrations, no `config.xml` edits.
- The scheduled task row is removed by Chaptarr on its next start, like the recordings one.

Checked against stock Chaptarr 0.9.965 on a data folder Talearr had written: it started, listed
and tested the Audiobookshelf connection with its mappings, removed the Talearr task rows and a
probe row with an unknown implementation, kept the migration version, and Talearr then started
on the same folder and synced again.
