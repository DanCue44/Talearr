# TaleArr matching and edition fixes

TaleArr changes how editions are picked, how release titles are matched and how Audible
duplicates are handled. None of it changes the database schema, the API routes or fields, or
`config.xml`, so Chaptarr can still run on the same database.

## Settings and storage

No new setting. The preferred edition language is the existing UI language
(`Settings > UI > UI Language`, stored by upstream as the `UILanguage` key in the existing
`Config` table). It defaults to English. Metadata profile `Allowed Languages` still filters
editions first. Nothing is written by these changes except the normal edition `Monitored` flags
and book rows that upstream refresh already writes.

## Edition choice (`EditionSelector`, `TalearrEditionPreference`)

- Editions are ranked by language before popularity: preferred language first, then unlabelled
  editions whose title matches the work title, then other languages. A popular German "Die Akte"
  no longer beats the English "The Pelican Brief" audiobook.
- The work title comes from the book (`Title`, `OriginalTitle`) and from editions already labelled
  with the preferred language. Format words like "(Audiobook)" or "(Kindle Edition)" are ignored.
- When every native-format edition is labelled with a foreign language, a preferred-language
  ebook or print edition is monitored instead (an audiobook not yet released in English).
- Fixed: new editions all have Id 0, and the single-monitored repair compared ids, so every new
  edition was monitored at once. Later refreshes then kept the lowest row id, often a foreign one.
  Repair now picks on merit when several editions are monitored.
- On refresh, an automatically chosen edition in a foreign language is replaced by the preferred
  language edition when the book accepts any edition, no edition has files and none was added
  manually. An edition picked in the UI sets `AnyEditionOk = false` and is left alone.

## Edition switch (`BookController`)

`PUT /api/v1/book/{id}` returned a 500 ("Sequence contains more than one matching element") for
books with several monitored editions. It now accepts the change, keeps the edition the user just
turned on and stores exactly one monitored edition.

## Release title matching (`ReleaseTitleMatchScorer.Talearr.cs`)

Extra title variants are tried: without the subtitle, without "A Novel", with the leading article
moved ("Pelican Brief, The") or dropped ("Love Hypothesis"), without a series prefix
("Mistborn: The Final Empire" as "The Final Empire") and without a series bracket
("Skyshade (Lightlark, #3)"). Guards against matching a different book by the same author:

- a short form shared with another book in the author's catalog is not used;
- after a subtitle-less match only format tags, a year, the author, the series or the start of
  the real subtitle may follow; anything else is `SubtitleMismatch`;
- one-word short forms are only used when the dropped part is series information, so
  "Dune: The Duke of Caladan" never matches a plain "Dune" release;
- multi-level titles ("Pocket Potters: Harry Potter: ...") keep their full form;
- a series name followed by "Series" ("Bride Series 02 Mate"), or a title followed by
  "Series N" where N is not this book's position ("The Firm Series (2)"), is `SeriesNameOnly`;
- another book's title just before the matched title, joined by a few plain words
  ("The Exchange: After the Firm"), is `SiblingTitleContradiction`. Numbers or series words in
  between ("Lightlark Series 03 - Skyshade") are a series prefix and still match.

Automatic and interactive search use the same rules. When they seem to disagree, check which
edition was monitored at the time: searches use the monitored edition title, and The Firm's
changed from "The Firm: A Novel (The Firm Series Book 1)" to "The Firm" between the automatic
runs and the interactive one.

Upstream tests that required subtitle-less releases to be rejected were changed to the TaleArr
behavior and are marked `TaleArr:`.

## Audible duplicates (`TalearrCatalogDedupe`)

During add and refresh, a remote book that only has an Audible (`az:`) identity is folded into the
book of the same media type with the same work title, and its editions are added there. It is kept
separate when no such book exists or when several books could be the target. The local duplicate
row is then removed by the normal refresh prune, which keeps rows with files or a manual add.

An Audible row only folds when its title is the work title or the work title without its
subtitle; one that adds its own subtitle ("Animal Farm: Adapted for Children") stays separate.

Import: when a download was grabbed for a book and the file matcher picks an Audible-only
duplicate of it, the import goes into the grabbed book (its monitored edition if the duplicate's
edition is not under it yet) instead of being blocked.
