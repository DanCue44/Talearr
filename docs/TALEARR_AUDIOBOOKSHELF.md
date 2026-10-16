# TaleArr and Audiobookshelf

TaleArr knows what is already in your Audiobookshelf (ABS) library. It reads the library every
hour, matches the items to your books, skips releases for books ABS already has, and shows
"In Audiobookshelf" on the book page with a link to the item. Telling ABS about new imports
stays the job of the existing connection, which already does it.

## Setup

Settings > Connect > Audiobookshelf. This is Chaptarr's own Audiobookshelf connection; TaleArr
adds three settings to it.

| Setting | Default | What it does |
|---|---|---|
| Host, Port, Use SSL, Url Base, API Key | | Chaptarr's connection settings. The API key's ABS user needs access to the libraries. |
| Notification triggers: On Release Import, On Upgrade, On Rename | on | Chaptarr's scan after import: a targeted watcher update for the imported folder, or a library scan when the ABS watcher is off, so new books appear right away. |
| **Sync Library** | on | Read the mapped ABS libraries every hour (GET requests only) and when the connection is saved. |
| **Skip Books Already in Audiobookshelf** | on | Do not grab a book ABS already has. See [What counts](#what-counts). |
| **Link URL** (advanced) | empty | The address your browser uses for ABS, for the book page links. Empty: built from Host, Port and Url Base, which may be a Docker name your browser cannot reach. |
| Root folder mappings | | Chaptarr's mapping of each TaleArr root folder to the ABS library folder that holds the same files. TaleArr also uses it to translate ABS paths into its own paths, because the containers usually see the share under different names. |

Test checks the connection as before and, with Sync Library on, that every library TaleArr
would read can be listed with this key.

Example (Unraid): ABS mounts `/mnt/user/Media/Books` as `/books` and
`/mnt/user/Media/Audiobooks` as `/audiobooks`; TaleArr mounts them as `/ebooks` and
`/audiobooks`. Map the TaleArr `/ebooks` root to the ABS `Ebooks - /books` folder and
`/audiobooks` to `Audiobooks - /audiobooks`. An ABS item at `/books/R.F. Kuang/The Poppy War`
is then `/ebooks/R.F. Kuang/The Poppy War` to TaleArr.

Without mappings TaleArr reads every ABS book library (or the single library picked in older
Chaptarr versions) and uses ABS paths as they are.

## Sync

System > Tasks > Sync Audiobookshelf Library runs every 60 minutes; the connection dialog shows
the last run and has **Sync now**. For each library it reads every item (title, subtitle,
author, narrator, ASIN, ISBN, duration, abridged flag, whether it has audio files or an ebook,
and the path), skipping missing items and podcasts. If ABS cannot be reached, the previous copy
is kept and the error is shown in the dialog, so a short outage never makes TaleArr forget the
library and grab everything again.

Each item is matched to TaleArr books, first match wins:

1. **Path**: the item's folder (through the mapping) holds files TaleArr imported. These are
   TaleArr's own books; they show the badge but never stop a grab, so upgrades keep working.
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
- TaleArr has no files for the book (with files, upgrades and the
  [recordings](TALEARR_RECORDINGS.md) upgrade guard decide as before);
- the ABS item is not TaleArr's own import;
- for an ebook: the item has an ebook file. An audiobook never stands in for an ebook, or the
  other way round;
- for an audiobook: the item is that recording.
  - Abridged or unabridged must agree. The item's answer is ABS's abridged flag, "abridged" or
    "unabridged" in its title, subtitle or folder name, else its duration against the wanted
    edition's (under 60% is abridged, 90% or more is unabridged). The wanted answer is the
    recordings resolver's. An unmarked item counts as unabridged, like an unmarked release under
    the grab policy. So an abridged copy in ABS does not stop TaleArr grabbing a wanted
    unabridged recording, and an unabridged copy does not satisfy a wanted abridged one. With
    the grab policy's abridged setting at "Any", this check is off.
  - With more than one recording, the item goes to the recording with the same narrator, else
    to the primary recording when the narrator is unknown. With one recording, any narrator
    counts.

## API

Additive routes; nothing upstream changes.

| Route | What it does |
|---|---|
| `GET /api/v1/talearr/audiobookshelf/book/{bookId}` | `inAudiobookshelf`, `skipsGrabs`, `skipReason` and the matched `items` (connection, ABS item id, title, narrators, duration, `abridged`, `hasAudio`, `hasEbook`, `matchedBy`, `managedByTalearr`, `url`). |
| `GET /api/v1/talearr/audiobookshelf/status` | Per connection: sync and skip settings, last sync, last error, item and match counts. |
| `POST /api/v1/talearr/audiobookshelf/sync?connectionId=` | Queues a sync of one connection or all (`202`). |

## Storage and rollback

- The connection is Chaptarr's own `AudioBookShelf` row in the `Notifications` table. TaleArr
  adds `talearrSyncLibrary`, `talearrSkipOwned` and `talearrLinkUrl` to its `Settings` JSON.
  Stock Chaptarr reads that JSON with System.Text.Json, which skips keys it does not know, so
  after a rollback Chaptarr keeps using the connection (scans, mappings) unchanged. If Chaptarr
  saves the connection, the three keys are dropped and TaleArr later uses the defaults (on, on,
  empty).
- TaleArr deliberately adds no connection type of its own: on start, Chaptarr deletes every
  connection whose implementation it does not know
  (`ProviderFactory.RemoveMissingImplementations`), which would lose the setup on a rollback.
- The library cache is in `talearr.db` (schema version 2: `AbsItems`, `AbsItemBooks`,
  `AbsSyncState`). Delete it and the next sync refills it. No `chaptarr.db` schema change, no
  migrations, no `config.xml` edits.
- The scheduled task row is removed by Chaptarr on its next start, like the recordings one.

Checked against stock Chaptarr 0.9.965 on a data folder TaleArr had written: it started, listed
and tested the Audiobookshelf connection with its mappings, removed the TaleArr task rows and a
probe row with an unknown implementation, kept the migration version, and TaleArr then started
on the same folder and synced again.
