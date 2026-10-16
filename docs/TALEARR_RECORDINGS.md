# TaleArr recordings

TaleArr 0.12.0 can keep more than one recording of the same audiobook: the abridged and the
unabridged reading, or two narrators, each with its own files and folder. It knows which
recording is which from Audible, and its upgrades never replace one recording with another.

The design and the reasons behind it are in
[TALEARR_RECORDINGS_DESIGN.md](TALEARR_RECORDINGS_DESIGN.md). This page is for using it.

## What a recording is

A recording is one audiobook entry of a book together with its edition and files. The first
one is the **primary** recording (the book itself). Every other one is an **additional**
recording: one of Chaptarr's own "wanted copy" entries, the same kind its narrator picker makes.
Because they are ordinary Chaptarr entries:

- each has its own edition, files, searches and upgrades,
- they show as separate entries in the book lists (as copies always have),
- stock Chaptarr understands them after a rollback.

Nothing new is stored in `chaptarr.db`. What TaleArr learns from Audible lives in a separate
cache, `talearr.db`, next to it (see [Storage](#storage)).

## The book page

Audiobook pages have a **Recordings** section above the History, Search and Files tabs:

| Column | What it shows |
|---|---|
| Narrator | The narrator(s), from Audible when known, else the edition, else the file tags. Additional recordings link to their own page; the primary one is marked "primary". |
| Abridged | **Unabridged**, **Abridged** or **Unknown**, and where the answer came from: Audible, the edition's metadata (title or edition info), the runtime against the other editions, or the files' length. Hover for the reason. |
| Runtime | Audible's runtime, else the edition's, else the files'. |
| Marketplace | The Audible store in use and the product's ASIN, linked to its Audible page. |
| Edition | Publisher and year. |
| Files | Downloaded (with format, bitrate and file count), Missing or Not monitored. |

**Refresh** looks the book's recordings up on Audible again, ignoring the cache.

**Add another recording** offers:

- **Unabridged version** and **Abridged version**: TaleArr picks the best edition of that kind
  (abridged status known, same language, Audible match, runtime known, popularity). Disabled,
  with the reason, when you already have it or no such edition is known.
- **Another narrator...**: pick from the narrators that are not a recording yet.
- **Pick an edition...**: every audiobook edition that is not a recording yet, with near
  identical ones folded together.

The new recording is searched right away (Settings > Metadata > Search when added). When the
primary recording is abridged and an unabridged edition is known, the section also shows
"You have the abridged recording. Add the unabridged one?" with an **Add and search** button.

**Remove this recording** (the bin icon) asks first and can delete the recording's files too.
Only additional recordings can be removed; for the primary recording the dialog explains that
it is the book itself and points at Delete. TaleArr never removes a recording on its own.

## Folders

Every recording gets its own book folder. When the naming format would give an additional
recording the same folder as an earlier one (two Scott Brick readings with
`{Book Title}{ - Narrator}`), TaleArr adds the first thing that tells them apart, using the
normal naming tokens:

1. the narrator, when the format does not already include it: `The Firm (D.W. Moffett)`,
2. abridged or unabridged: `The Firm - Scott Brick (Abridged)`,
3. the edition year, then the Audible ASIN: `The Firm - Scott Brick (2014)`,
4. else `(Copy 2)`, `(Copy 3)`, ...

A folder that already holds another recording's files counts as taken. The primary
recording's folder never changes, and a layout without book folders is left alone. An import
that would still land in a folder holding another recording's files is refused, with a
"TaleArr:" reason in the queue.

## Upgrades

With the **Upgrade guard** on (the default), an audiobook download only replaces a recording's
files when all of this holds:

1. **Same recording.** Same abridged status when both are known (the release name or download
   folder against the recording's answer), not longer by a whole abridgement (the files under
   60% of the download's length), narrator not contradicting, and not shorter than the
   **Runtime tolerance** (3%).
2. **Never worse.** Not fewer audio channels and not a lower bitrate (more than 5% lower), even
   when the quality profile ranks the new format higher.
3. **Better.** Higher in the quality profile, a higher revision or a higher custom format score;
   or, at the same quality, at least the **Minimum bitrate gain** (16 kbps) more in the same
   codec. A bitrate gain across codecs (128 kbps mp3 against 64 kbps aac) does not count. One
   chaptered M4B replacing many files at the same bitrate counts as better.

Bitrate, channels and length are measured from the files themselves, because Chaptarr does not
store them for imported files. Anything that cannot be measured never blocks an upgrade.
Manual imports and forced grabs are not checked. When the guard keeps the old files, the import
shows the reason, for example
"TaleArr: kept the existing files, the download is 64 kbps lower (64 kbps aac against 128 kbps mp3)".

**Keep both recordings** (on by default): an unabridged download for a book whose files are
abridged is imported as its own recording, an additional recording of the best unabridged
edition (an existing one is reused), instead of replacing the abridged files. With it off, such
a download is refused and the abridged files stay.

## Per-book abridged policy

The grab policy's **Abridged audiobooks** setting (Settings > Indexers > Grab policy, see the
[README](../README.md#grab-policy)) is the default for every book. A book can have its own value,
set through the API below. AllSeerr does this when its "Audiobook versions" setting is "requester
chooses"; its other choices (unabridged, abridged, both) map onto the same values or onto an
additional recording.

| Value | Releases | Ranking |
|---|---|---|
| `unabridgedOnly` | Releases marked abridged are skipped. As the global value they are still allowed when the monitored edition is itself abridged; as a book's own value that exception does not apply. | Abridged last. |
| `abridgedOnly` | Releases marked unabridged are skipped. Unmarked releases (most of them) are allowed only when the monitored edition is abridged. Releases marked abridged are always allowed. | Abridged first, then unmarked, then unabridged. |
| `preferUnabridged` | All allowed. | Abridged last, or first when the monitored edition is abridged. |
| `any` | All allowed. | No abridged step. |

Skipped releases are a soft filter labelled "Abridged" in interactive search, so you can still
grab one on purpose; the reason ends in "for this book" when a book's own value decided.
"Abridged" for a release means its name says so (`AbridgedReleaseDetector`); for the monitored
edition it is the recordings answer (Audible, then the edition's title or info, then its runtime,
see [The book page](#the-book-page)).

Which value applies:

- the book's own value when it has one, else the global value,
- additional recordings never take a book's value and keep their own behaviour: an abridged
  recording searches abridged. Under a global `abridgedOnly` an unabridged recording searches
  unabridged only and a recording of unknown abridgement takes either,
- ebooks are not affected.

The same value decides Audiobookshelf awareness: under `abridgedOnly` only an abridged copy in
Audiobookshelf counts as owned. The automatic retry after a failed download searches with the
book's value, and its history note says so ("...; abridged policy for this book: abridged only").

### Edition choice

Before a book search (missing book searches, Search, a retry after a failed download, an
interactive search of a monitored book, a search queued by AllSeerr), TaleArr checks the
monitored edition against the value that applies:

- `abridgedOnly` (the book's own or global) and the monitored edition is not abridged: TaleArr
  monitors the best abridged edition of the book, chosen the way **Add another recording >
  Abridged version** chooses (abridged status known, same language, Audible match, runtime
  known, popularity), and searches for it,
- a book's own `unabridgedOnly` and the monitored edition is abridged: the best unabridged
  edition, the same way.

The edition is pinned with Chaptarr's own wanted edition pin (the one the narrator picker uses),
and the book's history gets a "TaleArr: monitored edition switched to the abridged edition ..."
entry. When no matching edition is known, nothing changes: the search runs, finds nothing that
fits (only a release named abridged would), and history says "TaleArr: no abridged edition of
this book is known, so the search can only find releases marked abridged (...)", at most once a
day. History entries use the existing Download Ignored type with the text as the message, so
Chaptarr shows them after a rollback.

Not switched: books that already have files (the files stay, and the upgrade guard never
replaces one reading with the other), additional recordings, unmonitored books, and author-wide
searches (they apply the release rules above but keep the monitored editions). A switch is not
undone when the value changes back; pick another edition on the book page if you want one.

## Settings

Settings > Metadata > Recordings:

| Setting | Default | Config row |
|---|---|---|
| Audible marketplace | United States | `talearr.recordings.marketplace` (`us`, `uk`, `ca`, `au`, `fr`, `de`, `it`, `es`, `jp`, `in`) |
| Audible facts from | Audible catalog | `talearr.recordings.provider` (`audible`, `audnexus`, `off`) |
| Audnexus fallback | Off | `talearr.recordings.audnexusfallback` |
| Background lookups | Monitored recordings | `talearr.recordings.enrich` (`monitored`, `all`, `off`), every 12 hours |
| Search when added | On | `talearr.recordings.searchonadd` |
| Keep both recordings | On | `talearr.recordings.keepboth` |
| Upgrade guard | On | `talearr.upgrade.guard` |
| Minimum bitrate gain | 16 kbps | `talearr.upgrade.minbitrategainkbps` (0 to 320) |
| Runtime tolerance | 3% | `talearr.upgrade.runtimetolerancepct` (0 to 50) |

The Audible catalog API is unofficial and needs no account. TaleArr batches lookups, waits at
least a second between requests, caches answers for 30 days (7 for "not found") and only looks
up recordings you have. Audnexus (`api.audnex.us`) is a community run server, so it is only used
when you choose it or turn on the fallback.

## API

For AllSeerr, NoBookEnds and scripts (all additive; nothing upstream changes):

| Route | What it does |
|---|---|
| `GET /api/v1/talearr/recordings?bookId=` (or `providerId=hc:...`) | The recordings and candidate editions of a book. `bookId` may be any entry of the book, ebook included. |
| `POST /api/v1/talearr/recordings` | `{"bookId": 3401, "kind": "unabridged" \| "abridged" \| "narrator" \| "edition", "narrator": ..., "editionId": ..., "search": true, "requestedBy": "allseerr", "externalRef": "req-812"}`. `201` created, `200` already there, `404` nothing fits, `409` several fit (pick one with `kind: edition`), `400` bad input. |
| `DELETE /api/v1/talearr/recordings/{bookId}?deleteFiles=false` | Removes an additional recording. The primary one is refused with `409`. |
| `POST /api/v1/talearr/recordings/refresh?bookId=` | Refetches Audible facts for the book's recordings (`202`). |
| `GET\|PUT /api/v1/config/talearrrecordings` | The settings above. The upgrade fields are optional on `PUT`; leaving them out keeps the current values. |
| `GET /api/v1/talearr/grabpolicy/book/{bookId}` | A book's abridged policy: `{"bookId": 3401, "abridgedPolicy": "abridgedOnly", "effectivePolicy": "abridgedOnly", "source": "book"}`. `abridgedPolicy` is the book's own value or `null`; `source` is `book` or `global`. `bookId` may be any entry of the book (the ebook entry answers for its audiobook). `404` unknown book, `400` a book with no audiobook. |
| `PUT /api/v1/talearr/grabpolicy/book/{bookId}` | `{"abridgedPolicy": "unabridgedOnly" \| "abridgedOnly" \| "preferUnabridged" \| "any" \| null}`; `null` clears the book's value. Answers like `GET` (`200`); `400` for another value (numbers included) or a book with no audiobook, `404` unknown book. The edition switch happens on the next search, not on `PUT`. |
| `GET\|PUT /api/v1/config/talearrgrabpolicy` | The global grab policy; `abridgedPolicy` also takes `abridgedOnly`. |

Local ids in responses (`bookId`, `editionId`) are handles; store `baseBookId`,
`foreignEditionId` or the ASIN instead.

## Storage

- `talearr.db` in the config folder (next to `chaptarr.db`): Audible products, which edition
  matched which product, and which recordings were requested by whom. A rebuildable cache:
  delete it and TaleArr fetches again. Chaptarr never opens it.
- Per-book abridged values, in the `BookGrabPolicies` table of `talearr.db` (schema version 3).
  They are keyed by the work, the book's `BaseBookId` (`hc:378630`), the key the recordings use
  for a work: it comes from the metadata server, is shared by the book's audiobook and ebook
  entries and any duplicate entry, and stays the same when the book is refreshed or its entry is
  recreated. An entry without a `BaseBookId` falls back to `book:<id>` (none on a normal
  library). The monitored edition's key and the entry id are kept beside it for reference only.
  Losing `talearr.db` only loses these values: the books follow the global policy again.
- `talearr.*` rows in the existing `Config` table for the settings. Chaptarr ignores them.
- Additional recordings are Chaptarr's own copy entries; no new tables or columns, no
  migrations, `config.xml` untouched.

## Rolling back to Chaptarr

See [TALEARR_UNRAID_SWAP.md](TALEARR_UNRAID_SWAP.md#rollback). In short: swap the image back.
Chaptarr keeps every recording as an ordinary copy and searches and upgrades each on its own,
without the upgrade guard. Before you run Chaptarr's "Rename files", preview it: Chaptarr does
not know the folder rule above and may propose moving a disambiguated copy folder back into the
primary recording's folder.
