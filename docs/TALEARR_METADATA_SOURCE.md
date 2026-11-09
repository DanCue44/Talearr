# Talearr metadata source (Hardcover)

Talearr's own metadata source,
built on Hardcover, so Talearr no longer depends on Chaptarr's server
(`https://api2.chaptarr.com`).

**Status: Phase 3b.** The source can be selected (see "The switch" below); the default is still
the metadata server. The shadow comparison reads Hardcover and the server side by side and never
writes an author, book, edition, series or sync row. No database migration, no chaptarr.db change; the only stored value
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

## Checked against the live API (2026-10-20)

Run with a real key (Free plan, scopes `read:catalog`, `read:catalog:data`, `read:catalog:search`)
using the client's own four queries on Ali Hazelwood (`hc:234021`). All four queries ran with no
GraphQL errors, so every field below is readable with a catalog-only key.

| # | Result |
| --- | --- |
| 1 | Holds. `alternate_names` is an array; `identifiers` holds arrays (`audible`, `goodreads`, `openlibrary`). |
| 2, 3 | Hold. The nested `contributions` filter works. Values seen: `Author` and `Contributor` (anthologies such as *From a Certain Point of View*); `Contributor` is not a primary role, so those works are skipped. Note `books_count` (32) counts primary works only, while the books query returns every credited book (a full page of 100). |
| 4 | Holds; all fields are readable. |
| 5 | Holds, with extra keys: `Tag`, `Mood`, `Genre`, `Group`, `Content Warning`. Only `Genre` and `Mood` are read. |
| 6 | Holds: `position` 1, `details` "1", `featured`, `compilation`. |
| 7 | Holds: a full page of 100 editions came back. |
| 8 | Seen: 1 Physical, 2 Audio, 4 Ebook. Data is noisy: "Kindle Edition" also appears under 1 (Physical). |
| 9 | Holds. Contributors also carry `contributor_role_name` and `primary`. |
| 10, 11 | Hold. |
| 12 | Holds: `RateLimit: "Free";r=8;t=0, "daily";r=4997;t=38394`, plus `RateLimit-Policy` and `X-RateLimit-Daily-*`. |
| 13 | Dates are `YYYY-MM-DD`. `updated_at` comes both with an offset (`+00:00`) and without one; both are read as UTC. |
| 14 | Readable: `book_mappings { external_id platform { name } edition_id }` with Goodreads, Google and StoryGraph ids. Useful for Phase 3b matching of server rows that only have Goodreads ids. |

Keys: Hardcover keys are now scoped and can be set to never expire. A catalog-only key gets a 403
from `me`; Settings validates it with a catalog query instead (0.18.12). Hardcover reading lists
(import lists) need a key with library access.

## First shadow run on a test server (2026-10-20)

All 19 authors compared in about 3 minutes with 177 Hardcover requests; none failed.
**No protected book (monitored, with files, manual add) would lose its match.** 598 of 1,185
local rows would lose their match, all unmonitored and fileless.

Three kinds of difference:

1. **Clutter the server has and Hardcover does not** (the main effect). George Orwell: the server
   keeps 154 audiobook / 140 ebook works after filters, Hardcover 57 / 55; the server-only list is
   graded readers, translations, bilingual editions, quote books and "10 Masterpieces" bundles.
2. **Real gaps in Hardcover.** Jack Rosewood: the server has 66 works, Hardcover links 5 to the
   author; the missing ones are real books (the "True Story of..." series). Ryan Becker (26 vs 4),
   Sandra J. Paul (9 vs 2) and Navessa Allen (18 vs 9) look the same. Switching needs the Open Library
   gap fill (Phase 3b step 5) or these authors lose books.
3. **Id splits.** The same title can carry different work ids on each side (Orwell's "1984"
   appears on both sides of the diff), so some rows lose their match and come back as new rows.
   `book_mappings` (Goodreads ids) and title matching can close most of these.

Series: Hardcover sent series for 14 authors, all dropped by the conversion as expected (step 4).

## Schema assumptions (as first written)

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

## Closing the gaps (0.18.13)

Looking closer at the thin authors, Hardcover mostly has the books; the metadata profiles drop
them. Jack Rosewood has 49 works on Hardcover (5 survive the filters), Ryan Becker 15 (4), Navessa
Allen 28 (9). The server's rows carry Goodreads ratings and dates; Hardcover's records for small
and self-published authors usually have no release date on the book or any edition and no
ratings, only readers (`users_count`). The test profiles skip missing dates and need a popularity of
at least 1 (rating times number of ratings), so those books fall out.

- **Readers count as popularity.** `Ratings.Readers` (Hardcover `users_count`, stored inside the
  Ratings JSON, so no schema change and Chaptarr ignores it on a rollback) passes the minimum
  popularity rule when it reaches the minimum. Rows from the metadata server have 0 readers, so
  nothing changes for them.
- **Release dates from Open Library.** `TalearrSourceGapFill` runs only when a book has no date on
  the book, its year or any edition: one Open Library author lookup (skipped when Hardcover links
  the author) and one works list (`search.json?author_key=`, up to 200 works), then a match by
  normalized title, then by the part before the subtitle. Only a single unambiguous year is used.
- **Hardcover series are kept** for payloads from Talearr's source (`V5AuthorResponse.TalearrSource`);
  payloads from the metadata server keep the Goodreads-only rule, so the live path is unchanged.
- **The shadow report says why rows were filtered**: `filterReasons` per side
  ("Book: release date is missing" -> count) and `datesFilled` on the Hardcover side.

Id matching: Hardcover's `book_mappings` Goodreads ids are Goodreads *book* (edition) ids, while
the server's rows carry Goodreads *work* ids (`GoodreadsWorkId`), so they cannot close the id
splits directly. Rows that exist only on the server with a Goodreads id and no Hardcover id are
books Hardcover does not have at all.

## Second shadow run (0.18.14, 2026-10-20)

19/19 authors in 3.5 minutes, 189 Hardcover requests, no failures, **no protected book lost**.
Rows that would lose their match: 598 -> 473 of 1,185 (all unmonitored and fileless).

| Author | Server works | Hardcover before | Hardcover now | Dates from Open Library |
| --- | --- | --- | --- | --- |
| Jack Rosewood | 66 | 5 | 41 | 27 |
| Ryan Becker | 26 | 4 | 11 | 1 |
| Navessa Allen | 18 | 9 | 16 | 2 |
| Sandra J. Paul | 9 | 2 | 4 | 0 |
| George Orwell | 154 | 57 | 86 | 0 |
| John Grisham | 69 | 72 | 96 | 0 |

Hardcover series are now kept (174 rows across 13 authors). What is left: Jack Rosewood's
server-only works are mostly coloring and puzzle books and bundles; Ryan Becker's 18 server-only
works are not on Hardcover at all, so only an Open Library works gap fill (adding titles, not just
dates) would bring them back. Sandra J. Paul's are mostly undated on both Hardcover and Open Library.

## The switch (Phase 3b, built)

With `talearr.metadata.source` set to `talearr` and a Hardcover token saved, `BookInfoProxy`
serves Hardcover ids itself (`TalearrSourcePayloads`, `TalearrSourceMatcher`):

| Call | With Talearr's source |
| --- | --- |
| `GetAuthorInfo("hc:N")` (add author, single-book add, pending imports, series import) | Hardcover catalog, mapped, Open Library dates, same V5 conversion |
| `RefreshAuthorInfo("hc:N")` | Signature check first; full read only when it changed |
| `GetBulkAuthorChanges` (scheduled refresh) | Hardcover ids: one `authors` request per 100 authors; other ids still go to the server |
| `GetWorkInfo` / `GetBookInfo("hc:N")` | The book's primary author is read (cached) and the book picked from it |
| `GetEditionInfo("hc:edition:N")` | Edition to book, then as above |
| Import matching (`SearchV5Matching`) | ISBN / ASIN from the tags as Hardcover editions, then Hardcover's book search |

- **Signatures in place of ETags**: `hct-{books_count}-{updated_at ticks}-w{week}`. Adding a book
  to an author changes `books_count`; editing the author changes `updated_at`. Book and edition
  edits change neither, so the week number (offset per author, one seventh of the library a day)
  forces one full read per author per week.
- Authors refresh by their Hardcover id (`GetRefreshAuthorIdentifier`); the bulk check sends only
  that id for them. Authors without a Hardcover id keep using the server.
- An author Hardcover does not return is an error, never a deletion.
- The shadow comparison's server side always reads the server (`GetServerAuthorInfo`).
- Cost on a test server (19 authors): about 1 request per scheduled check plus about 9 per author
  that changed; the weekly full read is about 25 requests a day.

## Audible (0.18.60)

Hardcover is thin for many audiobooks: no audiobook edition, books left out of their series,
books listed under a co-author only (Talearr #7: Symbiote Wars 1 to 3 by J.N. Chaney and Chris
Kennedy). After the Open Library date fill, `TalearrSourcePayloads.GetAuthor` adds the author's
Audible catalog to the payload (`TalearrAudibleFill`, `TalearrAudibleMerge`).

- **Catalog** (`TalearrAudibleAuthorCatalog`): `catalog/products?author=` through the recordings
  client (`TalearrAudibleClient.SearchAuthor`, one request a second, back-off on 429/503), up to
  20 pages of 50, newest first, in the recordings marketplace. Kept: products that credit the
  author (or a pseudonym) as an author, `SinglePartBook` / `MultiPartBook`, in the marketplace's
  language. A read is reused for 3 days and the last good one is kept in talearr.db
  (`AudibleAuthorCatalogs`, schema 12).
- **Matching**: by ASIN, then the full title, then the same work title
  (`TalearrTitleNormalizer.IsSameWorkTitle`) unless the book holds another number in a series the
  listing names, then the same main title in the same series place. A series label in Audible's
  subtitle or in brackets ("Symbiote Wars, Book 4", "(The Forgotten Ship Book 8)") is not part of
  the title. Collections ("Books 1-3", box sets) never match a single book.
- **Fill** (every author, Settings > Metadata > Recordings > Fill gaps from Audible, on by
  default, `talearr.metadata.audiblefill`): a book with no audiobook edition gets Audible's
  editions (`az:{ASIN}`, `-audiobook` appended); a book in no series gets Audible's series and
  number; an empty number in the same series is filled; a lone audiobook edition without an ASIN
  gets Audible's when a narrator agrees. Hardcover's values are never replaced and no book or
  series is added.
- **Audible leads** (per author, Edit > Audiobook details from > Audible,
  `talearr.audiobooksource.hc:N` rows): matched books also get Audible's editions, ranked first for
  edition selection; Audible's series and numbers win; audiobooks Hardcover does not list for the
  author are added (`az:{ASIN}`, collections as omnibus books); series Hardcover does not have are
  added by their Audible id (`Series.AmazonSeriesAsin`). Saving a change queues a forced refresh.
  When Audible does not answer and no copy is stored, the refresh is skipped rather than dropping
  those books. Switching back to Hardcover removes the Audible-only books that have no files and
  are not monitored, like any book the source stops listing.
- `GetBookInfo` / `GetWorkInfo` of an `az:` id with a Hardcover author hint read that author's
  payload (single-book refresh). The stale-row reconciler takes old rows of the same title into
  an Audible-only book, so a row from the Goodreads days becomes that book.

### Series from Talearr's source

`RefreshSeriesService` used to keep Goodreads-backed series only, so no Hardcover series was
ever saved (series came only from rows made in the Goodreads days). Since 0.18.60 a series is
known by its Goodreads, Hardcover or Audible id (`SeriesProviderKey`). A Goodreads series of the
same title for the author takes the Hardcover id instead of a second series appearing next to it,
and Goodreads series the source does not list are kept. The mapper drops Hardcover series named
only "Book 1", "Volume 2" and the like.

## Phase 3b (next)

1. **Switch refresh.** When `talearr.metadata.source` is `talearr`, `RefreshAuthorService` reads
   the Hardcover catalog in place of `RefreshAuthorInfo`, using `updated_at` per author and book
   in place of ETags and the `/api/v5/authors/diff` bulk check. Only after the shadow reports show
   no lost protected matches on a test server.
2. **Work and edition lookups.** `GetBookInfo` / `GetWorkInfo` / `GetEditionInfo` (single-book
   refresh, canonical id search) served from Hardcover `books_by_pk` / `editions` queries.
3. **Local import matching** replacing `/api/v5/match`: ISBN and ASIN from file tags against
   local editions, then title and author against local books, then a Hardcover search.
4. **Series from Hardcover.** Done in 0.18.13 for payloads from Talearr's source.
5. **Gaps.** Open Library for works and ISBNs Hardcover lacks; Audible audiobook editions and
   series done in 0.18.60 (see "Audible" above); chapters still to do; possibly
   `book_mappings` for Goodreads and Open Library edition ids.
6. **Health and settings test** against the active source; the metadata server probe only runs
   when the server is selected.
