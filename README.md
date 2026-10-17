<div align="center">

<img src="Logo/talearr.png" width="160" alt="TaleArr logo">

# TaleArr

An ebook and audiobook collection manager, based on Chaptarr.

</div>

> TaleArr is a private fork and early work in progress.

## What is TaleArr?

TaleArr is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself a
fork of [Readarr](https://github.com/Readarr/Readarr) from the
[Servarr](https://wiki.servarr.com/) project family. It manages audiobook and ebook
libraries in one instance: narrator and edition aware metadata, separate audiobook and
ebook root folders, series handling, quality profiles and upgrades, and the usual
download client and indexer integrations.

TaleArr tracks Chaptarr closely. The differences today are the name and icon, a separate
TaleArr version, a "Works best with" block in System > Status (AllSeerr, NoBookEnds), a
grab policy (see below), two import additions (completed downloads that only hold archives
are unpacked before import, and downloaded files whose own language is not the wanted one
are rejected; see [docs/TALEARR_IMPORT.md](docs/TALEARR_IMPORT.md)), fixes to edition choice,
release title matching, Audible duplicates and duplicate rows on add (see [docs/TALEARR_MATCHING.md](docs/TALEARR_MATCHING.md)),
audiobook naming tokens, multi-disc import and an optional merge to M4B (see
[docs/TALEARR_AUDIOBOOKS.md](docs/TALEARR_AUDIOBOOKS.md)), audiobook recordings (abridged and
unabridged, or two narrators, side by side, with a recording-aware upgrade guard; see
[docs/TALEARR_RECORDINGS.md](docs/TALEARR_RECORDINGS.md)), Audiobookshelf library awareness
(see [docs/TALEARR_AUDIOBOOKSHELF.md](docs/TALEARR_AUDIOBOOKSHELF.md)), and its own Docker
image and CI. The icon master is
`Logo/talearr-icon-master.png`.

TaleArr behavior changes so far (all without schema changes, new settings or `config.xml` edits):

- **Metadata server probe.** A small GET to the root of the configured metadata server
  every 15 minutes (10 s timeout, backing off from 2 to 30 minutes while it is down, 5 minutes
  while slow or rate limited; a new problem is confirmed by a second probe 30 s later). Problems
  show in System > Status. A refresh during an outage keeps existing authors and books as they
  are and logs one warning per outage instead of an error per author. The server used is
  unchanged. State is in memory only.
- **Rejection reasons in interactive search.** Every rejected release shows a short label
  ("Language", "Wrong book", "Pack", "Size", "Blocklisted", ...) with the full reasons in a
  popover. The API adds `label` and `source` to each `rejectionDetails` entry; `rejections`
  and every existing field are unchanged. A new decision specification is labelled from its
  category or class name automatically.
- **Adding one book monitors only that book.** Through the UI or `POST /api/v1/book`, a book
  for a new author monitors only that book, and future releases stay unmonitored unless the
  request sets `monitorNewItems` / `audiobookMonitorNewItems` / `ebookMonitorNewItems`.
  `addOptions.monitor = "all"` still monitors the whole catalog. The add-book modal defaults to
  "Only this book" and "None"; saved browser defaults from before TaleArr are switched once.
- **Merge duplicates (Library > Duplicates).** Lists authors with the same name and book rows
  of one author with the same title and media type, and merges them: the kept row keeps its
  ids, everything else (editions, book files, history, queue and blocklist matches, series and
  narrator links) moves onto it, monitoring is combined, and the duplicate rows are removed in
  one transaction. Files on disk are never moved or deleted. Ebook and audiobook rows of one
  book are siblings and are never merged. A preview shows exactly what moves. API:
  `GET /api/v1/talearr/merge/candidates`, `POST /api/v1/talearr/merge/authors|books`
  (`targetId`, `sourceIds`, `dryRun`, `force`).
- **Large libraries.** Fewer full-library loads: the book index loads series links once with a
  single scan, Wanted, Cutoff Unmet, the calendar and author pages read statistics for their
  own authors only, and book routes by `titleSlug` (AllSeerr links) look the slug up instead
  of loading every book.

## Audiobooks

See [docs/TALEARR_AUDIOBOOKS.md](docs/TALEARR_AUDIOBOOKS.md).

- **Book title in names.** Folders and files are named after the book ("1984"), not the
  matched edition's store listing ("Nineteen Eighty-Four (MP3 Book)"), and format noise such as
  `(Audible Audio)`, `(Audio Cassette)` or `[Unabridged]` is dropped from the title.
- **Naming tokens.** `{Book SeriesPosition:00}` (zero padded), `{Book Year}` (empty when unknown),
  `{Abridged}` / `{Abridgement}`, and ready-made layouts in the naming help, including
  Audiobookshelf's `Author/Series/01 - 1994 - Title {Narrator}/files`.
- **Multi-disc import.** CD1/Disc 2/Part 03 folders and many-file rips import as one book in
  playing order (track and disc tags, then disc folder numbers, then a natural sort); files with
  the same name on different discs no longer overwrite each other; a folder holding two books
  (by album tags) is matched as two books.
- **Merge to M4B.** Settings > Conversion > Merge after import (off by default) and a Convert to
  M4B button on the book page merge multi-file mp3/m4a audiobooks into one chaptered M4B with
  FFmpeg, keeping the cover and tags. The originals are removed only after the M4B is verified
  (duration within 1%). Stored as the `talearr.audiobook.m4bafterimport` row in the `Config`
  table through the new `/api/v1/config/talearrm4b` route.
  **Convert library to M4B** in the same section merges every multi-file audiobook already in
  the library, one book at a time (manual only; a failed book keeps its files).
- **Audio quality ladder and ebook formats.** Settings > Profiles ranks releases the quality
  profile ranks equal: audio by tier (AAC 64 kbps+, MP3 128 kbps+ and lossless are good, under
  32 kbps or implausibly small ranks last), read from the title or estimated from size against
  the book's known runtime, and ebooks by a format order (EPUB > AZW3 > MOBI > PDF). Opt-in:
  upgrading low bitrate audio to a clearly better copy of the same recording, upgrading an ebook
  to a preferred format, and a minimum bitrate. `talearr.ladder.*` Config rows through
  `/api/v1/config/talearrqualityladder`.

## Recordings

See [docs/TALEARR_RECORDINGS.md](docs/TALEARR_RECORDINGS.md).

- **More than one recording per audiobook.** Keep the abridged and the unabridged reading, or
  two narrators, each with its own files and folder. The book page lists the recordings with
  narrator, runtime, abridged or unabridged (and where that answer came from), Audible
  marketplace and ASIN, and file status, and has "Add another recording" (unabridged, abridged,
  another narrator, pick an edition) and "Remove this recording" (asks first, never the primary
  recording, never automatic). Recordings are Chaptarr's own copy entries, so a rollback keeps
  them.
- **Audible facts.** Narrator, runtime and abridged or unabridged per edition from the Audible
  catalog of the chosen marketplace (US by default), cached in `talearr.db`. Audnexus is opt in.
  The grab policy and the `{Abridged}` naming tokens use the same answer.
- **Own folder per recording.** When the naming format would put two recordings in one folder,
  the copy gets ` (Abridged)`, the narrator, the edition year or the ASIN appended.
- **Upgrade guard.** An audiobook upgrade only replaces files of the same recording, never with
  fewer channels or a lower measured bitrate, and bitrate gains count only within one codec. An
  unabridged download for abridged files becomes its own recording (Keep both recordings).
- **Per-book abridged policy.** A book can have its own Abridged audiobooks value (unabridged
  only, abridged only, prefer unabridged, any), set by AllSeerr through
  `GET|PUT /api/v1/talearr/grabpolicy/book/{bookId}`. An abridged only book without files
  monitors its best abridged edition before it is searched; when none is known, its history says
  so. Additional recordings keep their own behaviour.
- **API** for AllSeerr and NoBookEnds: `GET|POST /api/v1/talearr/recordings`,
  `DELETE /api/v1/talearr/recordings/{bookId}`, `GET|PUT /api/v1/config/talearrrecordings`.

Storage: a separate `talearr.db` next to `chaptarr.db` (a rebuildable cache, plus the per-book
abridged values, which fall back to the global policy if it is lost), and `talearr.recordings.*`
and `talearr.upgrade.*` rows in the `Config` table. No schema change, no migrations, no
`config.xml` edits.

## Series

See [docs/TALEARR_SERIES.md](docs/TALEARR_SERIES.md).

- **Series page and Library > Series.** Every series with its books in order and the audiobook
  and ebook status of each.
- **Monitor a series** as audiobooks, ebooks or both: every main book is monitored and searched,
  and so is each new book that appears in the series later (applied after every author refresh
  and every 6 hours). It never unmonitors anything, follows the grab policy for box sets and
  leaves additional recordings alone.
- **API**: `GET|PUT /api/v1/talearr/series/{id}`, `GET /api/v1/talearr/series`,
  `POST .../apply`, `POST .../authors`.

Storage: one `talearr.series.monitor.*` row per monitored series in the `Config` table.
## Wishlists

See [docs/TALEARR_WISHLISTS.md](docs/TALEARR_WISHLISTS.md).

- **Settings > Import Lists > Wishlists**: StoryGraph to-read and Audible wishlist exports,
  Goodreads CSV exports, any title/author/ISBN/ASIN CSV, and Hardcover lists by URL (upstream's
  Goodreads and Hardcover Library import lists are unchanged and cover shelves and Want to Read).
- Each row is matched (ASIN, ISBN, then title and author) and added with "Only this book": only
  that book is monitored, in the chosen formats, once. Preview shows the matches first; Sync
  Wishlists runs every 12 hours.
- **API**: `/api/v1/talearr/wishlists` (list, add, change, remove, preview, sync).

Storage: one `talearr.wishlist.*` row per wishlist in the `Config` table and uploaded files in
`talearr-wishlists/` in the config folder. Not an import list provider, so a rollback deletes
nothing.

## Audiobookshelf

See [docs/TALEARR_AUDIOBOOKSHELF.md](docs/TALEARR_AUDIOBOOKSHELF.md).

- **Library awareness.** The existing Settings > Connect > Audiobookshelf connection gets
  Sync Library (on): TaleArr reads the mapped libraries every hour (GET only) and matches the
  items to books by path, ASIN, ISBN, then title and author.
- **No duplicate grabs.** Skip Books Already in Audiobookshelf (on) skips releases for books
  ABS already has and TaleArr has no files for, shown as "In Audiobookshelf" in interactive
  search. An abridged copy does not count for a wanted unabridged recording, and with several
  recordings the narrator decides which one an ABS copy is.
- **Book page badge.** "In Audiobookshelf", linking to the item (Link URL sets the address
  your browser uses).
- **Scan after import** stays the connection's own On Release Import trigger.
- **Sidecar files.** When a root folder writes Audiobookshelf's `metadata.json` and `cover.jpg`,
  the title is the book's own without format noise, names have stray spaces removed, and
  `cover.jpg` is the largest available image (full-size Goodreads and Amazon images, Audible
  covers of 500 px preferred), never a thumbnail and never smaller than the cover already there.
  Files Audiobookshelf or you put there are never overwritten.
- **API**: `GET /api/v1/talearr/audiobookshelf/book/{bookId}`, `GET .../status`,
  `POST .../sync`.

Storage: three extra keys in the connection's own settings JSON (stock Chaptarr skips them) and
a cache in `talearr.db`. Chaptarr deletes connection types it does not know on start, so
TaleArr adds none.

## Reader hand-off

Settings > Conversion > **Reader hand-off** (off by default) copies each ebook imported from a
download to a watch folder, for example the ingest folder of Calibre-Web-Automated:

- Always a copy: the library file is never moved or changed. Audiobooks are skipped, and so
  are disk scans and manual imports of files without a download.
- **Author subfolder** (off) puts each copy under a folder named after the author.
- **Convert EPUB to KEPUB** (off) converts the copy for Kobo with
  [kepubify](https://pgaskin.net/kepubify/) when it is on PATH in the container (not bundled).
  Without it, or if a conversion fails, the plain EPUB is copied and System > Status warns.
- Files are written under a hidden `.part` name and renamed when complete, so a watcher never
  picks up half a file. **Test** checks that the folder exists and is writable.
- **API**: `GET|PUT /api/v1/config/talearrreaderhandoff`, `POST .../test`.

Storage: `talearr.readerhandoff.*` rows in the `Config` table. Not a connection type, so a
rollback to Chaptarr only stops the copies.

## MyAnonamouse

The native MyAnonaMouse indexer (Settings > Indexers) gets these TaleArr additions:

- **Session health.** A `mam_id` that MAM rejects (search, download or account refresh) shows
  "MAM session expired: paste a new mam_id" in System > Status until a new one is saved. When
  MAM sends a new `mam_id` in Set-Cookie, TaleArr saves it to the indexer settings.
- **Update Dynamic Seedbox IP** (off) tells MAM the torrent client's current IP on startup and
  every **Dynamic Seedbox Interval** (30 minutes) via `t.myanonamouse.net/json/dynamicSeedbox.php`.
  The call must leave from the torrent client's IP: pick a proxy that exits through the
  client's VPN as **Seedbox Proxy** (for example the Privoxy in a qBittorrent VPN container),
  or give the indexer itself that proxy. **Seedbox mam_id** is an optional separate session.
  Refusals (invalid session, IP/ASN mismatch, session type not allowed) appear in System >
  Status; "No change" and "Last change too recent" are only logged. The task is listed in
  System > Tasks and can be run by hand.
- **Prefer Freeleech** (on) ranks freeleech, personal freeleech and VIP freeleech releases
  above otherwise equal ones (after the quality profile). **Freeleech Only** (off) rejects the
  rest. Both also apply to MAM results that come through Prowlarr, using the native indexer's
  settings.
- **Unsatisfied limit warning.** System > Status warns at 80% of MAM's unsatisfied-torrent
  limit, and when automatic grabs are paused by the safety reserve.

Storage: new keys in the indexer's own settings JSON (stock Chaptarr ignores them) and one
scheduled task row that Chaptarr drops on start.

## Drop-in compatibility with Chaptarr

TaleArr is built so it can start on an existing Chaptarr config folder and database
unchanged, and so Chaptarr can start on it again if you roll back.

Kept exactly as upstream:

- `config.xml`, `chaptarr.db`, `logs.db`, `cache.db` and the database schema and migrations
- the API routes and response fields (`/api/v1/...`; `appName` still reports `Chaptarr`
  so existing API clients keep working)
- environment variable config (`Chaptarr__Postgres__Host`, `Chaptarr__Server__Port`, ...)
- custom script environment variables (`Chaptarr_EventType`, ...)
- C# namespaces and assembly names (`NzbDrone.*`, `Chaptarr.*`), so upstream merges stay easy
- the metadata server (`api2.chaptarr.com`) and the user agent sent to it

Changed for users: the product name in the UI, page titles, login page, PWA manifest,
notifications, startup logs, the logo and icons, and the Docker image name. New native
installs use a `TaleArr` data folder; an existing `Chaptarr` data folder is reused.

## Grab policy

Settings > Indexers > Grab policy controls what TaleArr grabs and what it does when a grab
goes wrong:

| Setting | Default | What it does |
|---|---|---|
| Abridged audiobooks | Unabridged only | Audiobook releases marked "abridged" are skipped (shown as a soft filter in interactive search, so you can still grab one on purpose). They are allowed when the wanted edition is itself abridged, judged from the edition title or info, or from its runtime being under 60% of the longest edition in the same language. "Abridged only" does the opposite: releases marked unabridged are skipped, unmarked ones are allowed only when the monitored edition is abridged, abridged releases rank first, and before searching a book without files TaleArr monitors its best abridged edition (see [docs/TALEARR_RECORDINGS.md](docs/TALEARR_RECORDINGS.md#per-book-abridged-policy)). "Prefer unabridged" allows them but always ranks them below unabridged or unmarked releases. "Any" turns this off. A book can have its own value (from AllSeerr), which wins over this one. |
| Allow bundles | Off | Series bundles, omnibuses, box sets, collections, "complete novels", "N books" sets and explicit title lists ("Great Innovators (Benjamin Franklin; Einstein; Steve Jobs)") are skipped for single-book searches. A wanted book that is itself an omnibus still matches bundle releases. |
| Fail rejected imports | On | When an import is rejected because the release is the wrong book, its files are in the wrong language, its archives are unusable, or it holds no audio or ebook files (zip, rar and 7z are unpacked first, see [docs/TALEARR_IMPORT.md](docs/TALEARR_IMPORT.md)), the download is marked failed. That blocklists the release and starts the automatic redownload. Archive formats TaleArr does not unpack, same-title matches (usually duplicate metadata), transient errors and explicit interactive grabs stay in the queue as before. |
| Automatic retry limit | 3 | Failed downloads per book within 7 days before automatic searching stops. |
| Retry failed grabs | On | When a release cannot be sent to the download client because the indexer (or Prowlarr) answered 429, a 5xx error or timed out, the next approved release of the same search is tried (other releases of an indexer that just said 429 are left for later), and if nothing could be sent the book is searched again after 2, 10 and 30 minutes. The wait honours Retry-After and the indexer back-off shown under System > Status, is capped at an hour, and the number of re-searches is the automatic retry limit. Only automatic and user-started searches are retried; RSS sync and interactive grabs behave as before. Each step is logged with a "TaleArr:" prefix. |

Settings > Indexers > **Format policy** (see [docs/TALEARR_FORMAT_POLICY.md](docs/TALEARR_FORMAT_POLICY.md))
picks the format grabbed for a wanted book, for the library and per author: Both (default,
upstream behaviour), Prefer audiobook (accept the ebook after the audiobook has been missing for
24 hours, still upgrade to audio later), Audiobook only or Ebook only. Monitoring is unchanged;
the policy is a soft filter on grabs, stored as `talearr.formatpolicy.*` rows through
`/api/v1/config/talearrformatpolicy`.

Automatic redownload itself is the existing Download Clients > Failed Download Handling
setting, which is on by default. A replacement search covers every indexer with automatic
search enabled, Usenet and torrent. Grabs you made yourself (interactive or a manual search)
are retried as a manual search, so an unmonitored book or a delay profile does not block the
replacement. Each step is appended to the Download Failed entry in History, for example
"TaleArr: release blocklisted, searching all indexers for a replacement (attempt 1 of 3)".

Storage: these five settings live in the existing `Config` key/value table as
`talearr.grabpolicy.*` rows, read and written only by TaleArr through the new
`/api/v1/config/talearrgrabpolicy` route. Per-book abridged values live in `talearr.db`
(`/api/v1/talearr/grabpolicy/book/{bookId}`). There is no schema change, no existing route or
field changes, and `config.xml` is not touched. Chaptarr ignores the rows, so a rollback
runs Chaptarr's own behaviour on the same database.

## Versioning

TaleArr has its own version, set in `src/Talearr.props` (currently `0.16.0`). System >
Status shows it as the version, with a "Based on Chaptarr x.y.z" line underneath. The
assembly version stays the upstream Chaptarr version (from `package.json` and
`src/Directory.Build.props`) on purpose, because update checks, backups and metadata
requests key on it.

`build-tools/talearr_versions.sh` prints both.

## Hardlinks, seeding and manual import

**Torrent imports never move the client's files.** With Settings > Media Management >
Seeding-Safe Torrent Imports (advanced, on by default) every torrent import is a copy, and a
copy is a hardlink when "Use Hardlinks instead of Copy" is on (also the default). The torrent
keeps seeding from its own folder, the library gets its own link to the same data, and no disk
space is used twice. Upstream moved a torrent once it reached its seed goal, and honoured Move
in manual import even while a torrent was still seeding; both are now a hardlink. Usenet
downloads are still moved, a torrent blackhole still follows its own "read only" setting, and
the client still removes finished torrents and their data the usual way (the library copy
stays). Turn the setting off for the upstream behaviour. TaleArr's own steps never touch the
seeding data either: archives are unpacked into a separate work folder and only that folder is
deleted, an M4B merge writes a new file and only removes the library's links, bundle leftovers
are only deleted for usenet, and tag or date writes break a library hardlink first. Moving a
still-hardlinked file to the recycle bin no longer changes the torrent copy's timestamp.

**Post-import categories.** qBittorrent, Deluge, rTorrent, uTorrent and Flood can move an
imported torrent to a category or label (Audiobook / Ebook Post-Import Category, advanced
settings of the download client). Torrents in that category are kept by TaleArr and never
removed.

**Hardlinks need one filesystem.** A hardlink only works when the download folder and the
library are on the same filesystem and reached through the same container volume. Map one
parent folder that holds both (for example `-v /mnt/user/data:/data` with
`/data/torrents` and `/data/media/audiobooks`) instead of separate `/downloads` and
`/audiobooks` volumes. On Unraid, `/mnt/user` (the FUSE user share view) only hardlinks within
one share, so the downloads and the library must be folders of the same share. When a
hardlink is not possible the file is copied, and after 3 such copies in a row System > Status
shows a warning naming the two paths (cleared by the next successful hardlink; the count is in
memory and starts again after a restart).

**Manual import (Wanted > Missing > Manual Import, or Manual Import on a queue item).** Point
it at any folder, for example an old library: files are scanned and matched with the normal
import matching, then shown in a review table where every row can be re-matched (author, book,
edition). TaleArr adds:

- A match confidence per row (High, Medium, Low; hover for why), from the match's own evidence
  and whether the matched title appears in the file's tags, file name or folder. A Low row is
  the one to check before importing. API: `talearrMatchConfidence` and `talearrMatchNotes` on
  each `GET /api/v1/manualimport` item.
- A language warning when the file's own language is not the wanted one (the completed
  download check, as a warning only).
- Three import modes: Move, Hardlink/Copy (hardlink when the setting is on) and Copy (no
  hardlinks). The API takes `importMode` `copy` plus `talearrTransfer: "copy"` (or
  `"hardlink"`) on the `ManualImport` and `DownloadedBooksScan` commands.
- `foreignEditionId` is accepted when `editionId` is missing, and the error names the field.

Authors that are not in the library yet show as "Will add from metadata" and are added on
import, using the default root folder for the media type. Quick Import imports a folder
without the review table and adds missing authors the same way.

## Docker

Images are published to GitHub Container Registry by `.github/workflows/talearr-docker.yml`:

| Tag | Built from |
|---|---|
| `ghcr.io/dancue44/talearr:develop` | every push to `develop` |
| `ghcr.io/dancue44/talearr:latest` | every push to `main` |
| `ghcr.io/dancue44/talearr:0.16.0` | `main` (TaleArr version) and `v0.16.0` git tags |
| `ghcr.io/dancue44/talearr:<branch>` | pushes to `feat/**` and `fix/**` branches (amd64 only) |
| `ghcr.io/dancue44/talearr:sha-<commit>` | every build |

`develop`, `main` and version tags are multi-arch (`linux/amd64`, `linux/arm64`). The
repository is private, so the package is private too: run `docker login ghcr.io` with a
GitHub token that has `read:packages` on the machine that pulls it.

```bash
docker run -d \
  --name talearr \
  -p 8789:8789 \
  -e PUID=99 \
  -e PGID=100 \
  -e UMASK=002 \
  -v /path/to/config:/config \
  -v /path/to/audiobooks:/audiobooks \
  -v /path/to/ebooks:/ebooks \
  -v /path/to/downloads:/downloads \
  --restart unless-stopped \
  ghcr.io/dancue44/talearr:develop
```

The container starts as root only long enough to create the `PUID`/`PGID` user and fix
ownership of `/config`, then drops to that user with `gosu`. If `PUID`/`PGID` are not set
it defaults to `99:100`. `UMASK` is optional.

To move an existing Chaptarr container to TaleArr (and back), see
[docs/TALEARR_UNRAID_SWAP.md](docs/TALEARR_UNRAID_SWAP.md).

## Building from source

Requires the .NET 10 SDK, Node.js 20 (22 also works) and Yarn 1.

```bash
yarn install --frozen-lockfile
yarn build                      # frontend -> _output/UI
dotnet build src/Chaptarr.NoTests.sln -c Release
dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj -c Release

# run from a publish folder
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish
cp -r _output/UI _output/publish/UI
dotnet _output/publish/Chaptarr.dll -nobrowser -data=/tmp/talearr-data
```

The entry assembly keeps its upstream name (`Chaptarr.dll`). Install FFmpeg when running
natively; the Docker image bundles it along with m4b-tool and mp4v2.

`python3 build-tools/talearr_brand_check.py` checks that the rebrand is intact and that the
drop-in identifiers are unchanged. CI runs it on every push.

## Metadata fallback

Settings > Metadata > **Metadata fallback** keeps search and add working while the Chaptarr
metadata server is down. Values: Off, When the metadata server is down (default), and the same
with search results labelled "Open Library". It uses the public Open Library API (no key, one
request per second, a `TaleArr/<version>` user agent, responses cached for 6 hours).

- Search falls back when the metadata server is down or the search provider fails with a
  server or network error. A missing Hardcover token is not treated as an outage.
- Add falls back when fetching the author from the metadata server fails that way. A hc:/gr:
  author keeps that id (Open Library is matched by exact name only), an Open Library author
  gets `ol:` ids plus its Goodreads and Amazon ids from Open Library. Books get `ol:` work and
  edition ids in the existing Open Library columns. An author already in the library is never
  changed by fallback data.
- Refresh never uses the fallback: during an outage it is skipped and nothing changes. After
  recovery the normal refresh updates the author from the metadata server, and books added
  from Open Library are matched to the server's books by ISBN-13, then by exact title (same
  media type, one candidate only), so the existing rows are updated in place, not duplicated.
- Limits: Open Library has no audiobook editions, narrators or series, so fallback books get
  one print edition (audiobook rows search by title and author as usual). An author with no
  Goodreads or Amazon id on Open Library stays on Open Library data until it is re-added. A
  book that matches no server book, or more than one, is left to the normal refresh rules.
- System > Status shows a notice while the fallback is active and for 24 hours after it was
  last used. The setting and live status are at `/api/v1/config/talearrmetadatafallback`
  (stored as a `talearr.metadatafallback.mode` row; no schema change).

## Keeping up with Chaptarr

See [docs/TALEARR_UPSTREAM_SYNC.md](docs/TALEARR_UPSTREAM_SYNC.md).

## Privacy

Like Chaptarr, TaleArr uses `api2.chaptarr.com` for metadata and matching, and the update
check contacts Chaptarr's update service. See the upstream README for what those requests
contain.
While the metadata server is unavailable, the metadata fallback (on by default) sends search
terms and author names to `openlibrary.org`; turn it off under Settings > Metadata.

## License and credits

TaleArr is free software under the [GNU GPL v3](LICENSE). See [COPYRIGHT.md](COPYRIGHT.md).

TaleArr is based on [Chaptarr](https://github.com/Chaptarr/chaptarr) by the Chaptarr
contributors, which is based on [Readarr](https://github.com/Readarr/Readarr) and the
shared codebase of the Servarr team ([Sonarr](https://github.com/Sonarr/Sonarr),
[Radarr](https://github.com/Radarr/Radarr), [Lidarr](https://github.com/Lidarr/Lidarr),
[Prowlarr](https://github.com/Prowlarr/Prowlarr)). Thank you to all of them.

TaleArr is not affiliated with or supported by the Chaptarr project or the Servarr team.
Please do not send TaleArr support requests to them.

- Copyright 2026 TaleArr contributors
- Portions copyright 2026 Chaptarr contributors
- Portions copyright 2010-2026 the Servarr team and contributors
