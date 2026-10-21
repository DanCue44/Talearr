# Talearr author page groups

An author's catalog holds far more rows than real works. George Orwell has dozens of rows that
mention 1984: box sets, "1984 / Animal Farm", York Notes and SparkNotes, graded readers, the
graphic novel, Turkish and other translations, and the same novel listed several times by
different publishers. Talearr sorts each row into the author's **main works** or one kind of
**other listing**, and the author page shows the main works first.

## What you see

- **Library view** (the default): unchanged. It lists monitored books and books with files,
  and those are always main works.
- **All view**: the main works, in the page's sort order, as before. Below them a collapsed
  **Other listings (N)** bar. Opening it shows the rest in one table with the same columns, under
  small labels:
  - Collections and omnibus editions
  - Study guides and readers
  - Adaptations
  - Other listings of the same book
  - Other languages
- The bar remembers whether it is open, per browser (`talearrOtherListingsExpanded` in local
  storage, kept across version changes by `Utilities/versionGate.js`).
- Sorting, the search box, monitoring and the book editor work the same in both parts. A search
  that only matches other listings shows them in the count of the bar; open it to see them.
  Select all and shift-click ranges only reach rows that are on screen.
- Monitoring a book from the other listings moves it to the main works right away. Nothing is
  hidden from the API, removed, or written to the database.
- The Series tab is not grouped.

## Rules

`src/NzbDrone.Core/Talearr/Catalog/TalearrAuthorPageGroups.cs`, per author and per format
(audiobook and ebook rows are grouped separately). The first rule that applies wins.

1. **Kept rows are always main**: monitored for the format, with files, added by hand, or with a
   strict or hand-picked edition (the same test as "Only my books" uses).
2. **Title rules**, on the title the page shows:
   - `guide`: York Notes, SparkNotes, CliffsNotes, Coles Notes, MaxNotes, SuperSummary,
     GradeSaver, BookRags, "Study Guide/Notes/Companion", literature, reader's, teacher's and
     discussion guides, "Summary", "Analysis of", "... Notes on ..." (not at the start, so
     "Notes on Nationalism" stays a main work), workbooks, and graded readers ("Level 4: 1984",
     Penguin/Pearson/Macmillan/Cambridge Readers, Oxford Bookworms).
   - `omnibus`: the book's omnibus flag, "Collection", "Omnibus", "Box Set", "Books Set",
     "Complete/Collected Works" (and Novels, Stories, Fiction, Series), "N Books/Novels"
     ("3 Books", "Six Novels"), "Volumes 1-3" or "Books 1-3", and two titles joined by " / ".
   - `adaptation`: "Graphic Novel" (or adaptation, edition), manga, Classics Illustrated, comic
     adaptations. A plain "(Illustrated)" edition is not an adaptation; it is the same book.
3. **Same-book rows**: the remaining rows are grouped by a normalized title (lower case, accents
   removed, a leading author name such as "George Orwell 1984" or "George Orwell's 1984" dropped,
   anything in brackets dropped, subtitles like ": A Novel" or ": 75th Anniversary Edition" and
   words like unabridged, annotated, illustrated, edition dropped, a leading "The/A/An" dropped)
   and by shared Hardcover, Goodreads or Open Library work ids. In each group:
   - a row with a graphic novel, comics or manga genre next to a prose row is an `adaptation`
     (an author whose own works are graphic novels keeps them as main works);
   - a row in a known language outside the preferred languages is `otherLanguage` when the
     group has a row in a preferred or unknown language. Such a row also joins a group on its
     title before any bracket, colon or dash ("1984 (Resimli Yeni Ceviri) - Klasik" joins
     "1984");
   - one row is the representative and stays main: kept rows first, then the most rating votes,
     then the earliest release, then the lowest id. The others are `duplicate`. Kept rows stay
     main even when they are not the representative.

**Preferred languages** are the allowed languages of the author's metadata profiles, else the
languages of the kept rows, else the most common language of the author's rows. Without any
language data the language rule is off (the UI language is not used).

## API

`GET /api/v1/book?authorId={id}` (the author page list) adds a read-only `talearrGroup` to every
book: `main`, `omnibus`, `guide`, `adaptation`, `duplicate` or `otherLanguage`. It is computed
per request from the rows already loaded and is left out of every other book response (it is not
in SignalR book updates either; the page keeps the last value and treats a monitored book or one
with files as main). If the grouping fails, the list is returned without it and the page shows
every book as a main work.

## Storage and rollback

Nothing is stored. No schema change, no Config rows. Stock Chaptarr ignores the field.

## Limits and next steps

- The rules are title and word based. "Nineteen Eighty-Four" and "1984" are only linked when
  they share a work id. A novel whose title happens to match a rule (a novel called "The
  Collection") is shown under the other listings; monitor it to make it a main work.
- Phase 3b (the Talearr metadata source) can feed Hardcover's compilation flags and edition
  grouping into the same classifier.

Tests: `Chaptarr.Core.Test/Talearr/Catalog/TalearrAuthorPageGroupsFixture.cs` (rules, with the
1984 cases) and `Chaptarr.Core.Test/Api/BookControllerAuthorListFixture.cs` (the resource field).
