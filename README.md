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

TaleArr has its own version, set in `src/Talearr.props` (currently `0.15.3`). System >
Status shows it as the version, with a "Based on Chaptarr x.y.z" line underneath. The
assembly version stays the upstream Chaptarr version (from `package.json` and
`src/Directory.Build.props`) on purpose, because update checks, backups and metadata
requests key on it.

`build-tools/talearr_versions.sh` prints both.

## Docker

Images are published to GitHub Container Registry by `.github/workflows/talearr-docker.yml`:

| Tag | Built from |
|---|---|
| `ghcr.io/dancue44/talearr:develop` | every push to `develop` |
| `ghcr.io/dancue44/talearr:latest` | every push to `main` |
| `ghcr.io/dancue44/talearr:0.15.3` | `main` (TaleArr version) and `v0.15.3` git tags |
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

## Keeping up with Chaptarr

See [docs/TALEARR_UPSTREAM_SYNC.md](docs/TALEARR_UPSTREAM_SYNC.md).

## Privacy

Like Chaptarr, TaleArr uses `api2.chaptarr.com` for metadata and matching, and the update
check contacts Chaptarr's update service. See the upstream README for what those requests
contain.

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
