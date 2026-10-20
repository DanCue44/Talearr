# Talearr series monitoring

A series is its own page in Talearr. Monitor it and Talearr monitors and searches every book in
it, now and whenever a new book appears, as audiobooks, ebooks or both.

## Where

- **Library > Series** lists every series in the library (Chaptarr keeps one series row per
  media type; Talearr shows them as one), with how many books it has, how many are downloaded
  per format, and what Talearr monitors. "Monitored series only" narrows the list.
- **The series page** (`/talearr/series/<id>`, either media type's series id works) shows the
  books in order (1, 2, 2.5, 3; box sets by their range), each with its audiobook and ebook
  status (Downloaded, Missing, Not monitored, Author paused, Not in library), links to the book
  pages and the number of additional recordings.
- **Author page > Series tab**: the list icon on each series header opens its series page.

## Monitoring a series

Tick **Audiobooks**, **Ebooks** or both and save. Talearr then:

1. monitors every main book of the series in those formats (the audiobook row and/or the
   ebook row of each book),
2. searches for the ones without files (when **Search when books are added** is on, the
   default),
3. does the same again for every book that appears in the series later. New books arrive with
   the normal author refresh; Talearr applies the series rules after every author refresh and
   every 6 hours (System > Tasks > Apply Series Monitoring), and **Check for new books** does it
   at once.

What it does not do:

- **It never turns monitoring off.** A rule acts on each book and format once. If you unmonitor
  a book afterwards it stays unmonitored, and stopping a series (untick both formats) leaves
  every book as it is.
- **Box sets and omnibus editions** (`1-3`, `1-6`, "Box Set", omnibus books) are skipped unless
  Settings > Indexers > Grab policy > Allow bundles is on, so the series does not grab bundles
  your grab policy would refuse anyway.
- **Novellas and extras** (entries the metadata does not count as main books, or fractional
  positions such as 2.5) are skipped unless **Include novellas and extras** is ticked.
- **Additional recordings** (a second narrator, the abridged reading; see
  [TALEARR_RECORDINGS.md](TALEARR_RECORDINGS.md)) keep their own monitoring. A rule only touches
  each book's primary row per format.
- **Books by authors Talearr does not have.** A series that continues with a co-author's book
  picks it up once that author is in the library. Use Add New > series search to add the author.

Downloads go through the normal search, so the grab policy (unabridged only, bundles, failed
download retries) and the recordings upgrade guard apply exactly as for any other book.

### Author paused

Chaptarr searches a monitored book only when its author is monitored for that format (the
Audiobook or eBook switch in the author's edit dialog). When it is not, the series page shows
"Author paused" for those books and offers **Monitor audiobooks for <author>**. That button
turns on only the author's format switch: no other book of the author is monitored and
"monitor new items" is left as it is (the Phase 1 rule). The books the rule monitored while the
author was paused are searched then.

A book without a root folder for the format is reported and tried again on the next run.

## Settings and storage

Each monitored series is one row in the existing `Config` table, key
`talearr.series.monitor.<series key>`, holding JSON: the formats, Search when books are added,
Include novellas and extras, when it was added and last applied, the last result, and which
books the rule has already acted on per format. The series key is the provider series id
(`gr:61743`), else the base series id, else the title. Nothing in `chaptarr.db`'s schema
changes, there are no migrations and `config.xml` is untouched. The rows are user choices, so
they are not in the rebuildable `talearr.db`.

The scheduled task is a `ScheduledTasks` row like Talearr's other tasks.

## API

All additive under `/api/v1/talearr/series`; upstream's `/api/v1/series` is unchanged.

| Route | What it does |
|---|---|
| `GET /api/v1/talearr/series?monitoredOnly=false` | Every series (one entry per series key) with counts and its rule. |
| `GET /api/v1/talearr/series/{seriesId}` | The series page: books in order with per-format status. |
| `PUT /api/v1/talearr/series/{seriesId}` | `{"audiobook": true, "ebook": false, "searchOnAdd": true, "includeExtras": false}`. Both formats false stops monitoring the series. Returns the page. |
| `POST /api/v1/talearr/series/{seriesId}/apply` | Applies the rule now (`409` when the series is not monitored). |
| `POST /api/v1/talearr/series/{seriesId}/authors` | `{"format": "audiobook"}`: turns the format on for the series' authors whose format switch is off. |

## Rolling back to Chaptarr

Chaptarr ignores the `talearr.series.monitor.*` rows and drops the scheduled task row on start,
so nothing breaks. Books the rules monitored stay monitored (they are ordinary monitored books),
and author format switches turned on from the series page stay on. What stops is the
automation: new books in a series are no longer monitored or searched on their own. Rolling
forward again picks the rules up where they were.
