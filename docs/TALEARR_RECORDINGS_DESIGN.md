# Talearr Phase 2: audiobook recordings (design)

Status: built in Talearr 0.12.0 (PRs 2 to 9 below); user documentation is in
[TALEARR_RECORDINGS.md](TALEARR_RECORDINGS.md). Written 2026-10-16 against Talearr 0.11.0
(based on Chaptarr 0.9.965, main database migration 107).

Where the build differs from this design:

- The upgrade guard and the keep both reroute live in `ImportApprovedBooks.Import` (download
  imports, before any old file is staged), not in an `IImportDecisionEngineSpecification` or
  next to `BookUnitDestinationService`: Chaptarr's import pipeline has no such specification
  interface any more, and library scans (the unit destination path) are not replacements.
- No `DownloadDecisionComparer` bitrate tie break and no release-title custom format presets
  yet; the "same codec only" rule is applied to measured bitrates at import.
- All recordings and upgrade settings are one Recordings block on Settings > Metadata.
- The upgrade settings are nullable on `PUT /api/v1/config/talearrrecordings`, so an older
  client keeps them.
- The "2 recordings" badge in book lists is not built yet.

## Goal

A user put it this way: "What I would love is if someone is listening and reading an abridged version
and is able to set the unabridged version to download. There's no way to keep both copies?"

Phase 2 covers three things:

1. **Audible metadata per recording**: narrator, runtime, abridged or unabridged, publisher,
   release date, ASIN, for a chosen Audible marketplace (US, UK, ...).
2. **More than one recording of the same book**, each with its own files (abridged and
   unabridged, or two narrators), and an "add the unabridged version" action that AllSeerr and
   NoBookEnds can trigger too.
3. **An upgrade path** that replaces a worse copy with a better one (bitrate, codec, ebook format)
   under clear rules, and never replaces one recording with a different recording.

The fixed constraint: stock Chaptarr must still start on the same database after a rollback and
behave sensibly on the data Talearr left behind.

## Summary of the recommendation

- **Recordings are upstream's own "wanted copy" rows (option c).** Chaptarr already models a second
  copy of an audiobook as a second `Books` row in the same identity group (same `BaseBookId` and
  `MediaType`), pinned to one edition. Its narrator picker already creates these rows through
  `POST /api/v1/book/{id}/editions/wanted`. Talearr builds the recordings feature on that
  mechanism, so it needs no new tables or columns for the recordings themselves, and stock
  Chaptarr understands every row Talearr creates.
- **Audible facts live in a separate `talearr.db` (option a), as a rebuildable cache keyed by
  provider identity (marketplace + ASIN), not by Chaptarr row ids.** Losing or deleting it costs
  nothing but a refetch. Chaptarr never opens it.
- **Settings use `talearr.*` rows in the existing `Config` table**, the pattern the grab policy
  already uses.
- **No FluentMigrator migrations (option b is not used).** The invariant in README and
  `TALEARR_UNRAID_SWAP.md` ("Talearr does not add database migrations", schema byte-for-byte
  unchanged, checked by the Docker workflow) stays true. Option b was checked and would work
  today, but it has costs listed below, and none of the Phase 2 data needs it.

## What exists today

### Data model (relevant parts)

| Table / model | Relevant fields | Notes |
|---|---|---|
| `Books` (`Book.cs`) | `MediaType` (Audiobook=0, Ebook=1), `BaseBookId`, `UnitKeyHash`, `AnyEditionOk`, `AddOptions.AddType`, `AudiobookMonitored`, `EbookMonitored`, `Narrator`, `DurationMinutes`, `WantedNarratorId` (legacy) | One row per media type of a provider pocket, plus optional copy rows. |
| `Editions` (`Edition.cs`) | `BookId`, `ForeignEditionId`, `Asin`, `Asins`, `AudibleASIN`, `Narrator`, `NarratorNames`, `NarratorCredits`, `DurationSeconds`, `Publisher`, `ReleaseDate`, `Language`, `Format`, `EditionInfo`, `EditionFormat`, `AudioProductionType`, `IsGraphicAudio`, `Chapters`, `Monitored`, `ManualAdd` | One monitored edition per book row. |
| `BookFiles` (`BookFile.cs`) | `EditionId`, `Path`, `Quality`, `MediaInfo` (`AudioFormat`, `AudioBitrate`, `AudioChannels`, `AudioSampleRate`, `Duration`), `DurationSeconds`, `Narrator`, `MediaType`, `ReplicaPaths` | Files hang off an edition, so off one book row. |

### Multiple copies are already an upstream concept

- `BookService.AddWantedEdition(bookId, editionId)` (audiobooks only): if the book has no files it
  just pins the edition (`ManualAdd = true`, `AnyEditionOk = false`). If it has files it creates a
  **new Book row** with the same `BaseBookId`, provider ids and media type, slug
  `<slug>_wanted_<editionId>`, `AddOptions.AddType = Manual`, `AnyEditionOk = false`, pinned to the
  chosen edition, and optionally searches it. It returns an existing wanted row instead of making a
  duplicate when one already pins the same edition key (`hc:`, `gr:`, `ol:`, `az:`, ISBN, foreign id).
- The UI reaches it from the narrator cell (`frontend/src/Book/Narrator/NarratorSearchModal*`,
  `narratorActions.js`): picking a narrator on a book that has files creates a wanted copy.
- On import, `BookUnitDestinationService` routes a second physical unit (a different folder) of the
  same work to a copy row, cloning one if needed (`_copy_<ticks>` slug, `UnitKeyHash` stamped), and
  `EditionPinPolicy` stops an import from overriding a pinned edition.
- Refresh never merges copy rows away: `RefreshAuthorService.HasLocalBookInstancePreservationMarker`
  keeps rows with `AddType = Manual`, a `UnitKeyHash`, `AnyEditionOk = false` or a manual edition.
- The edition resource already has `monitoredByAnotherAudiobookBook`.
- Upgrades are per book row: `UpgradeMediaFileService.UpgradeBookFile` removes
  `localBook.Book.BookFiles`, which `MediaFileRepository.GetFilesByBook` scopes to that one row. A
  better release for the unabridged copy never deletes the abridged copy's files.

So the hard part of "keep both copies" already works in stock Chaptarr. What is missing is knowing
which edition is the abridged or unabridged recording, a direct action for it, keeping two copies
with the same narrator from landing in the same folder, a clear view of the recordings of a book,
and an API for AllSeerr and NoBookEnds.

The live library has one copy row today (book 7485, "Caught Up", ebook, `_copy_` slug), so the
mechanism is in use.

### What the metadata server provides per edition

From `V5Edition` (`MetadataSource/BookInfo/V5/V5AuthorResponse.cs`) and the live
`GET /api/v1/edition?bookId=...` on a test Chaptarr (0.9.965, read only):

| Field | Source field | Live coverage (504 audiobook editions over 4 books) |
|---|---|---|
| Narrator(s) | `narratorNames`, `narrators` (credits) | 59% non-empty |
| Runtime | `durationSeconds` (`LegacyDurationMinutes`) | 17% non-zero |
| Publisher | `publisher` | 89% |
| Release date | `publicationDate` | 91% |
| ASIN | `asin`, `asins`, provider ids (`AudibleASIN`) | 24% have `audibleASIN` |
| Language | `language` | 99% |
| Abridged | none. Only free text: `editionInfo` (0% filled), title words ("Unabridged on CDs") | 3 of 504 mention it, all in titles |
| Marketplace | none | ASINs are a mix of marketplaces |

Example, "The Firm" (book 3401, 81 editions, 40 audiobook): the monitored edition 28701 (Scott
Brick, Random House Audio, 61800 s, `audibleASIN` B002PE36VO) and a UK edition 28702 (Hodder &
Stoughton, B00NIXU8GW). The abridged D.W. Moffett readings (1991 to 2006) carry no runtime, no ASIN
and no abridged marker.

Two things this shows:

- The abridged flag has to come from somewhere else. Talearr's grab policy currently infers it from
  title words or a runtime under 60% of the longest same-language edition
  (`AbridgedReleaseDetector.IsAbridgedByMetadata` / `IsAbridgedByRuntime`), which fails when runtime
  is missing, as it is for most editions.
- Stored ASINs go stale and are marketplace specific. B002PE36VO now returns nothing from the US
  catalog (Audnexus answers `REGION_UNAVAILABLE`); the current US product for the same recording is
  B002V5BSLC (unabridged, 1030 min, Random House Audio, 2009-09-16, Scott Brick). B00NIXU8GW is a
  UK product (unabridged, 1029 min, Hodder & Stoughton, 2014-09-25).

### What needs Audible (or Audnexus)

Upstream already calls the Audible catalog: `MetadataSource/Audible/AudibleCatalogProxy.cs` hits
`https://api.audible.com/1.0/catalog/products[/{asin}]` with `product_desc,contributors,media,product_attrs`,
rotates browser user agents, caches 24 h through `ICachedHttpResponseService`, and maps
`format_type` into `AudibleCatalogBookResource.BookFormat`. It is US only and is used only for the
explicit "audible" search provider, not for edition enrichment.

Fields the Audible catalog gives per product, checked live:

| Need | Audible catalog field | Audnexus (`/books/{asin}?region=`) |
|---|---|---|
| Abridged | `format_type` (`unabridged`, `abridged`) | `formatType` |
| Runtime | `runtime_length_min` | `runtimeLengthMin` |
| Narrators | `narrators[].name` (`contributors`) | `narrators[].name` |
| Publisher | `publisher_name` | `publisherName` |
| Release date | `release_date`, `issue_date` | `releaseDate` |
| Language | `language` | `language` |
| Title, subtitle | `title`, `subtitle` (`product_desc`) | `title`, `subtitle` |
| Marketplace | the host you asked | `region` |

Marketplace hosts: `api.audible.com` (us), `.ca`, `.co.uk`, `.com.au`, `.fr`, `.de`, `.it`, `.es`,
`.co.jp`, `.in`. Audnexus regions: `us`, `ca`, `uk`, `au`, `fr`, `de`, `it`, `es`, `jp`, `in`.

Limits and terms:

- **Audible catalog API** is unofficial and undocumented, needs no account, and has no published
  rate limit. It throttles heavy use with 429 or 503 responses. Audible's Conditions of Use forbid
  robots and data mining, so this is used at the user's own risk, the same risk upstream Chaptarr
  already takes. Talearr keeps volume low: batch lookups (`?asins=a,b,c`, one request for many
  ASINs), at most one request per second, a long cache, enrichment only for editions the user cares
  about, and no redistribution of the data.
- **Audnexus** (`api.audnex.us`, GPL-3.0, community run, what Audiobookshelf uses) defaults to 100
  requests per minute per source (`MAX_REQUESTS`). It is one ASIN per call and depends on a
  volunteer server. Offer it as an alternative provider, not the default: the direct catalog has
  batch lookups and search, and upstream already depends on it.

## Options for storing Talearr data

What has to be stored:

- Settings (marketplace, provider, upgrade rules): a handful of values.
- Audible facts per product: rebuildable from Audible at any time.
- Recording rows and their files: must survive rollback and be understood by Chaptarr.
- Optional request provenance (which AllSeerr request asked for which copy): nice to have.

### (a) Separate `talearr.db`, keyed by Chaptarr ids

A SQLite file next to `chaptarr.db`, opened only by Talearr, with its own tiny schema versioning
(`PRAGMA user_version`), not FluentMigrator.

- **Rollback:** Chaptarr never opens it. `chaptarr.db` is untouched, so rollback is the image swap
  it is today. The file can stay (Talearr picks it up again on return) or be deleted.
- **Upstream merges:** no conflict surface beyond a registration line in the container setup.
- **Existing data:** none touched.
- **Costs:** two databases can drift. Chaptarr row ids are "handles, not identity"
  (`API_IDENTITY_AND_LIFECYCLE.md`): refresh, merge and reimport can delete and recreate rows, and a
  restored Chaptarr backup brings back older rows. Anything keyed by Chaptarr ids goes stale. Not in
  Chaptarr's backup zip unless Talearr adds it. On Postgres installs it is still a SQLite file in
  the config folder.
- **Fix for the costs:** store only rebuildable data there, and key it by provider identity
  (marketplace + ASIN, `ForeignEditionId`, `BaseBookId`), with Chaptarr ids at most as a lookup hint
  that is rechecked on read. Then drift, backups and Postgres stop mattering: a missing or stale
  row means a refetch.

### (b) Additive tables or columns in `chaptarr.db`, migrations in a reserved high range

Checked in the code:

- `MigrationController.Migrate` calls FluentMigrator 7.2 `runner.MigrateUp()` over every
  `[Migration(n)]` in `NzbDrone.Core.Datastore.Migration` (exact namespace, so Talearr migrations
  would have to live there). `MigrateUp` applies every known migration not yet in `VersionInfo` and
  ignores `VersionInfo` rows it has no class for. It does not validate version order (that is a
  separate `ValidateVersionOrder` call, not used). So stock Chaptarr starts on a database that has
  Talearr versions, and upstream migration 108 still applies after a Talearr 9000001.
- `RebasedVersionCollisionSchemaRepair` (a `BeforeAll` maintenance step) only looks at upstream's
  own reused low numbers (4, 5, 6, 9, 10, 15, 22, 23, 24, 25). It does not react to unknown versions.
  Its existence shows upstream has renumbered migrations before, which is why a reserved range far
  above upstream's (for example 9000000 and up; versions are cast to `int`) would be needed.
- `MigrationController` has a whole-schema checksum hook (`BaselineSchema.ExpectedSqliteSha256`,
  `ExpectedPostgresSha256`). Both are empty today, so it is off. If upstream ever fills them in,
  stock Chaptarr would refuse to start on a database with extra Talearr tables.
- `IDatabase.Migration` is `MAX(Version)` from `VersionInfo`. After a Talearr migration, System >
  Status and Sentry on stock Chaptarr would report 9000001 instead of 107, and the swap doc's
  "same migrationVersion" check no longer holds.
- Migrations run for main, log and cache databases (`NzbDroneMigrationBase` dispatches on type), so
  a Talearr version row would appear in `logs.db` and `cache.db` too.
- Columns on upstream tables are the riskier half: SQLite column drops and alters rebuild the table
  from the columns upstream knows, so a future upstream migration on `Books` or `Editions` could
  silently drop a Talearr column, or fail on one. New tables avoid that, but must have no foreign
  keys into upstream tables, or Chaptarr deletes of books and editions could fail after rollback.

Summary for (b):

- **Rollback:** works today. Talearr tables sit unused, `migrationVersion` reads differently, and
  the empty checksum hook is a future trap.
- **Upstream merges:** fine as long as migrations stay in the reserved range and touch only
  Talearr tables.
- **Existing data:** untouched if tables only.
- **Against it:** it breaks a documented and CI-checked promise, and the only data that needs to
  be durable (the recording rows) does not need new schema.

Keep (b) as the fallback for a later phase that really needs durable, non-rebuildable Talearr data.
If it is ever used: tables only, `Talearr` prefix, no foreign keys, versions from 9000000, a
`talearr-rollback-prep` command that can drop them, and updated swap docs.

### (c) Extra recordings as extra books or editions inside the existing schema

- **Extra editions only** (keep one book row, monitor two editions) does not work: the "one
  monitored edition per book row" rule is enforced in many places (Talearr's own Phase 1
  `BookController` fix stores exactly one), and an upgrade on the row deletes every file of the row.
- **Extra book rows** is exactly upstream's wanted copy. Rows are created by upstream code paths,
  survive refresh, keep separate files and upgrade separately.
- **Rollback:** stock Chaptarr sees ordinary copy rows that its own narrator picker would have
  made. Nothing to undo.
- **Upstream merges:** Talearr calls `IBookService.AddWantedEdition` and reads the identity group;
  it does not fork that logic. If upstream changes copy semantics, Talearr follows.
- **Existing data:** none rewritten. Copies show as separate entries in Chaptarr's book lists and
  API (already true for upstream copies).
- **Gaps Talearr fills:** which edition is which recording, folder collisions between copies, a
  recordings view, and an API.

### Decision

| | Rollback | Upstream merges | Existing data | Fit |
|---|---|---|---|---|
| (a) talearr.db | Untouched DB, swap only | No conflicts | Untouched | Good for rebuildable caches keyed by provider ids |
| (b) high-range migrations | Works now; version number shows 9000001; checksum hook risk | OK with discipline | Untouched (tables only) | Overkill for Phase 2; breaks the no-migration promise |
| (c) wanted copy rows | Native rows, nothing to undo | Uses upstream API | Untouched | Right model for recordings |

Use **(c) for recordings, (a) for Audible facts, `Config` rows for settings.**

## Data model

### Recordings (no new schema)

A **recording** is one audiobook `Books` row of an identity group, together with its monitored
edition and files. The identity group is what `BookUnitDestinationService.GetIdentityGroupBooks`
uses: same `AuthorId` and `MediaType`, same `BaseBookId` or intersecting provider ids. The base row
is the lowest-id row without `_wanted_` / `_copy_` in its slug; the rest are copies.

Derived per recording (computed, not stored):

| Field | From |
|---|---|
| `bookId` | the row (local handle) |
| `editionId`, `foreignEditionId` | monitored edition |
| `narrators` | Audible facts, else edition `NarratorNames` / `Narrator`, else file tag `BookFile.Narrator` |
| `abridged` | `unabridged` / `abridged` / `unknown`, see "Abridged resolution" |
| `runtimeMinutes` | Audible, else edition `DurationSeconds`, else files `DurationSeconds` |
| `publisher`, `releaseDate`, `language` | Audible, else edition |
| `asin`, `marketplace` | resolved Audible product |
| `hasFiles`, `quality`, `bitrateKbps`, `codec` | `BookFiles` (`Quality`, `MediaInfo`) |
| `role` | `primary` (base row) or `additional` (copy) |

### `talearr.db` (rebuildable cache)

Location: the app data folder next to `chaptarr.db` (`/config/talearr.db`). SQLite in WAL mode,
schema version in `PRAGMA user_version`, opened with `Microsoft.Data.Sqlite` and Dapper (both
already dependencies). Created on first use.

```
AudibleProducts
  Marketplace      TEXT  NOT NULL   -- us, uk, ca, au, fr, de, it, es, jp, in
  Asin             TEXT  NOT NULL
  Status           INT   NOT NULL   -- 0 ok, 1 not found, 2 region unavailable
  Title            TEXT
  Subtitle         TEXT
  FormatType       TEXT             -- unabridged | abridged | null
  RuntimeMinutes   INT
  Narrators        TEXT             -- JSON array of names
  Authors          TEXT             -- JSON array of names
  Publisher        TEXT
  ReleaseDate      TEXT             -- ISO date
  Language         TEXT
  Source           TEXT  NOT NULL   -- audible | audnexus
  FetchedAt        TEXT  NOT NULL
  PRIMARY KEY (Marketplace, Asin)

EditionAudibleLinks                 -- which product a Chaptarr edition resolved to
  ForeignEditionId TEXT  NOT NULL   -- provider identity, survives row churn
  Marketplace      TEXT  NOT NULL
  Asin             TEXT             -- null = searched, nothing matched
  Method           TEXT  NOT NULL   -- stored-asin | asins-list | search
  Confidence       REAL  NOT NULL
  EditionIdHint    INT              -- local handle, rechecked on read
  CheckedAt        TEXT  NOT NULL
  PRIMARY KEY (ForeignEditionId, Marketplace)

RecordingRequests                   -- optional provenance, safe to lose
  Id               INTEGER PRIMARY KEY
  BaseBookId       TEXT  NOT NULL
  CopyBookIdHint   INT
  ForeignEditionId TEXT
  Kind             TEXT  NOT NULL   -- unabridged | abridged | narrator | edition
  RequestedBy      TEXT             -- ui | allseerr | api
  ExternalRef      TEXT             -- e.g. AllSeerr request id
  CreatedAt        TEXT  NOT NULL
```

Cache rules: positive rows 30 days, `not found` and `region unavailable` 7 days, refresh on demand
from the UI. Delete the file and everything is refetched.

### Settings (`Config` rows, as the grab policy does)

| Key | Default | Meaning |
|---|---|---|
| `talearr.recordings.marketplace` | `us` | Audible marketplace used for lookups and links |
| `talearr.recordings.provider` | `audible` | `audible` (direct catalog), `audnexus`, `off` |
| `talearr.recordings.enrich` | `monitored` | `monitored` (monitored and copy editions), `all` (every audiobook edition of monitored books), `off` |
| `talearr.recordings.keepBoth` | `true` | an unabridged release for a book whose files are abridged becomes a new recording instead of replacing |
| `talearr.upgrade.guard` | `on` | the upgrade guard below |
| `talearr.upgrade.minBitrateGainKbps` | `16` | same codec: minimum bitrate gain to count as better |
| `talearr.upgrade.runtimeTolerancePct` | `3` | a replacement shorter than this is not the same recording |

### Abridged resolution

In order, first answer wins:

1. Audible `format_type` of the resolved product in the chosen marketplace.
2. Explicit text on the edition: title, subtitle, `EditionInfo`, `EditionFormat`
   (`AbridgedReleaseDetector.Detect`).
3. Runtime: under 60% of the longest same-language edition with a known runtime, using Audible
   runtimes where known (`IsAbridgedByRuntime`, now with more data).
4. For a recording with files: NoBookEnds Sync alignment coverage, when available (Phase 3, see
   below), else file duration against the unabridged runtime.
5. Otherwise `unknown`.

`AbridgedReleaseDetector` gets the resolved value through a small Talearr service, so the grab
policy ("allowed when the wanted edition is itself abridged") becomes reliable.

### Resolving an edition to an Audible product

1. Stored `AudibleASIN`, then each of `Asins`, looked up in the chosen marketplace in one batch call.
2. If none answers (stale or other marketplace), search the marketplace by title + author, keep
   products whose narrators match the edition's and, when both are known, whose runtime is within
   5%. Release year and publisher break ties. Below a confidence threshold, store no link.
3. Store the result in `EditionAudibleLinks`. Never write back to `Editions` (upstream refresh owns
   those columns and would overwrite them anyway).

## API additions (additive only)

All under Talearr-only controllers and routes. No existing route, field or status code changes.
Identities in responses follow the provider-identity contract: local ids are handles, provider ids
are what clients should store.

### `GET /api/v1/talearr/recordings?bookId={id}`

`bookId` may be any row of the group, ebook rows included (the audiobook group of the same
`BaseBookId` is returned). Optional `providerId=` instead of `bookId`.

```json
{
  "baseBookId": "hc:149559",
  "providerIdsAll": ["hc:149559", "gr:173491"],
  "marketplace": "us",
  "recordings": [
    {
      "bookId": 3401,
      "role": "primary",
      "editionId": 28701,
      "foreignEditionId": "hc:...",
      "narrators": ["Scott Brick"],
      "abridged": "unabridged",
      "abridgedSource": "audible",
      "runtimeMinutes": 1030,
      "publisher": "Random House Audio",
      "releaseDate": "2009-09-16",
      "asin": "B002V5BSLC",
      "audibleUrl": "https://www.audible.com/pd/B002V5BSLC",
      "monitored": true,
      "hasFiles": true,
      "files": { "count": 1, "quality": "M4B", "codec": "aac", "bitrateKbps": 64, "sizeBytes": 520000000 },
      "path": "/audiobooks/John Grisham/The Firm - Scott Brick"
    }
  ],
  "candidates": [
    {
      "editionId": 28705,
      "narrators": ["D.W. Moffett"],
      "abridged": "abridged",
      "abridgedSource": "runtime",
      "runtimeMinutes": null,
      "asin": null,
      "publisher": "Random House Audio",
      "releaseDate": "1991-02-28"
    }
  ]
}
```

`candidates` are audiobook editions in allowed languages that are not already a recording, grouped
so near-identical editions (same narrator, abridged status and runtime within 5%) show once.

### `POST /api/v1/talearr/recordings`

```json
{ "bookId": 3401, "kind": "unabridged", "editionId": null, "narrator": null, "search": true,
  "requestedBy": "allseerr", "externalRef": "req-812" }
```

- `kind`: `unabridged`, `abridged`, `narrator` (needs `narrator`) or `edition` (needs `editionId`).
- Talearr picks the best matching candidate edition (abridged status known, preferred language,
  then Audible link confidence, then runtime known, then upstream popularity), then calls
  `IBookService.AddWantedEdition(bookId, editionId)` and, if `search`, queues `BookSearchCommand`
  exactly as upstream does.
- Responses: `201` with the recording, `200` when that recording already exists (upstream's own
  duplicate check), `404` when no edition fits (body lists the candidates), `409` when the request
  is ambiguous (several candidates tie; body lists them, same spirit as `ProviderAmbiguityResource`),
  `400` for an ebook-only book or bad input.
- `POST /api/v1/book/{id}/editions/wanted` stays as it is; this route is a convenience on top.

### `POST /api/v1/talearr/recordings/refresh?bookId={id}`

Refetches Audible facts for that group (ignores the cache). Returns `202`.

### `GET|PUT /api/v1/config/talearrrecordings`

The settings above, same shape as `/api/v1/config/talearrgrabpolicy`.

### Removing a recording

No new route. A copy is removed with the existing `DELETE /api/v1/book/{id}` (with or without
files). The UI only offers it on copy rows.

## UI sketches

**Book details, new "Recordings" section** (audiobook side, under the editions area). A compact
table, one line per recording:

```
Recordings                                        Marketplace: US   [Refresh]
  Scott Brick      Unabridged   17h 10m   Random House Audio 2009   M4B 64 kbps   Downloaded
  D.W. Moffett     Abridged      -        Random House Audio 1991   -             Wanted (searching)
                                                     [+ Add another recording v]
```

- Abridged and Unabridged are badges; "Unknown" is a muted badge with a tooltip that says why
  ("no runtime or Audible match").
- Clicking a narrator opens the existing edition picker scoped to that recording.
- "Add another recording" is a menu: "Unabridged version", "Abridged version", "Another narrator...",
  "Pick an edition...". Items that cannot be satisfied are disabled with the reason ("no unabridged
  edition known").
- Each copy line has the usual Search, Interactive search and a "Remove recording" action (delete
  the copy row, confirm modal with "Delete files" checkbox).

**Contextual prompt.** When the base recording is abridged and an unabridged candidate exists, the
book header shows a one-line notice: "You have the abridged recording. Add the unabridged one?
[Add and search]".

**Book lists.** Copies already show as separate entries upstream. Talearr adds a small
"2 recordings" badge on the base row and shows the abridged badge in the narrator cell. No
change to sorting or counts.

**Interactive search** on a copy row already searches that row's edition. The abridged soft filter
uses the resolved abridged value of that row's edition, so searching the abridged copy no longer
hides abridged releases and searching the unabridged copy no longer shows them first.

**Settings > Metadata, new "Audible" block**: Marketplace (dropdown), Provider (Audible / Audnexus
/ Off), Enrich (Monitored only / All editions / Off), and a note about the unofficial API.

**Settings > Media Management, "Upgrades" block**: the upgrade guard switch, minimum bitrate gain,
runtime tolerance, and "Keep both when an unabridged recording arrives".

## AllSeerr and NoBookEnds

### AllSeerr

Today AllSeerr keeps one Chaptarr book id per format (`Media.externalServiceId` for ebook,
`externalServiceIdAudio` for audiobook) and drives editions through `chaptarrRequests.ts`
(`monitorEditionInLanguage`, lookups, monitor and search).

Additions (AllSeerr repo, after Talearr ships the API):

- Detect Talearr: `GET /api/v1/system/status` `instanceName` contains "Talearr" and
  `GET /api/v1/config/talearrrecordings` answers. Without it, hide the feature.
- Request option on audiobook requests: `recording: "default" | "unabridged" | "abridged"`. A
  request for a book that is already available as audiobook can ask for the other recording; the
  "already available" check then compares recordings, not just the format.
- On approval: `POST /api/v1/talearr/recordings` with `kind`, `search: true`,
  `requestedBy: "allseerr"`, `externalRef` = request id.
- Track the extra recording: new nullable column `externalServiceIdAudioAlt` (or a small
  `MediaRecording` table if more than two are wanted later), status from
  `GET /api/v1/talearr/recordings`.
- NoBookEnds contract: `GET /api/v1/nobookends/item/{absItemId}` gains
  `recordings: [{ abridged, narrators, status }]` and `canRequestRecording`; `POST /request` accepts
  `recording`.

### NoBookEnds

NoBookEnds talks to Audiobookshelf and AllSeerr, never to Talearr directly.

- Each recording lives in its own folder, so Audiobookshelf shows two library items. NoBookEnds
  groups them by ASIN, ISBN or title + author into one book with a "Recordings" switcher.
- The abridged note it already shows (Listen & Read, `abridgedInfo` from alignment coverage) gets an
  action: "Get the unabridged audiobook", which calls AllSeerr `POST /request` with
  `recording: "unabridged"`. Shown only when AllSeerr status allows requests and reports
  `canRequestRecording`.
- When the new item arrives, the book page shows both recordings and lets the user switch.
- **Phase 3 (not in this design):** carry the listening position across recordings. The natural
  pivot is the ebook text: NoBookEnds Sync aligns each audio item to the same ebook, so a position
  in recording A maps to an ebook offset, and that offset maps to a time in recording B. Talearr's
  part is only to expose stable identities (ASIN, `foreignEditionId`) so the two ABS items can be
  recognised as one book.

## Upgrade path

What exists: `Quality` is the container or format only (MP3, M4B, FLAC, Unknown Audio; EPUB, AZW3,
MOBI, PDF). Quality profile order and cutoff decide upgrades (`UpgradableSpecification`,
`UpgradeDiskSpecification`, `CutoffSpecification`), plus custom format scores (release title, size,
narrator, audio production, indexer flag, release group). After import, `UpgradeMediaFileService`
moves the old files of that book row to the recycle bin (when configured) or deletes them.
`MediaInfo` holds bitrate, codec, channels and duration, but only after download.

Rules Talearr adds (code only, settings in `Config`):

1. **Same recording only.** A release or file may replace a book row's files only if it is the same
   recording: same abridged status (when both known), runtime not shorter than the tolerance
   (default 3%), and narrator not contradicting (file tags or release title). Otherwise:
   - with `keepBoth` on and an unabridged candidate for an abridged row, the import is routed to an
     unabridged copy (created through `AddWantedEdition`) instead of replacing;
   - else the import is rejected with a clear reason ("Different recording: abridged vs unabridged"),
     which the existing rejection labels show.
2. **Audio is better when**, in order: higher quality in the profile (as today); same quality and
   codec with a bitrate gain of at least `minBitrateGainKbps`; same bitrate class and a single M4B
   with chapters replacing many MP3 parts. Lossy to lossy transcodes (bitrate higher but source
   evidently re-encoded, for example 128 kbps MP3 from a 64 kbps AAC release name) are not better.
3. **Never worse on measured facts.** After download, before the old files are removed, compare
   `MediaInfo`: lower bitrate beyond the tolerance, fewer channels, or shorter duration beyond the
   tolerance rejects the upgrade and keeps the old files.
4. **Ebooks**: better means a higher format in the profile (as today) for the same edition language;
   a PDF never replaces a reflowable format unless the profile ranks PDF higher on purpose.
5. **Release-side bitrate** (before download) uses ordinary `ReleaseTitle` custom formats ("64kbps",
   "128kbps", "AAC", "Opus"), which Talearr can offer as presets. No new custom format
   implementation class: stock Chaptarr could not deserialize one after rollback.
6. Every decision is written into History like the grab policy does ("Talearr: kept existing file,
   new file is 32 kbps lower").

Implementation: one Talearr import specification (`IImportDecisionEngineSpecification`) for rules
1 and 3, a marked hook in `DownloadDecisionComparer` for rule 2 tie breaks, and the `keepBoth`
reroute at the point where the import destination is resolved (marked block next to
`BookUnitDestinationService.ResolveDestinationForUnit`).

## Folder collisions between copies

The audiobook format here is `{Book Title}{ - Narrator}/{Book Title}{ (PartNumber:smart)}` with
renaming off. Two narrators get two folders. Two recordings by the same narrator (abridged and
unabridged Scott Brick) would get the same folder, and with renaming off, release file names can
collide too.

Rule: when a copy row's computed book folder equals the folder of another row in its identity
group, Talearr appends a disambiguator to the copy's folder: " (Abridged)" or " (Unabridged)" when
known, else " (<release year>)", else " (<ASIN>)". The base row's path never changes. This is a
marked block in the path builder for copy rows only, not a new naming token (an unknown token would
be left literal by stock Chaptarr after rollback).

After rollback, stored `BookFile.Path` values stay as they are. Only a stock Chaptarr "Rename files"
run could recompute a colliding folder for a copy; the rollback notes say to check
`GET /api/v1/rename` (a preview) before renaming.

## Migration and rollback plan

### Going to Talearr Phase 2

- No database migration. `chaptarr.db` schema is unchanged; the Docker workflow's schema
  byte-for-byte check stays as it is.
- On first start Talearr creates `talearr.db` and writes default `talearr.*` `Config` rows only when
  the user saves settings (reads fall back to defaults).
- Background enrichment runs as a low-priority scheduled command (`TalearrRefreshAudibleFacts`,
  every 12 h, monitored and copy editions first, rate limited). Nothing is written to upstream
  tables by it.
- Existing copy rows (from the narrator picker or import cloning) show up as recordings right away.

### Rolling back to stock Chaptarr

1. Image swap as in `TALEARR_UNRAID_SWAP.md`. Nothing else is required.
2. What stock Chaptarr sees: copy rows it knows how to handle, the `talearr.*` `Config` rows it
   ignores, and files at their stored paths. It keeps both recordings, searches and upgrades each
   row with its own rules (no upgrade guard, no abridged awareness beyond its own).
3. `talearr.db` is ignored; keep it for a return to Talearr or delete it.
4. Before running "Rename files" on stock Chaptarr, preview it; disambiguated copy folders may be
   proposed for a move into the base folder.
5. Coming back to Talearr: everything is picked up again; stale cache rows are refetched.

### Upstream merges

New Talearr-only files: `src/NzbDrone.Core/Talearr/Recordings/` (service, Audible client, cache
store, specification), `src/Chaptarr.Api.V1/Talearr/` (controllers), frontend
`Book/Recordings/` and settings blocks. Marked `// Talearr:` hooks, to be added to the conflict table
in `TALEARR_UPSTREAM_SYNC.md` and the brand check:

- `AbridgedReleaseDetector` (Talearr file already) takes the resolved abridged value.
- Import destination reroute next to `BookUnitDestinationService.ResolveDestinationForUnit`.
- Copy-folder disambiguation in the path builder.
- `DownloadDecisionComparer` tie break.
- Container registration for `ITalearrDatabase` and the services.

Talearr does not change `AddWantedEdition`, `EditionPinPolicy`, refresh preservation or
`UpgradeMediaFileService`; if upstream changes them, Talearr inherits the change. A test fixture
pins the upstream behaviour Talearr depends on (copy creation, per-row upgrade, refresh
preservation), so a merge that changes it fails CI instead of breaking recordings quietly.

## Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Audible throttles or blocks the unofficial API | No new facts | Long cache, batching, 1 req/s, back-off on 429/503, Audnexus as alternative, heuristics still work without it |
| Audible terms forbid automated access | Policy risk for the user | Same exposure as upstream; low volume; documented in settings; can be turned off |
| Stale or foreign-marketplace ASINs from the metadata server | Wrong or no match | Batch lookup of all ASINs, then narrator + runtime constrained search; confidence threshold; never overwrite upstream columns |
| Wrong abridged classification | Wrong edition picked for "add unabridged" | Show the source of the answer; confirm modal lists the chosen edition; user can pick an edition instead |
| Upstream changes copy semantics (wanted copies, `UnitKeyHash`, refresh preservation) | Recordings break or merge | Pinned behaviour tests; Talearr calls upstream services instead of forking them |
| Two copies resolve to the same folder | Files mixed or overwritten | Copy folder disambiguation; import refuses a destination that holds another row's files |
| Upgrade guard rejects a real upgrade | Missed upgrade | Tolerances are settings; History says why; manual import still overrides |
| `talearr.db` corrupt or deleted | Facts missing until refetched | Rebuildable by design; recreated on open failure |
| Copies clutter book lists and wanted counts | Cosmetic | "2 recordings" badge; copies only exist when someone asks for them |
| TalearrCatalogDedupe or Phase 1 edition repair touching copy rows | Copy folded or edition swapped | Both already skip manual and pinned rows; add explicit tests with copy rows |
| Option b checksum hook if ever adopted | Stock Chaptarr refuses to start | Not using b; documented if a later phase needs it |

## Implementation plan (mergeable PRs)

Each PR builds, passes `talearr_brand_check.py`, the test suite and the schema check, and is safe to
ship on its own.

1. **`docs: recordings design`** (this document).
2. **`feat(recordings): talearr.db store and recordings settings`**
   - `ITalearrDatabase` (SQLite file in app data, `user_version` migrations, WAL), `AudibleProducts`,
     `EditionAudibleLinks`, `RecordingRequests`.
   - `TalearrRecordingsSettingsService` over `Config` rows, `GET|PUT /api/v1/config/talearrrecordings`.
   - Tests: open, upgrade, corrupt file recreated; settings round trip; rollback fixture shows
     `chaptarr.db` schema unchanged.
   - No behaviour change.
3. **`feat(recordings): Audible facts per edition`**
   - `TalearrAudibleClient` (marketplace hosts, batch `asins=`, search, Audnexus alternative,
     rate limiter, negative cache), resolver (stored ASIN, ASIN list, constrained search).
   - `TalearrRefreshAudibleFacts` scheduled command; on-demand refresh route.
   - Recorded HTTP fixtures for the Firm examples above (stale US ASIN, UK ASIN, abridged product).
4. **`feat(recordings): abridged resolution in grab policy`**
   - `TalearrAbridgedResolver` (order above) feeding `AbridgedReleaseDetector`.
   - Tests: wanted abridged copy allows abridged releases; unabridged copy ranks them last.
5. **`feat(recordings): recordings API`**
   - `GET /api/v1/talearr/recordings`, `POST /api/v1/talearr/recordings` over `AddWantedEdition`,
     candidate grouping, 404/409 bodies.
   - Pinned upstream behaviour tests (copy creation, per-row upgrade, refresh preservation,
     dedupe and edition repair skip copies).
6. **`feat(recordings): copy folder disambiguation`**
   - Path builder hook for copy rows; import refuses a folder holding another row's files.
   - Tests with same-narrator abridged and unabridged copies, renaming on and off.
7. **`feat(ui): recordings section and add-another-recording`**
   - Book details Recordings section, add menu, contextual prompt, list badge, Settings blocks.
8. **`feat(upgrades): recording-aware upgrade guard`**
   - Import specification (same recording, never worse on measured facts), comparer tie break,
     `keepBoth` reroute, History notes, release-title custom format presets.
9. **`docs: phase 2 user docs and sync table`**
   - README section, `TALEARR_UPSTREAM_SYNC.md` hooks, `TALEARR_UNRAID_SWAP.md` rollback notes
     (talearr.db, rename preview), release as Talearr 0.12.0.

Outside this repo, after step 5 is released:

10. **AllSeerr**: Talearr detection, `recording` request option, `externalServiceIdAudioAlt`, status
    from the recordings API, NoBookEnds contract fields.
11. **NoBookEnds**: group ABS items by identity, Recordings switcher, "Get the unabridged audiobook"
    on the abridged note.
12. **Phase 3**: listening position across recordings through the ebook alignment.

## Open questions

- Default marketplace: `us` for everyone, or follow the UI language's country where it has one?
- Should "Add the unabridged version" search right away by default (assumed yes), and should
  AllSeerr requests for a second recording need approval like a new book?
- When the unabridged recording is downloaded, keep the abridged one forever, or offer "remove the
  abridged recording" after a while (not automatic)?
- Is Audnexus acceptable as a fallback provider, given it is a volunteer server?
