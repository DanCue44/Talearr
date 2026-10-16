# TaleArr audiobook naming, multi-disc import and M4B merge

Three Phase 2 additions for audiobooks. None of them changes the database schema, an existing
API route or field, or `config.xml`, so Chaptarr can still run on the same database.

## Naming tokens

Settings > Media Management > Book Naming, the `?` button next to Standard Book Format. The
modal now has an "Audiobook layouts" group (inserted as the whole format) and a "Recording"
group, and the Series, Narrator and Release Date groups list the new tokens with examples.

| Token | Example | Notes |
|---|---|---|
| `{Book SeriesPosition:00}` | `03` | Zero padded to the number of zeros (`:000` gives `003`). Decimals keep their fraction (`2.5` gives `02.5`); positions that are not a number (`1-3`) are left as they are. `{Book SeriesPosition}` without a format is unchanged. |
| `{Book SeriesPosition:00 - }` | `03 - ` | The usual optional prefix/suffix rules apply: no position, no text. |
| `{Book Year}` | `1994` | First published year, else the edition's year. Empty when unknown (unlike `{Release Year}`, which says `Unknown`), so `{Book Year - }` disappears cleanly. |
| `{ {Narrator}}` | ` {Ray Porter}` | Narrator in curly braces, which Audiobookshelf reads from the end of the book folder name. Narrator tokens now fall back to the narrator read from the file when the edition has none. |
| `{Abridged}` | `Abridged` | Only for an abridged recording, otherwise empty: `{ (Abridged)}`. |
| `{Abridgement}` | `Unabridged` | `Abridged` or `Unabridged` when the edition says so (title, format, edition info) or its runtime is under 60% of the longest edition in the same language; empty when unknown. Same rules as the grab policy. |

Narrator (`{Narrator}`, `{Narrators}`, `{Narrator Initials}`, ...) and series (`{Book Series}`,
`{Book SeriesTitle}`) tokens already existed in Chaptarr and are unchanged.

### Audiobookshelf layout

Audiobookshelf's preferred folder structure is `Author/Series/Vol 1 - 1994 - Title {Narrator}/files`.
Its scanner takes the series from the folder above the book, then reads a leading `1 - `,
`1. `, `Vol 1 - ` or `Book 1 - ` as the series position, a following `1994 - ` as the year and a
trailing `{...}` as the narrator. It does not read `(#1)`, so the preset uses the number form:

```
{Book Series}/{Book SeriesPosition:00 - }{Book Year - }{Book Title}{ {Narrator}}/{Book Title}{ (PartNumber:smart)}
```

With Author Folder Format `{Author NameFirstLast}` this gives
`Terry Goodkind/Sword of Truth/01 - 1994 - Wizards First Rule {Sam Tsoutsouvas}/Wizards First Rule (01).mp3`,
and `George Orwell/1949 - 1984 {Simon Prebble}/1984.m4b` for a book that is not in a series (the
empty series folder and position are dropped). Two simpler variants (no year or narrator, and no series folder) are in
the same list.

Storage: none. The tokens are part of the existing naming format strings
(`StandardBookFormat` in the `NamingConfig` table), which already hold any text.

## Multi-disc and multi-file import

Audiobooks split into `CD1`/`CD2`, `Disc 1`, `Part 01` folders or many mp3 files import as one
book, in playing order:

- **Order.** When every file has a track number tag and the (disc, track) pairs are unique, the
  tags decide; a missing disc tag is taken from the disc folder, so per-disc track numbers work.
  Otherwise the path decides: disc folders by their number (`CD2` before `CD10`, `Disc Two` before
  `Disc Three`, `Part IX` before `Part X`, `Side A` before `Side B`), loose files next to disc
  folders first, then a natural sort (`Track 2` before `Track 10`). Only folders below the files'
  common folder count, so a book folder named "... Part Two" does not matter.
- **Same file names on different discs.** `Part 1/Chapter 1-2.mp3` and `Part 3/Chapter 1-2.mp3`
  used to land on the same name in the book folder when renaming was off (the default), so one
  overwrote or skipped the other. This happened on the live library: the 1984 download in
  `Orwell G. - 1984 (West S.) - 2010 (96kbps)/Part 1..3` imported 8 of its 11 files. Files that come
  from a disc folder now keep their original name with the part number in front
  (`01 - Chapter 1-2.mp3`, `09 - Chapter 1-2.mp3`), which also keeps them in order for players
  that sort by name. The same applies when renaming is on but the format has no `PartNumber`.
  Files from a flat folder keep their names exactly.
- **Two books in one folder.** The matcher treats all audio files of one type in one folder as a
  single book. When the album tags clearly name more than one book (`1984` x 12, `Animal Farm` x 9),
  each book is now matched on its own. Disc, part and format words are ignored when comparing
  albums (`Dune (Disc 1)` and `Dune [CD 2] (Unabridged)` are one book), a file without an album tag
  keeps the folder together, and one album per file with a 1..N track run (chapter titles in the
  album tag) is not split.

Code: `src/NzbDrone.Core/Talearr/Audiobook/AudiobookDiscFolder.cs`, `AudiobookTrackOrder.cs`,
`SharedFolderBookSplitter.cs` and `TalearrNamingTokens.GetOriginalFileName`, hooked from
`PartAssignmentHelper`, `FileNameBuilder.BuildBookFileName` and the unit grouping in
`FileMatchingService`. No setting.

## Merge to M4B

Settings > Conversion > Merge to M4B > **Merge after import** (off by default), and a
**Convert to M4B** button on the book page (audiobooks with more than one mp3, m4a or m4b file).

- Runs as the background command `TalearrConvertToM4b` (`POST /api/v1/command`
  `{"name": "TalearrConvertToM4b", "bookId": 123}`), shown as "Convert to M4B" in System > Tasks
  with progress messages. After import it is queued once per imported book with two or more
  mergeable files.
- One M4B per edition, written as `<Book Title>.m4b` in the folder that holds the files (the book
  folder above any disc folders). An existing file of that name is never overwritten.
- FFmpeg concat. AAC sources with the same sample rate and channels are copied without
  re-encoding; anything else is encoded to AAC with the existing Conversion settings (Preferred
  Bitrate, Do Not Upscale Bitrate, Audio Channels).
- Chapters: a file that already has chapters keeps them (shifted to its place in the book); every
  other file is one chapter, named from its title tag when the titles differ, else from the file
  name without the track number and the text all names share
  (`101 - Walter Isaacson - Steve Jobs - 2011 - Apple is Born` becomes `Apple is Born`), with the
  disc folder in front when names repeat (`Part 3 - Chapter 1-2`), else `Chapter N`.
- Tags: title, album, artist and album artist (author), composer (narrator, where Audiobookshelf
  and most players look), date (the recording's year, else first published), genre `Audiobook`,
  description, publisher, grouping (`Series #N`) and the audiobook media type. Cover:
  `cover.jpg`/`folder.jpg` in the book folder, else the first embedded cover.
- Safety: ffmpeg writes to `.talearr-m4b/<name>.m4b.partial~` next to the target (disk scans skip
  dot folders and `*.partial~` files). The result must have an audio stream, chapters, and a
  duration within 1% of the summed source durations, or it is deleted and the book is left as it
  was. Only then is it renamed into place, added to the library, and the original files removed
  (to the recycle bin when one is set; history records them as deleted for an upgrade). Emptied
  disc folders are removed. If the database update fails, the new M4B is deleted and the
  originals stay.
- Not a disk-access command, so imports are not held up for the length of an encode.

FFmpeg and FFprobe were already in the TaleArr Docker image (`ffmpeg` from the Debian packages in
`Dockerfile.cross`, used by Chaptarr's own conversion and m4b-tool), so the image is unchanged
(size increase 0). Native installs need FFmpeg on the PATH; without it the command fails with
"FFmpeg and FFprobe are needed" and nothing is touched.

Chaptarr's own download-time conversion (quality profile "convert to M4B", m4b-tool) is separate
and unchanged; when it has already produced a single M4B there is nothing left to merge.

Storage: `Config` table row `talearr.audiobook.m4bafterimport` (`True`/`False`), read and written
only by TaleArr through the new `/api/v1/config/talearrm4b` route
(`{"id": 1, "convertAfterImport": false}`). Chaptarr ignores the row.

Code: `src/NzbDrone.Core/Talearr/Audiobook/M4b/`, `Chaptarr.Api.V1/Config/TalearrM4bConfigController.cs`,
`frontend/src/Settings/Conversion/TalearrM4b*.js`.
