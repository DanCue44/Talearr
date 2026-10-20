<div align="center">

<img src="Logo/talearr.png" width="160" alt="Talearr logo">

# Talearr

An ebook and audiobook collection manager, based on Chaptarr.

</div>

> Talearr is a private fork and early work in progress.

## What is Talearr?

Talearr is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself a
fork of [Readarr](https://github.com/Readarr/Readarr) from the
[Servarr](https://wiki.servarr.com/) project family. It manages audiobook and ebook
libraries in one instance: narrator and edition aware metadata, separate audiobook and
ebook root folders, series handling, quality profiles and upgrades, and the usual
download client and indexer integrations.

Talearr tracks Chaptarr closely. The differences today are the name and icon, a separate
Talearr version, a "Works best with" block in System > Status (Allseerr, NoBookEnds), a
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

Talearr behavior changes so far (all without schema changes, new settings or `config.xml` edits):

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
  "Only this book" and "None"; saved browser defaults from before Talearr are switched once.
  The new author also keeps only that book instead of its whole catalog (see
  [Tracked authors](#tracked-authors)).
- **Merge duplicates (Library > Duplicates).** Lists authors with the same name and book rows
  of one author with the same title and media type, and merges them: the kept row keeps its
  ids, everything else (editions, book files, history, queue and blocklist matches, series and
  narrator links) moves onto it, monitoring is combined, and the duplicate rows are removed in
  one transaction. Files on disk are never moved or deleted. Ebook and audiobook rows of one
  book are siblings and are never merged. A preview shows exactly what moves. API:
  `GET /api/v1/talearr/merge/candidates`, `POST /api/v1/talearr/merge/authors|books`
  (`targetId`, `sourceIds`, `dryRun`, `force`). Merges stay merged: the provider ids of the
  removed rows are kept in `talearr.db` pointing at the kept row, so a later refresh or add that
  still sees an old id updates the kept row instead of adding the duplicate again.
- **Large libraries.** Fewer full-library loads: the book index loads series links once with a
  single scan, Wanted, Cutoff Unmet, the calendar and author pages read statistics for their
  own authors only, and book routes by `titleSlug` (Allseerr links) look the slug up instead
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
  When the guard rejects an upgrade download (the size estimate looked better, the real files
  were not), the download is failed without a new search, which blocklists that release, and
  automatic searches do not grab upgrades for that book for 7 days (an "Upgrade paused" soft
  filter; interactive search can still grab). Explicit user grabs stay in the queue. The pause
  is kept in `talearr.db`.
- **Per-book abridged policy.** A book can have its own Abridged audiobooks value (unabridged
  only, abridged only, prefer unabridged, any), set by Allseerr through
  `GET|PUT /api/v1/talearr/grabpolicy/book/{bookId}`. An abridged only book without files
  monitors its best abridged edition before it is searched; when none is known, its history says
  so. Additional recordings keep their own behaviour.
- **API** for Allseerr and NoBookEnds: `GET|POST /api/v1/talearr/recordings`,
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

## Tracked authors

See [docs/TALEARR_TRACKED_AUTHORS.md](docs/TALEARR_TRACKED_AUTHORS.md).

- **Only my books.** An author added for a book request (add-book modal, `POST /api/v1/book`,
  Allseerr, wishlists, MAM filler imports) keeps only the books you added, requested, monitor or
  have files for. Refresh updates them and adds none of the rest of the catalog. Adding an author
  still follows all of its books.
- **Requested books pass the metadata profile.** An explicitly requested book is added even
  when a profile rule (omnibus, parts and sets, ignored terms, popularity, ...) would skip it.
  Import list exclusions still apply.
- **Switch any time** on the author page: **Follow all books** brings the catalog in, **Only my
  books** (confirmed first) removes unmonitored, fileless catalog rows. Files are never touched.
- **API**: `GET|PUT /api/v1/talearr/authors/{id}/catalog` with `{"mode": "full" | "tracked"}`;
  the author resource carries `talearrCatalogMode`.

Storage: one `talearr.authorcatalog.*` row per "Only my books" author in the `Config` table. A
rollback to Chaptarr treats every author as followed in full again.

## Author page groups

In the author page **All** view, the author's main works come first and collections, study
guides and graded readers, adaptations, other listings of the same book and other languages are
in a collapsed **Other listings** section. Monitored books and books with files are always main
works; nothing is hidden or removed. See [docs/TALEARR_AUTHOR_PAGE.md](docs/TALEARR_AUTHOR_PAGE.md).

## Book tags and genre filters

- **Book tags.** Tag single books with the same tags as authors (Settings > Tags): in the book's
  Edit dialog or in bulk with Set Book Tags in the book index editor. Shown on the book page and
  in the index Tags column.
- **Custom filters.** The book index adds Book Tags, Author Tags (for the book's format),
  Format, Has File and Any Recording Narrator to the existing filters (genres, narrator,
  monitored). Wanted > Missing and Cutoff Unmet get custom filters on book tags, author tags,
  genres, narrator and monitored.
- **Metadata profile genres and content types.** Allowed and blocked genre lists, and Skip Short
  Stories, Skip Novellas, Skip Anthologies and Skip Graphic Novels, applied on author add and
  refresh like the other profile rules (manual adds and books with files are kept; books the
  metadata has no genres for always pass). All off by default.
- **API**: `GET /api/v1/talearr/booktags`, `GET|PUT .../booktags/{bookId}`,
  `PUT .../booktags/editor`, `GET /api/v1/talearr/genres`; `talearrContentFilter` on
  `/api/v1/metadataprofile`; `talearrFilter` (JSON) on `/api/v1/wanted/missing` and `.../cutoff`.

Storage: book tag links in the `BookTags` table of `talearr.db` (lost only if that file is
recreated), one `talearr.metadataprofile.<id>` row per profile in the `Config` table.

## Migrating from Readarr, Bookshelf or LazyLibrarian

See [docs/TALEARR_MIGRATION.md](docs/TALEARR_MIGRATION.md).

- **System > Migrate** reads a running Readarr or Bookshelf over its API (URL and API key), a
  `readarr.db` or a Readarr backup zip (uploaded or a path on the server), or LazyLibrarian over
  its API (`getIndex`, `getAllBooks`).
- **Preview** is a dry run: tags, root folders, quality and metadata profiles (matched by name),
  download clients and indexers, then every monitored book matched to a metadata book
  (Goodreads id, ASIN, ISBN, then title and author). It lists what will be created, what is
  already there, what did not match and what conflicts. **Apply** carries out that plan:
  authors are queued with only the source's monitored books monitored, and files on disk are
  matched by the scan that runs when an author or root folder is added.
- Imported clients and indexers start **disabled** unless asked, so the source and Talearr do
  not both grab. Prowlarr-synced indexers are skipped. The source is only read, never changed.
- **API**: `GET /api/v1/talearr/migration`, `POST .../preview`, `POST .../upload`,
  `POST .../apply`, `DELETE` (discard the preview).

Storage: the plan and the last report are JSON files in `talearr-migration/` in the config
folder (the plan, which holds the source's provider settings, is removed after apply). The API
key is kept in memory only. No schema change, no new provider type.

## Audiobookshelf

See [docs/TALEARR_AUDIOBOOKSHELF.md](docs/TALEARR_AUDIOBOOKSHELF.md).

- **Library awareness.** The existing Settings > Connect > Audiobookshelf connection gets
  Sync Library (on): Talearr reads the mapped libraries every hour (GET only) and matches the
  items to books by path, ASIN, ISBN, then title and author.
- **No duplicate grabs.** Skip Books Already in Audiobookshelf (on) skips releases for books
  ABS already has and Talearr has no files for, shown as "In Audiobookshelf" in interactive
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
Talearr adds none.

## Soulseek and download clients

See [docs/TALEARR_SOULSEEK.md](docs/TALEARR_SOULSEEK.md) and
[docs/TALEARR_DOWNLOAD_CLIENTS.md](docs/TALEARR_DOWNLOAD_CLIENTS.md).

- **Soulseek through slskd** (Settings > Download Clients > Soulseek, off by default): Search
  Soulseek on a book page, judged by the normal matching, language, bundle and quality rules;
  Grab downloads the whole folder through your slskd and imports it for that book. Etiquette
  settings (free upload slot only, maximum peer queue, one download per peer) are on by default.
  Manual only, no automatic search.
- **Debrid** works through qBittorrent-compatible proxies (rdt-client, Decypharr) added as a
  qBittorrent client; Talearr reads their API versions and empty content paths correctly.
- **API**: `/api/v1/config/talearrsoulseek`, `/api/v1/talearr/soulseek` (search, grab, grabs).

Storage: `talearr.soulseek.*` rows in the `Config` table. Not a download client or indexer
provider and no new download protocol value, so a rollback deletes nothing and History,
Blocklist and pending releases only ever hold values Chaptarr knows.

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

## Ebook delivery

Settings > Conversion, below the reader hand-off. Both parts are off by default.

**OPDS catalog.** A read-only OPDS 1.2 feed at `http://<host>:8789/opds` (plus your URL base)
for KOReader, Panels, Thorium, Moon+ Reader and other OPDS readers:

- Recently added, Authors, Series and All books (ebooks with a file only), paged, plus search
  by title, author or series through an OpenSearch description.
- Downloads stream the library file (EPUB, AZW3, MOBI, PDF and the rest). **Offer KEPUB
  downloads** (off) adds a KEPUB link next to each EPUB, converted on download with kepubify
  when it is installed; otherwise the plain EPUB is sent. The library file is never changed.
- Sign-in: HTTP Basic with your Talearr user (Forms or Basic authentication, same lockout as the
  login page), or any user name with the API key as the password (works with Plex, OIDC or
  External authentication too), or `?apikey=` on the URL. From a local address no sign-in is
  needed when Authentication Required is "Disabled for Local Addresses", like the web UI. While
  the feed is off, `/opds` answers 404.

**Send to Kindle.** Emails an ebook to one or more Send-to-Kindle addresses, from the **Send to
Kindle** button on an ebook's page, and optionally after each ebook imported from a download.

- Mail goes through the SMTP settings in the section, or through an existing **Email
  connection** (its server, login and From address are reused). **Test** signs in to the server
  and sends nothing. The password is never logged or returned by the API.
- The best file Amazon accepts is sent: EPUB, then PDF, DOCX, DOC, RTF, TXT, HTML. MOBI and AZW3
  are no longer accepted by Amazon, so those books are skipped with a message. Files over 50 MB
  are refused with their size. The From address must be on the Approved Personal Document
  E-mail List in your Amazon account.

**Kobo.** There is no Kobo store sync emulation. Use KOReader on the Kobo with the OPDS feed, or
the reader hand-off with Convert to KEPUB into Calibre-Web-Automated (which has Kobo sync).

- **API**: `GET|PUT /api/v1/config/talearropds`, `GET|PUT /api/v1/config/talearrkindle`,
  `POST .../test`, `GET .../emailconnections`, `GET /api/v1/talearr/kindle/book/{bookId}`,
  `POST /api/v1/talearr/kindle/send/{bookId}`.

Storage: `talearr.opds.*` and `talearr.kindle.*` rows in the `Config` table. No connection type
and no schema change, so a rollback to Chaptarr removes the feed and the button.

## MyAnonamouse

The native MyAnonaMouse indexer (Settings > Indexers) gets these Talearr additions:

- **Session health.** A `mam_id` that MAM rejects (search, download or account refresh) shows
  "MAM session expired: paste a new mam_id" in System > Status until a new one is saved. When
  MAM sends a new `mam_id` in Set-Cookie, Talearr saves it to the indexer settings.
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
- **Freeleech wedge balance.** The hourly account refresh also reads your wedge count and
  bonus points (`wedges`, `seedbonus` in `jsonLoad.php`, when MAM sends them). The indexer Test
  shows them, and System > Status shows a notice when **Freeleech Wedges** is Prefer Wedge and
  no wedges are left (grabs then download without one). The refresh also reads VIP expiry
  (`vip_until`), ratio (`ratio`, or `uploaded_bytes`/`downloaded_bytes`) and a seed-only flag
  when MAM sends them. Nothing is bought unless you turn on bonus points auto-spend (below).
- **Bonus points auto-spend** (every rule off by default; indexer settings, Advanced). Uses
  MAM's Bonus Points Store API (`json/bonusBuy.php`), which is on MAM's list of allowed
  automation, with the `mam_id` session. The hourly **Spend My Anona Mouse Bonus Points** task
  (System > Tasks) checks the stored account status (refreshing it first when it is an hour
  old) and buys at most one thing per run:
  1. **Ratio Safety Net** (off): when the ratio is below **Minimum Ratio** (1.2), or MAM made
     the account Mouse or seed-only, buys **Upload Credit to Buy** (50 GiB, the API minimum;
     500 points per GiB, so 25,000 points) with `spendtype=upload&amount=50`.
  2. **Auto-Keep VIP** (off): for Power User or VIP accounts only (the classes MAM's store
     sells VIP to), when VIP is not active or has fewer than **VIP Renew Below** (14) days left,
     buys `spendtype=VIP&duration=max` (fills to MAM's 90-day limit, at least 7 days). The
     cost is estimated at 5,000 points per 4 weeks of missing days. A VIP account whose expiry
     MAM does not report is left alone.
  3. **Auto-Buy Wedges** (off): while you have fewer than **Minimum Wedges** (2), buys one
     wedge (50,000 points) with `spendtype=wedges`.

  Every rule keeps at least **Bonus Points Reserve** (10,000) points. **Bonus Purchases per
  Day** (2) caps real purchases in any 24 hours, and after MAM refuses a purchase that kind is
  not tried again for 24 hours (no retry storms). Every attempt is written to `talearr.db`
  before it is sent (nothing is bought if that write fails), and the account status is
  refreshed right after. MAM documents no reply format, so only an explicit `success` flag
  counts as bought; anything else is a failure, logged with the raw reply (truncated).
  **Bonus Spend Dry Run** (off) only logs and lists what would be bought. Activity > Seeding
  shows **Recent bonus purchases** under the MAM summary; each real purchase or failure is
  sent through On Health Issue + Include Health Warnings. System > Status warns when the
  account is Mouse or seed-only: "Buy upload credit in the Bonus Points Store (API minimum
  50 GB = 25,000 points)".

  MAM policy: automating anything not on MAM's allowed list can cost you the account.
  Auto-spend only calls two allowed endpoints (Bonus Points Store, Load User Data) and can
  never send the `gift` or `sendWedge` spend types (MAM
  forbids automating them; the code has no way to build them) or touch the Millionaire's
  Vault. Cheese purchases are not in the API.
- **Seed-only and ratio guard** (always on). A new account starts with 0 upload, so a few
  normal grabs can push the ratio under 1.0, and MAM then demotes it to Mouse and makes it seed
  only. Talearr blocks every MAM grab (freeleech too, native and Prowlarr MAM results) while the
  account is Mouse or reports a seed-only / download-disabled flag, with the System > Status
  error "MAM account is seed-only: buy upload credit in the Bonus Points Store". A
  non-freeleech grab only goes ahead when uploaded / (downloaded + release size) stays at or
  above **Minimum Ratio After Grab** (2.0, the Power User line; Advanced). Freeleech, personal
  freeleech, VIP freeleech for VIP members, and grabs that will request a wedge while wedges
  are left are exempt. With no account status, or one older than 3 hours, only freeleech MAM
  releases are grabbed. The check runs in the decision engine and again at grab time.
- **Account refresh by hand.** Running **Refresh My Anona Mouse Account Status** in System >
  Tasks always calls MAM now (it used to share the one-hour throttle of the health-cycle
  refresh, so a manual run right after another refresh did nothing and a class change stayed
  stale for up to an hour).
- **MAM timing is yours to choose** (indexer settings, Advanced). Every frequency and
  aggressiveness value is a setting whose help text gives a gentle suggestion: **Account
  Status Refresh** (60 minutes; was fixed), **Dynamic Seedbox Interval** (1 to 1440 minutes,
  suggested 60), **Filler Run Interval** (6 hours), **Filler Max Grabs Per Run** (5, up to 1000),
  **Filler Search Pages** (3) and **Filler Preferred Search Pages** (2), **Filler Pause After
  Error** (12 hours), **Bonus Purchases per Day** (2, up to 100) and **Bonus Retry After
  Refusal** (24 hours). Validation only blocks values that would break (zero or negative
  intervals, no grabs, typo-sized numbers); lower or more is allowed but sends MAM more requests
  or uses more slots. The account status and seedbox tasks tick as often as the shortest setting
  needs, and with a refresh longer than an hour the 2 and 3 hour status freshness windows grow
  by the same amount so grabs do not pause between refreshes. Existing settings keep their old
  values.
- **Freeleech seeding filler** (off; indexer settings, section Freeleech Seeding Filler). MAM
  counts a torrent as satisfied after 72 hours of seeding, and only satisfied torrents earn
  bonus points. The filler uses free unsatisfied slots for popular freeleech torrents so more
  torrents reach that point sooner. The **MAM Freeleech Filler** task (System > Tasks, can be
  run by hand) checks hourly and runs every **Filler Run Interval** (6 hours):
  1. Uses the stored account status (refreshed through `jsonLoad.php` when over an hour old).
     It stops when the status is unknown or older than 3 hours, when the account is seed only,
     when any MAM torrent on Activity > Seeding is at risk, and for 12 hours after any MAM
     error.
  2. Free slots = unsatisfied limit - **Filler Slot Margin** (2) - unsatisfied - pending slot
     reservations, at most **Filler Max Grabs Per Run** (5). The unsatisfied-slot protection
     still applies: every grab reserves a slot through it, so its Safety Reserve and User
     Buffer win when they are stricter.
  3. Searches MAM's JSON search for freeleech (`searchType=fl`) English audiobooks and ebooks
     (up to 3 pages of 100), then keeps only torrents MAM itself flags as freeleech or
     personal freeleech (never VIP-only torrents unless the account is VIP), up to **Filler
     Max Size** (2048 MB), with at least **Filler Min Leechers** (1) and at most **Filler Max
     Seeders** (30), and not grabbed before (by MAM torrent id), reserved, or already in the
     client. Ranking: audiobooks first (**Filler Prefer Audiobooks**, on), then most leechers,
     fewest seeders, smallest.
  4. **Filler Dry Run** (on) only logs the picks and shows them on Activity > Seeding under
     **Filler preview**. With it off, each pick is downloaded with `download.php` (never the
     `fl` wedge parameter) and added straight to qBittorrent in **Filler Category**
     (`mam-seed`) with a per-torrent seeding time of **Filler Seed Time** (30 days). No grab
     history, queue entry or import is created; **Filler Import Into Library** (off) puts them
     in the client's normal Talearr category instead. **Filler Remove After Seed Time** (on)
     removes them with their files once they have seeded that long (never under 72 hours).
  5. **Taste preferences.** Picks that match a preferred category or author go first (then the
     ranking above); the rest only fill the remaining slots, or none with **Filler Only Grab
     Preferred** (off). **Filler Preferred Categories** (`True Crime`) and **Filler Excluded
     Categories** (`Religion, Spirituality, Christian, Pol/Soc/Relig`) are comma separated MAM
     category names matched by whole words in any case against MAM's `catname` (so True Crime
     covers Audiobooks - True Crime and Ebooks - True Crime); an exclusion always wins.
     Pol/Soc/Relig is MAM's combined politics, society and religion category. Authors count
     when they are in the library (**Filler Prefer Authors in My Library**, on) or listed in
     **Filler Preferred Authors** (empty); names ignore case, accents, dots and spacing. One
     extra freeleech search per run (up to 2 pages) is limited to the preferred categories'
     MAM ids (`cat`), so they are found even when the general pages miss them. Filler preview
     shows a **Preferred** badge with the reason.
  6. **Filler Import Preferred Picks** (on) puts preferred picks into the library while they
     keep seeding in the filler category: once the download has finished, the book is matched
     in the metadata and added like a wishlist book (author added with only that book
     monitored for that format, no search; an author already in the library just gets that
     book monitored), then the finished download is imported with copy mode (hardlink or copy
     per Use Hardlinks, never a move), so Audiobookshelf picks it up. A book that already has
     files, has no metadata match or is on the import list exclusions stays seed-only. Other
     picks stay seed-only. The torrent stays a filler row on Activity > Seeding and counts as
     before for slots and the seed time. Ignored when **Filler Import Into Library** is on.

  Filler torrents show on Activity > Seeding with indexer MyAnonamouse, a **Filler** label
  and the 72-hour requirement, so the hit-and-run risk check covers them. MAM policy: the
  filler only uses MAM's allowed automation endpoints (Torrent Search JSON, Download Torrent,
  Load User Data), through the indexer's proxy and session handling, and never spends wedges
  or bonus points.
- **Power User progress.** Activity > Seeding shows how far the account is from MAM's
  automatic Power User promotion (member for 4 weeks, over 25 GiB uploaded, ratio 2.0) when
  the account refresh reports the join date and upload totals. Unknown values stay blank.
- **Seeding status.** Activity > Seeding lists what the torrent clients seed in Talearr's
  categories (post-import categories included): book, format, indexer, state, seeded time
  against the requirement (72 hours for MAM; the indexer Seed Time is the goal), upload and
  ratio, plus the stored MAM account status. A private-tracker torrent that has not met its
  requirement and is paused, queued, errored, has a tracker error, or was removed from the
  client counts as at risk, and System > Status shows a warning (sent with On Health Issue +
  Include Health Warnings). qBittorrent reports full details; other clients are best effort.
  Read-only: nothing is changed in the client and MAM is not called. API:
  `GET /api/v1/talearr/seeding` (client reads cached for 30 seconds).

Storage: new keys in the indexer's own settings JSON (stock Chaptarr ignores them), scheduled
task rows that Chaptarr drops on start, and the `MamBonusPurchases` table in `talearr.db`
(schema 7; losing it only resets the daily cap and back-off). API: the
`GET /api/v1/talearr/seeding` `mam` block gains `vipUntil`, `ratio`, `restricted`,
`bonusAutoSpend`, `bonusDryRun` and `bonusPurchases`. The filler adds the
`TalearrFillerTorrents` and `TalearrFillerState` tables (schema 8; losing them only forgets
which client torrents the filler added and its last run; preferred picks' library state is
kept there as `pick:<id>` rows, no schema change), and the seeding API gains `isFiller` per
torrent, a `filler` block (last run, result, back-off, preview with `preferred`,
`preferredReason`, `category`, `author`, `libraryState`, `libraryMessage`) and, in `mam`,
`memberSince`, `uploadedBytes`, `downloadedBytes`, `daysUntilEligible`,
`uploadedRemainingBytes` and `ratioOk`.

## Drop-in compatibility with Chaptarr

Talearr is built so it can start on an existing Chaptarr config folder and database
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
installs use a `Talearr` data folder; an existing `Chaptarr` data folder is reused.

## Notifications

Talearr uses Chaptarr's existing connection types (Webhook, Discord, Pushover, Apprise,
Notifiarr, Telegram, Slack, ntfy, Gotify, email and the rest); it adds no new connection
type, because stock Chaptarr deletes types it does not know when it starts. Events:

| Event | Trigger flag | Notes |
| --- | --- | --- |
| Grab, import, upgrade | On Grab, On Release Import, On Upgrade | Discord and Pushover show the cover, format (e.g. `Audiobook (M4B)`), narrator and series; upgrades are titled "Book Upgraded" |
| Bundle grabbed as last resort | On Grab | title "Bundle Grabbed (last resort)"; webhook `release.isBundle` / `release.isBundleFallback` |
| Download failed | On Download Failure | now also sent by Webhook and Notifiarr (`eventType: DownloadFailure`) |
| Import failed | On Import Failure | webhook `eventType: ImportFailure`, with `statusMessages` |
| Manual interaction required | On Import Failure | a blocked import that waits for you; webhook `eventType: ManualInteractionRequired` |
| Health issue / health restored | On Health Issue, On Health Restored | restored uses the existing `OnHealthRestored` column; webhook `eventType: HealthRestored` |
| MAM session expired | On Health Issue | the MyAnonamouse session health check (error) |
| MAM bonus purchase made or failed | On Health Issue + Include Health Warnings | bonus points auto-spend; a notice when bought, a warning when MAM refused |
| MAM account is Mouse or seed-only | On Health Issue + Include Health Warnings | warning with the upload credit advice |
| Private torrent at risk of a hit and run | On Health Issue + Include Health Warnings | the seeding risk check (warning); see MyAnonamouse > Seeding status |
| Metadata fallback active | On Health Issue + Include Health Warnings | Talearr's notice-level checks go out with warnings |
| Application update, author/book added and deleted | as upstream | |

Webhook payloads stay Readarr compatible: existing fields and `eventType` names are
unchanged, and new data is added as extra fields (`books[].coverUrl`, `mediaType`,
`narrator`, `series`). Rolling back to Chaptarr keeps every flag (they are existing columns).

## Prowlarr and Readarr API clients

- **Prowlarr**: add Talearr as a **Readarr** application (URL `http://<host>:8789`, API key
  from Settings > General). Prowlarr syncs its indexers as Newznab/Torznab through
  `/api/v1/indexer`, `/indexer/schema` and `/indexer/test`. Sync categories 3030
  (audiobook) and 7000-7999 (7020 ebook); at search time Talearr sends audiobook searches only
  the 3xxx categories and ebook searches only the 7xxx ones, so keep both in the app's
  sync categories. MAM results (native indexer or through Prowlarr) carry 3030/7020 plus
  the site category as `100000 + id`, like Prowlarr reports them.
- **Readarr clients** (Allseerr, dashboards, mobile apps): `/api/v1/system/status`, `/book`,
  `/book/lookup`, `/author`, `/author/lookup`, `/queue`, `/queue/status`, `/wanted/missing`,
  `/wanted/cutoff`, `/calendar`, `/command`, `/history` answer with Readarr-shaped resources
  plus Chaptarr's per-format fields. `appName` stays `Chaptarr`. Clients that expect one
  quality profile and root folder per author can use the scoped base URLs
  `/readarr/hc/audiobook`, `/readarr/hc/ebook` (or `/audiobook`, `/ebook`).
- `src/Chaptarr.Core.Test/Api/TalearrApiCompatibilityFixture.cs` pins the routes, fields and
  enum strings these clients read; a change that breaks one fails the build.

## Grab policy

Settings > Indexers > Grab policy controls what Talearr grabs and what it does when a grab
goes wrong:

| Setting | Default | What it does |
|---|---|---|
| Abridged audiobooks | Unabridged only | Audiobook releases marked "abridged" are skipped (shown as a soft filter in interactive search, so you can still grab one on purpose). They are allowed when the wanted edition is itself abridged, judged from the edition title or info, or from its runtime being under 60% of the longest edition in the same language. "Abridged only" does the opposite: releases marked unabridged are skipped, unmarked ones are allowed only when the monitored edition is abridged, abridged releases rank first, and before searching a book without files Talearr monitors its best abridged edition (see [docs/TALEARR_RECORDINGS.md](docs/TALEARR_RECORDINGS.md#per-book-abridged-policy)). "Prefer unabridged" allows them but always ranks them below unabridged or unmarked releases. "Any" turns this off. A book can have its own value (from Allseerr), which wins over this one. |
| Allow bundles | Off | Series bundles, omnibuses, box sets, collections, "complete novels", "N books" sets and explicit title lists ("Great Innovators (Benjamin Franklin; Einstein; Steve Jobs)") are skipped for single-book searches. A wanted book that is itself an omnibus still matches bundle releases. |
| Fail rejected imports | On | When an import is rejected because the release is the wrong book, its files are in the wrong language, its archives are unusable, or it holds no audio or ebook files (zip, rar and 7z are unpacked first, see [docs/TALEARR_IMPORT.md](docs/TALEARR_IMPORT.md)), the download is marked failed. That blocklists the release and starts the automatic redownload. Archive formats Talearr does not unpack, same-title matches (usually duplicate metadata), transient errors and explicit interactive grabs stay in the queue as before. |
| Automatic retry limit | 3 | Failed downloads per book within 7 days before automatic searching stops. |
| Retry failed grabs | On | When a release cannot be sent to the download client because the indexer (or Prowlarr) answered 429, a 5xx error or timed out, the next approved release of the same search is tried (other releases of an indexer that just said 429 are left for later), and if nothing could be sent the book is searched again after 2, 10 and 30 minutes. The wait honours Retry-After and the indexer back-off shown under System > Status, is capped at an hour, and the number of re-searches is the automatic retry limit. Only automatic and user-started searches are retried; RSS sync and interactive grabs behave as before. Each step is logged with a "Talearr:" prefix. |

Settings > Indexers > **Format policy** (see [docs/TALEARR_FORMAT_POLICY.md](docs/TALEARR_FORMAT_POLICY.md))
picks the format grabbed for a wanted book, for the library and per author: Both (default,
upstream behaviour), Prefer audiobook (accept the ebook after the audiobook has been missing for
24 hours, still upgrade to audio later), Audiobook only or Ebook only. Monitoring is unchanged;
the policy is a soft filter on grabs, stored as `talearr.formatpolicy.*` rows through
`/api/v1/config/talearrformatpolicy`.

Settings > Indexers > **Scheduled missing search** searches monitored missing books again on a
schedule: Off (default), Weekly or Daily, with "Books per run" (default 20) and a wait between
books (default 30 seconds). Readarr and Chaptarr only search a book when it is added, so a book
requested before an indexer was added is otherwise only found by RSS. Each run takes the books
searched longest ago first (the existing `LastSearchTime` every search records, so nothing new is
stored), skips books already in the queue, and shows its next run in System > Tasks as "Scheduled
Missing Book Search". It is an automatic search, also when started from System > Tasks, so delay
profiles and every grab protection apply. Wanted > Search All Missing is a manual
(user-invoked) search and skips delay profiles. Settings are `talearr.missingsearch.*` rows
through `/api/v1/config/talearrmissingsearch`.

Release matching also guards against generic titles and other authors: a short title ("All In",
"Bride", "Deep End") found only inside a hyphenated compound ("All-in-One") does not match, a
bundle must name the book as a whole phrase, must not credit another author, needs the author for
a short title or a course or marketing pack, and must not contradict a number in the wanted title
("45 Murderers" is not "48 ... Murder Cases"). A release that leads with a different author
("Craig Rice - ...") is rejected, a series-named book ("The Naturals", #1) does not match another
position of its series ("[The Naturals 04.5] - Twelve"), and an import whose ebook author tag,
file name or release name credits a different author is rejected ("Completed download is by X,
not Y"), which fails the download like a wrong-book import.

Automatic redownload itself is the existing Download Clients > Failed Download Handling
setting, which is on by default. A replacement search covers every indexer with automatic
search enabled, Usenet and torrent. Grabs you made yourself (interactive or a manual search)
are retried as a manual search, so an unmonitored book or a delay profile does not block the
replacement. Each step is appended to the Download Failed entry in History, for example
"Talearr: release blocklisted, searching all indexers for a replacement (attempt 1 of 3)".

Storage: these five settings live in the existing `Config` key/value table as
`talearr.grabpolicy.*` rows, read and written only by Talearr through the new
`/api/v1/config/talearrgrabpolicy` route. Per-book abridged values live in `talearr.db`
(`/api/v1/talearr/grabpolicy/book/{bookId}`). There is no schema change, no existing route or
field changes, and `config.xml` is not touched. Chaptarr ignores the rows, so a rollback
runs Chaptarr's own behaviour on the same database.

## Versioning

Talearr has its own version, set in `src/Talearr.props` (currently `0.18.16`). System >
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
stays). Turn the setting off for the upstream behaviour. Talearr's own steps never touch the
seeding data either: archives are unpacked into a separate work folder and only that folder is
deleted, an M4B merge writes a new file and only removes the library's links, bundle leftovers
are only deleted for usenet, and tag or date writes break a library hardlink first. Moving a
still-hardlinked file to the recycle bin no longer changes the torrent copy's timestamp.

**Post-import categories.** qBittorrent, Deluge, rTorrent, uTorrent and Flood can move an
imported torrent to a category or label (Audiobook / Ebook Post-Import Category, advanced
settings of the download client). Torrents in that category are kept by Talearr and never
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
edition). Talearr adds:

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
| `ghcr.io/dancue44/talearr:0.18.16` | `main` (Talearr version) and `v0.18.16` git tags |
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

### Unraid

`unraid/talearr.xml` is a Community Applications style template (port 8789, `/config`,
`/books`, `/audiobooks`, `/downloads`, `PUID` 99, `PGID` 100, `UMASK` 002). Until it is in
Community Applications, copy it to `/boot/config/plugins/dockerMan/templates-user/my-TaleArr.xml`
and pick it under Docker > Add Container > Template.

Hardlinks (instant moves, no second copy while seeding) only work when the download and the
library are on the **same share and the same mount inside the container**. Two separate mappings
such as `/downloads` and `/books` are two mounts, even on one share, so imports fall back to a
copy. For hardlinks, use one parent share, for example `/mnt/user/data` with `downloads/` and
`media/books/`, `media/audiobooks/` inside it, and map it once (Add another Path:
`/data` to `/mnt/user/data`, and clear the three separate paths). Give the download client the
same `/data` mapping, set its completed folder under `/data/downloads`, and add
`/data/media/books` and `/data/media/audiobooks` as root folders. Unraid can only hardlink within
one disk or pool, so a hardlink fails (and Talearr copies) when a download sits on the cache
and its library folder already lives on an array disk.

To move an existing Chaptarr container to Talearr (and back), see
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
request per second, a `Talearr/<version>` user agent, responses cached for 6 hours).

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
- Audiobooks: when Open Library lists an Audible ASIN for a work (`id_amazon`, B0...), Talearr
  asks Audnexus (`api.audnex.us`, no key, same user agent, one request per second, cached a day,
  at most 20 lookups per author) for that product and, if its title and author match, adds an
  audio edition with narrators, runtime and Unabridged/Abridged. Only fallback-built books are
  enriched; metadata server data is never touched, and an Audnexus outage only skips this.
- Limits: Open Library has no series, and Audnexus has no title search, so works without an
  Audible ASIN on Open Library get one print edition (audiobook rows search by title and author
  as usual). An author with no
  Goodreads or Amazon id on Open Library stays on Open Library data until it is re-added. A
  book that matches no server book, or more than one, is left to the normal refresh rules.
- System > Status shows a notice while the fallback is active and for 24 hours after it was
  last used. The setting and live status are at `/api/v1/config/talearrmetadatafallback`
  (stored as a `talearr.metadatafallback.mode` row; no schema change).

## Keeping up with Chaptarr

See [docs/TALEARR_UPSTREAM_SYNC.md](docs/TALEARR_UPSTREAM_SYNC.md).

## Privacy

Like Chaptarr, Talearr uses `api2.chaptarr.com` for metadata and matching, and the update
check contacts Chaptarr's update service. See the upstream README for what those requests
contain.
While the metadata server is unavailable, the metadata fallback (on by default) sends search
terms and author names to `openlibrary.org`; turn it off under Settings > Metadata.

## License and credits

Talearr is free software under the [GNU GPL v3](LICENSE). See [COPYRIGHT.md](COPYRIGHT.md).

Talearr is based on [Chaptarr](https://github.com/Chaptarr/chaptarr) by the Chaptarr
contributors, which is based on [Readarr](https://github.com/Readarr/Readarr) and the
shared codebase of the Servarr team ([Sonarr](https://github.com/Sonarr/Sonarr),
[Radarr](https://github.com/Radarr/Radarr), [Lidarr](https://github.com/Lidarr/Lidarr),
[Prowlarr](https://github.com/Prowlarr/Prowlarr)). Thank you to all of them.

Talearr is not affiliated with or supported by the Chaptarr project or the Servarr team.
Please do not send Talearr support requests to them.

- Copyright 2026 Talearr contributors
- Portions copyright 2026 Chaptarr contributors
- Portions copyright 2010-2026 the Servarr team and contributors
