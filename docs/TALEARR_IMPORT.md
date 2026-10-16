# TaleArr import additions

Two things TaleArr does on completed downloads that Chaptarr does not.

## Archives

Scene ebook releases often arrive as a zip that holds a rar (plus `.nfo` and `.diz`
files), and multi-part rar sets are common for audiobooks. When a completed download
has no audio or ebook files but does have archives, TaleArr unpacks them before
importing:

- Supported: `.zip`, `.rar` (single, `.partN.rar` sets and old `.rar`/`.r00` sets), `.7z`,
  and archives inside archives, up to 3 levels deep.
- The archives are unpacked into a work folder next to the download, named
  `.talearr-unpack.<download name>`. If that folder cannot be created (read-only download
  folder) the system temp folder is used instead.
- Book files are imported from the work folder, then the work folder is deleted. The
  original download is only read, never moved or deleted, so torrents keep seeding.
  If a detached conversion still needs the files, the folder is kept until the next
  import pass and reused. Work folders older than two days whose download is gone are
  removed the next time something is unpacked in the same folder.
- Downloads that already contain book files are left alone, so the NZBGet BookUnpack
  extension can stay in place.
- Guards: every entry path is checked so nothing is written outside the work folder
  (absolute paths, drive letters and `..` are refused, links are skipped); unpacking stops
  when an archive expands to more than 100 times its size (with a 256 MiB floor), when the
  whole unpack passes 50 GiB or 10,000 entries, or when there is less free space than the
  archives' size. Password-protected or incomplete archives are refused. Sizes are counted
  from the bytes written, not from archive headers.
- An archive that cannot be unpacked blocks the import with the reason shown in the queue.

The library is [SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT,
pure managed code, so it runs in the linux-musl image without native unrar or 7z).

Code: `src/NzbDrone.Core/MediaFiles/TalearrImport/SafeArchiveExtractor.cs` and
`DownloadArchiveUnpacker.cs`, called from `DownloadedBooksImportService.ProcessPath`.

## Downloaded-file language

After download and before import, TaleArr reads the language a file declares about itself:

| File | Where the language comes from |
|---|---|
| EPUB | OPF `dc:language` (the rootfile named in `META-INF/container.xml`) |
| MOBI, AZW, AZW3 | EXTH record 524 |
| PDF | catalog `/Lang`, then XMP `dc:language` |
| Audio | language tags already read by the import (ID3 `TLAN`, MP4/Vorbis/APE/ASF `LANGUAGE`) |

When the file says nothing (or only `und`/`mul`), the release and file names are checked
for explicit language words, ignoring case (`SWEDiSH`, `Dutch`, `[GER]`, `Svenska`). Words
that are part of the book title or author name do not count, and two-letter codes are
never used.

The wanted language is, in order: the languages allowed by the author's metadata profile
for that media type (when set); otherwise the languages of the book's monitored editions
and of every edition published under the book's own title. Only completed downloads are
checked, not manual imports.

When the file's language is not a wanted one, the file is rejected with a `Language`
rejection, for example:

> File language is Swedish (swe) (from EPUB dc:language), but English (eng) is wanted
> (editions of 'The Pelican Brief'). TaleArr rejected this release so another one can be
> tried.

If nothing in the download imported and every file was rejected for language (files only
refused by the quality profile may sit beside them), the download is marked failed the
normal way (`FailedDownloadService.MarkAsFailed`), so it is blocklisted and the usual
redownload setting decides whether another release is searched. The reason is stored in
the `downloadFailed` history entry. Grabs the user forced are not failed; they stay
blocked in the queue with the same reason.

Code: `FileLanguageReader.cs`, `ReleaseNameLanguageDetector.cs` and
`DownloadedFileLanguageVerifier.cs` in `src/NzbDrone.Core/MediaFiles/TalearrImport/`, and
`CompletedDownloadService.IsWrongLanguagePayload`.

## Settings

Both features are on by default and can be turned off in Settings > Media Management >
Importing (shown with advanced settings):

| Setting | API field (`/api/v1/config/mediamanagement`) | Stored as |
|---|---|---|
| Unpack Archives | `talearrUnpackArchives` | `Config` table row `talearrunpackarchives` |
| Verify File Language | `talearrVerifyFileLanguage` | `Config` table row `talearrverifyfilelanguage` |

They live in the existing key/value `Config` table, so there is no schema migration and
`config.xml` is not touched. Chaptarr ignores rows it does not know, so rolling back to
Chaptarr on the same database keeps working; the rows simply stay unused. The API fields
are additive; no existing route or field changed.
