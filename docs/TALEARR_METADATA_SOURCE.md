# Talearr metadata source (Hardcover)

Phase 3 of [TALEARR_METADATA_PLAN.md](TALEARR_METADATA_PLAN.md): Talearr's own metadata source,
built on Hardcover, so Talearr no longer depends on Chaptarr's server
(`https://api2.chaptarr.com`).

**Status: Phase 3a, shadow mode only.** The new source reads Hardcover and compares the result
with the metadata server. Nothing switches: refresh, add, search, single-book lookups and import
matching all still use the metadata server, and the comparison never writes an author, book,
edition, series or sync row. No database migration, no chaptarr.db change; the only stored value
is one `talearr.*` row in the Config table, which Chaptarr ignores (a rollback keeps working).

## Design

```
Hardcover GraphQL ──> TalearrHardcoverMetadataClient ──> HardcoverAuthorCatalog
                         (paged, rate limited, cached)
HardcoverAuthorCatalog ──> TalearrHardcoverMapper ──> V5AuthorResponse
V5AuthorResponse ──> BookInfoProxy.ConvertV5AuthorResponse ──> Author + Books (audiobook and ebook rows)
                     (the same ConvertV5AuthorToDomain the server's payloads go through)
```

The seam is the metadata server's own payload shape (`V5AuthorResponse`), the same one the Open
Library fallback (`Talearr/MetadataFallback/TalearrOpenLibraryMapper.cs`) uses. Everything after
the mapper is the existing pipeline: dual audiobook/ebook rows, edition selection,
`RefreshAuthorService.NormalizeRemoteBooks` (metadata profiles, import list exclusions, edition
retention), identity matching.

Code (all new, `src/NzbDrone.Core/Talearr/Metadata/Source/`):

| File | What |
| --- | --- |
| `TalearrHardcoverModels.cs` | Plain records of the Hardcover rows read (field names in comments) |
| `TalearrHardcoverRateLimiter.cs` | Token bucket plus Hardcover's `RateLimit` and `Retry-After` headers |
| `TalearrHardcoverMetadataClient.cs` | GraphQL client: author, books, editions, series; paging; in-memory cache |
| `TalearrHardcoverMapper.cs` | Catalog to `V5AuthorResponse` |
| `TalearrMetadataShadowComparer.cs` | Pure comparison logic (no I/O) |
| `TalearrMetadataShadowService.cs` | Loads local rows, reads both sources, runs the comparer; the command handler |
| `TalearrMetadataShadowModels.cs` | Report shapes and `TalearrMetadataShadowCompareCommand` |
| `TalearrMetadataSourceSettings.cs` | `talearr.metadata.source` setting |

Upstream files touched (each change marked `// Talearr:`):

- `MetadataSource/BookInfo/BookInfoProxy.cs`: implements the new
  `MetadataSource/ITalearrV5AuthorConverter.cs` (`ConvertV5AuthorResponse`, a public wrapper over
  the existing private `ConvertV5AuthorToDomain`; pure, no HTTP).

New elsewhere: `HealthCheck/Checks/TalearrMetadataSourceCheck.cs`,
`Chaptarr.Api.V1/Talearr/TalearrMetadataController.cs`.

### Ids

The mapper uses the id shapes the metadata server already emits for Hardcover data, so rows the
server created keep matching (`BookIdentity`, `BookEditionIdentity`, `ProviderIdHelper`):

| Entity | Id | Notes |
| --- | --- | --- |
| Author | `hc:{authors.id}` | `HardcoverAuthorId`; `canonical_id` is added to `provider_ids_all` |
| Work | `hc:{books.id}` | `V5Book.Id`, `BaseBookId`, `HardcoverBookId` |
| Edition | `hc:edition:{editions.id}` | `HardcoverEditionId` is the bare number; the conversion appends `-audiobook` / `-ebook` for `ForeignEditionId` |
| Series | `hc:{series.id}` | `V5Series.Id` and `HardcoverSeriesId`; members name works by `hc:{books.id}` |
| Narrator | `hc:{authors.id}` | from edition `cached_contributors` |

Hardcover duplicates (`book_status_id` 4 or a `canonical_id`) are not books of their own: their
ids go into the canonical work's `provider_ids_all`, so a local row created from the duplicate id
still matches. Duplicate editions (`canonical_id` set) are dropped.

### What is mapped

- **Works**: the author's books where the author has a primary contribution role
  (`HardcoverContributionRoles`, the rule search already uses). Editor, translator and similar
  credits are skipped, as are deleted rows (status 3) and `is_partial_book` rows.
- **Editions**: reading format, ISBN-10/13 (digits only), ASIN, language, release date, page
  count, audio seconds, narrators, publisher, cover, edition format and information.
- **Reading formats**: Hardcover `reading_format_id` 1 Physical, 2 Audio, 3 Both, 4 Ebook become
  V5 1 physical, 2 audio, 3 ebook. "Both" is mapped to physical; a missing id falls back to the
  `edition_format` text.
- **Omnibus**: `books.compilation` or a `book_series.compilation` membership. `book_category_id`
  8 (Collection) is read but not used: a story collection is not an omnibus.
- **Series**: name, description, book counts, positions (1, 1.5, or the `details` text). A member
  is a primary slot when its position is a whole number and it is not a compilation. The current
  conversion keeps only Goodreads-backed series, so Hardcover series reach the V5 payload but are
  dropped in conversion until Phase 3b (the shadow report shows both counts).
- **Ratings**: book `rating` and `ratings_count`. Editions have no ratings count; `users_count`
  stands in as the popularity signal edition selection ranks by.
- **Images**: author, book and edition `image { url }` (falling back to `cached_image.url`).
- **Genres / moods**: `cached_tags.Genre` and `cached_tags.Mood`, at most 10 each.
- **updated_at**: kept on every record; the newest one stamps the response (`hc-{ticks}`) for logs.
  Phase 3b uses it in place of ETags.

### Requests and limits

Hardcover's documented limits (Getting Started > Rate Limits, read 2026-10-20): 60 requests a
minute, a burst bucket of 10 (Free) or 15 (Supporter), and 5,000 (Free) or 50,000 (Supporter)
requests a day. Each top-level field costs one request. A depth limit of 3 is announced for 2026.

- Every query has exactly one top-level field (a test checks this) and nests at most two levels
  below it (`books { image { url } }`, `editions { language { code3 } }`), inside the depth limit.
- One author costs: 1 (author) + book pages + edition chunks + series chunks. Books are paged by
  id (`id > last`, 100 per page). Edition requests group books so each holds about 100 editions
  (from `books.editions_count`), at most 50 books per request, paged the same way. A typical
  author costs 4 to 8 requests; a prolific one (hundreds of books) 20 to 60.
- Hardcover does not document a page cap, so a short page is trusted as the last only once the
  server has returned a full page, or the expected count is reached; otherwise one more page is
  asked for. This costs at most one extra request per list and survives a cap below 100.
- The client keeps its own bucket (5 tokens, one every 1.1 s, about 55 a minute), lowers it to
  what `RateLimit` says is left, waits `t` seconds when the plan bucket is empty, and waits
  `Retry-After` on a 429 (up to 4 attempts). 5xx and 408 are retried; 401 and 403 are not.
- It stops (`TalearrHardcoverBudgetException`) when `RateLimit` says 200 or fewer requests are
  left today, so search, lists and import lists keep working.
- Catalogs are cached in memory for 6 hours (200 authors at most).

## Schema assumptions to verify with a real token

Built from the docs (hardcoverapp/hardcover-docs, schemas Books, Editions, Authors, Series,
BookSeries, Contributions, Languages, ReadingFormats) and the queries Talearr already runs. None
of this has been checked against the live API. With a token, run each query in
`TalearrHardcoverMetadataClient` (`AuthorQuery`, `BooksQuery`, `EditionsQuery`, `SeriesQuery`) in
the Hardcover GraphQL explorer and check:

1. `authors(where: { id: { _eq } }, limit: 1)` returns `alternate_names` as a JSON array of strings
   and `identifiers` as an object of string or string-array values (keys such as `goodreads`,
   `viaf`, `isni`, `openlibrary`, `amazon`). Unknown shapes are ignored, not fatal.
2. `books(where: { contributions: { author_id: { _eq } } })` returns the author's books, and
   `books.contributions` lists book-level credits only (not edition credits).
3. The nested `contributions(where: { author_id: { _eq: $authorId } }) { contribution }` filter is
   allowed on `books` and a null or empty `contribution` means author (as in search).
4. The fields `compilation`, `book_category_id`, `book_status_id`, `canonical_id`,
   `is_partial_book`, `editions_count`, `default_audio_edition_id`, `default_ebook_edition_id`,
   `default_physical_edition_id`, `cached_tags`, `users_count` are readable with a user token.
5. `cached_tags` is `{ "Genre": [{ "tag": "...", "count": n }], "Mood": [...] }`.
6. `book_series { series_id position details featured compilation }` is readable;
   `position` is a number (`float8`), `details` its text form.
7. `editions(where: { book_id: { _in } , id: { _gt } }, order_by: { id: asc })` works with
   `limit` 100, and Hardcover's page cap (if any) is at least 100. If it is lower the client still
   reads everything, at one extra request per list.
8. `reading_format_id` is 1 Physical, 2 Audio, 3 Both, 4 Ebook, and what "Both" means.
9. `editions.cached_contributors` is `[{ "author": { "id", "name" }, "contribution": "Narrator" }]`
   with narrators marked `Narrator`.
10. `language { code2 code3 language }` and `publisher { name }` and `image { url }` are allowed as
    one nested level under `editions`, inside the depth limit.
11. `series(where: { id: { _in } })` returns `books_count`, `primary_books_count`, `is_completed`,
    `canonical_id`.
12. The `RateLimit` header has the documented form (`"Free";r=8;t=0, "daily";r=4231;t=51234`) and
    `Retry-After` comes with a 429.
13. Dates come as `YYYY-MM-DD` (`release_date`, `born_date`) and ISO 8601 (`updated_at`).
14. Whether `book_mappings` (external platform ids on books and editions) is readable; not used
    yet (see Phase 3b).

A GraphQL error names the field; the client reports it in the shadow report and in
`GET /api/v1/talearr/metadata/source` (`hardcover.lastError`).

## Running the shadow comparison

Needs the Hardcover API token in Settings > Metadata (the existing setting). Authors without a
Hardcover id (`hc:`) are skipped.

One author, now (one metadata server read plus a few Hardcover requests):

```
GET /api/v1/talearr/metadata/shadow/{authorId}
```

The whole library (or some authors), as a command:

```
POST /api/v1/command
{"name": "TalearrMetadataShadowCompare"}
{"name": "TalearrMetadataShadowCompare", "authorIds": [12, 40], "maxHardcoverRequests": 500}
```

`maxHardcoverRequests` (default 1500) stops the run before it uses up a Free token's 5,000 a day.
The run logs one line per author (`[TALEARR-SHADOW] ...`, a warning when a protected book would
lose its match) and a summary, and keeps the last report in memory:

```
GET /api/v1/talearr/metadata/shadow
```

Each author report has, for each side (`server`, `hardcover`): book rows per media type before
and after the author's metadata profiles, distinct works, editions before and after, series kept
(and, for Hardcover, series sent), and how many local rows it matches. Then:

- `onlyOnServer` / `onlyOnHardcover`: works (after filtering) the other side has no stable work id
  for, with titles, ids and media types (counts complete, lists capped at 100).
- `lostMatches`: local rows the server matches today and Hardcover would not, protected rows
  (monitored, with files, or manual adds) first, each with a reason: `missing from Hardcover` or
  `filtered by profile` (Hardcover has it but the profile drops it, for example a compilation).
  This is the list that must be empty, or understood, before switching. `lostProtectedCount` is
  the headline number.
- `gainedMatchesCount`: local rows only Hardcover matches.

Matching follows refresh: `BookIdentity.FindWorkFirstMatchResult` per media type (stable work ids
first, edition ids only for rows without work identity; ambiguous edition matches count as no
match). It does not consume a local row once matched, so two remote pockets matching one row both
count.

The setting:

```
GET /api/v1/talearr/metadata/source
PUT /api/v1/talearr/metadata/source   {"source": "talearr"}   (or "chaptarr", the default)
```

Stored as `talearr.metadata.source`. In Phase 3a it only changes the System > Status notice
(`TalearrMetadataSourceCheck`), which says the source is in shadow mode.

## Phase 3b (next)

1. **Switch refresh.** When `talearr.metadata.source` is `talearr`, `RefreshAuthorService` reads
   the Hardcover catalog in place of `RefreshAuthorInfo`, using `updated_at` per author and book
   in place of ETags and the `/api/v5/authors/diff` bulk check. Only after the shadow reports show
   no lost protected matches on a test server.
2. **Work and edition lookups.** `GetBookInfo` / `GetWorkInfo` / `GetEditionInfo` (single-book
   refresh, canonical id search) served from Hardcover `books_by_pk` / `editions` queries.
3. **Local import matching** replacing `/api/v5/match`: ISBN and ASIN from file tags against
   local editions, then title and author against local books, then a Hardcover search.
4. **Series from Hardcover.** Accept Hardcover series ids in `ConvertV5AuthorToDomain` (today only
   Goodreads-backed series are kept).
5. **Gaps.** Open Library for works and ISBNs Hardcover lacks; Audible / Audnexus for audiobook
   editions, narrators, runtime and chapters (the recordings client exists); possibly
   `book_mappings` for Goodreads and Open Library edition ids.
6. **Health and settings test** against the active source; the metadata server probe only runs
   when the server is selected.
