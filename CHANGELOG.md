# Changelog

Notable changes to Talearr are listed here, newest first. Talearr's version number is its own and is separate from the Chaptarr version it is based on.

## 0.18.71 - 2026-11-24

### Added
- Clean Series From Titles (Settings > Metadata, off by default): books are shown and named without the series in the title, so "Viridian Gate Online: The Lich Priest: The Viridian Gate Archives, Book 5" becomes "The Lich Priest". Searches try the full metadata title first, then the shorter one, and releases named either way are recognized. Run Rename Files after turning it on. (#3)
- Display Title in the Edit book dialog: set your own title for a book, used for what Talearr shows and for file and folder names. Edit Details still changes the title everywhere. (#3)
- Make Primary on each additional audiobook recording, so you can choose which recording is the primary one and remove the old one. (#3)

### Fixed
- A book no longer lists the same audiobook recording twice with the primary one Missing: imports go to the book itself, and existing duplicates are merged on their own. (#3)
- Book requests that name a book by its Goodreads id (as Allseerr sends) now find the author's Hardcover book of the same title instead of waiting in the pending queue forever. A request that still cannot be matched stops after about two hours and shows why.

## 0.18.70 - 2026-11-22

### Fixed
- A download whose author name has a one-letter slip (an ebook of One Second After credited to "William R. Fortschen") is no longer rejected as another author's book. Only one word of the name may differ, by one letter or two swapped neighbours, in a word of seven letters or more.

## 0.18.69 - 2026-11-22

### Added
- Search on release day (Settings > Indexers, off by default): a monitored book is searched once when it comes out, and the scheduled missing search tries new releases first.
- Preferred Series in the Edit book dialog: for a book in more than one series, pick the one used for the series naming tokens and for sorting. Kept through refreshes.
- Lock Edition in the Edit book dialog: metadata refreshes, import lists and automatic switching never change the edition you picked.
- New wishlist sources: an owned Audible library export (added without searching), a pasted or uploaded list of author names, and a list of author names, ISBNs or ASINs read from a URL.
- A {Book Language} naming token.
- Import list exclusions can be searched and show when each one was added.
- The Add New page lists author adds and refreshes that are waiting or running, with a cancel button for waiting ones.

### Changed
- MAM searches that find nothing also try the title without apostrophes ("Enders Game") and the author name without middle initials or accents.

### Fixed
- The comics search no longer lists novel series that share a name with the search, such as the John Matherson books (One Second After) shown as a comic.

## 0.18.68 - 2026-11-20

### Fixed
- Audiobooks that Audible titles after their series (every Illusionist book is listed as "Viridian Gate Online", some without their own title) are now matched to the right book by series and book number, and keep the book's own title (#3).
- An Audible audiobook no longer goes to a bare Hardcover copy of a book that sits outside any series when the real book is in the series. The profile filters dropped such copies, and the audiobook with them.

## 0.18.67 - 2026-11-17

### Added
- A new grab policy, Prefer Abridged: abridged releases rank first and unabridged ones are still accepted. It can be set globally or per book.

### Fixed
- Asking for a second recording of an audiobook (such as the abridged reading while the unabridged one is still being searched for) adds it as a recording of its own instead of switching the book to it, so the reading already wanted keeps being searched for.
- When a completed download matches a different edition than the one grabbed for, the message now says how the two differ (abridged or unabridged, narrator, publisher, year, ISBN or ASIN) and how long the files run. A download that turns out to be the other reading is failed and replaced when failing rejected imports is on.
- While looking for an abridged recording, a release with no abridged or unabridged marking that is as large as the unabridged reading would be is skipped.

## 0.18.66 - 2026-11-17

### Fixed
- When Audible leads an author's audiobook details, books that only Audible lists are no longer dropped by the metadata profile's popularity rule. They now carry Audible's listener ratings, and owned copies are protected (#10).
- An author that Hardcover lists twice keeps its own name and details when a library scan finds it through the other Hardcover entry (#10).

## 0.18.65 - 2026-11-17

### Fixed
- Audible-only audiobooks that carry the author's Audible id now add to the matching library author instead of failing with "not found" (#10).

## 0.18.64 - 2026-11-16

### Fixed
- Audible results for authors listed without a middle name, or without an Audible author id, are now matched to the right library author and can be added (#10).
- An author's Audible catalog falls back to a first and last name search when the full name finds nothing.

## 0.18.63 - 2026-11-16

### Changed
- The root folder edit dialog also offers to apply Monitor New Books to the authors in the folder whose setting does not match, even when the folder's value itself is unchanged (#8).

## 0.18.62 - 2026-11-14

### Added
- When a root folder's Monitor New Books setting changes, the edit dialog offers to apply the new value to the authors already in that folder (#8).

### Fixed
- Manual import can relink a file that is already tracked under a different book to the book you pick, without copying, moving or renaming it. This also works in read-only library folders (#9).

## 0.18.61 - 2026-11-10

### Added
- The add search also asks Audible and lists audiobooks that Hardcover does not have for an author in the library. Added books stay with the author on later refreshes.
- New grab policy setting "Fail stalled downloads" (on by default): a download left paused in NZBGet for 6 hours with no progress is failed, blocklisted and searched again.

### Changed
- The delete dialogs no longer tick "Don't add this audiobook back" by default.

## 0.18.60 - 2026-11-09

### Added
- Audible fills gaps in Hardcover's audiobook data (on by default): missing audiobook editions, series, series numbers and ASINs. Hardcover's own values are never replaced.
- Per author, Audible can lead audiobook details, so its editions, series and numbers win and audiobooks Hardcover does not list are added.

### Fixed
- Series from the Talearr (Hardcover) metadata source are now saved instead of being dropped.

## 0.18.59 - 2026-11-09

### Fixed
- Old copies of a book carried over under different ids are merged into the copy that holds the files on refresh.
- A series label after a title (such as "book 2" in a subtitle) is no longer read as a different book.

## 0.18.58 - 2026-11-09

### Fixed
- Unmonitoring a book from its Edit dialog now sticks.
- An old book row that holds the files is merged with the server's empty copy of the same book on refresh, so the book no longer shows twice.

## 0.18.57 - 2026-11-08

### Fixed
- Edit Details links a book whose series name is left over from older metadata to that series when saved.
- Author refresh no longer fails for authors without a biography.
- Merging authors shows the kept author right away instead of a blank page until reload.
- A book whose old provider id the server no longer returns is updated in place instead of getting a second row.
- With the Hardcover source, duplicate works whose subtitle only names the series are folded together, and adding a book found by a Goodreads search no longer brings back merged or extra books.

## 0.18.56 - 2026-11-07

### Added
- Edit Details on the book page: correct a book's title, series and number, and an audiobook's narrators. Edits survive metadata refreshes and can be reset to the provider's values (#3).
- Merge Author on the author page, and the Duplicates page also suggests authors whose names differ only by a middle name or initial (#3).
- Optional read-only Calibre library lookup to identify ebooks in Retry Match and Manual Import (#4).

### Fixed
- A book moved by hand to another folder or root folder is no longer imported as a second recording.
- An unnumbered series slot takes the number from the book's own record.
- A reading sold under two ASINs shows once in the recordings list.

## 0.18.55 - 2026-11-06

### Fixed
- Add another recording no longer offers the recording a book already has, and editions of the same recording show as one entry.

## 0.18.54 - 2026-11-05

### Fixed
- Organize & Rename renames a book inside whichever root folder holds it, instead of skipping it when the author is set to a different root folder.

## 0.18.53 - 2026-11-04

### Added
- A tip link in the Support Talearr box.

### Fixed
- Adding or editing a read-only root folder is undone with a clear error if its read-only protection cannot be saved.

## 0.18.52 - 2026-11-04

### Fixed
- Read-only root folders are saved and protected again. The setting was never read back, so those folders were not protected and a second save failed (#1).

## 0.18.51 - 2026-11-03

### Fixed
- EPUBs made by the Kindle conversion are added to the library instead of failing to save.

## 0.18.50 - 2026-11-03

### Added
- Convert to EPUB (Settings > Conversion): optionally turn AZW3 and KF8 MOBI ebooks into EPUB after import, plus a task to convert books already in the library. The Kindle file is kept by default.

### Changed
- New ebook quality profiles rank EPUB as the best format. Existing profiles keep their order.

## 0.18.49 - 2026-11-03

### Added
- Interactive search marks releases that were already grabbed for the book and asks before grabbing one again.

### Changed
- Book list pages build file statistics for their own books only, so large libraries page quickly on a busy server.
- A download client that refuses a release it already has now gets a plain error message.

## 0.18.48 - 2026-11-01

### Fixed
- A download whose grabbed edition was replaced by a metadata refresh now imports into the book's monitored edition instead of stopping with an error.

## 0.18.47 - 2026-10-31

### Changed
- Plex, Kavita, Subsonic, Discord and Join now see the Talearr name and icon. New download clients default to a "talearr" category, and the release group fallback in file names is "Talearr". Saved settings are not changed.
- Cover images and PDF metadata are handled by new libraries (SkiaSharp and PdfPig), which removes several security advisories. Covers are limited to JPEG, PNG, WebP, GIF and BMP.

### Fixed
- Custom scripts get the talearr_* and readarr_* copies of the environment variables as intended.

## 0.18.46 - 2026-10-27

### Fixed
- Comic imports that failed on Talearr's side recover: a release NZBGet turned down as a copy or duplicate imports from the earlier download, earlier failed imports are retried once at startup, and a release that imports after all comes off the blocklist.

## 0.18.45 - 2026-10-24

### Fixed
- Failed or unimportable comic releases are blocklisted and a new search starts right away.
- Comic releases wrapped in zip, rar or 7z archives are unpacked before import.

## 0.18.44 - 2026-10-24

### Added
- Comic previews include each volume's page count, description and Hardcover link.

## 0.18.43 - 2026-10-24

### Fixed
- The comics list says when Hardcover covers could not load, so request apps can ask again soon instead of keeping blank covers.

## 0.18.42 - 2026-10-23

### Added
- Request apps can ask for specific comic volumes: only those volumes are monitored and searched.

## 0.18.41 - 2026-10-23

### Fixed
- Comic covers prefer sharp Hardcover images over small store thumbnails.

## 0.18.40 - 2026-10-23

### Fixed
- The popular manga list is in the right order, trending skips series with almost no popularity, and the curated comics list makes fewer Hardcover calls.

## 0.18.39 - 2026-10-23

### Added
- Comics discover lists (trending, popular, releasing manga and a curated set of Western comics) and a series preview for request apps.

## 0.18.38 - 2026-10-23

### Fixed
- Hardcover comic series show covers in comic search.

## 0.18.37 - 2026-10-21

### Added
- Comics and manga search on the private tracker indexer, with the same account and ratio rules as book grabs.

### Fixed
- Browsers drop the cached old favicon.

## 0.18.36 - 2026-10-21

### Changed
- Comics moved into the main menu: Library > Comics, a Books / Comics and manga switch in Add New, Wanted > Comics and Settings > Comics. Old comics links redirect.

## 0.18.35 - 2026-10-21

### Fixed
- Kavita scans the whole library when a comic series folder is new, so the first volume shows up.
- ComicInfo.xml page counts match the pages actually written.

## 0.18.34 - 2026-10-21

### Added
- Comics and manga (phase 1): series and volumes from AniList and Hardcover, wanted list, indexer search and grab, import as CBZ with ComicInfo.xml, Kavita and Komga scans, age ratings with kids library limits, a MangaDex rating fallback and a comics category on download clients.

### Fixed
- The MQTT connection no longer stops the app from starting.

## 0.18.33 - 2026-10-21

### Added
- A "Why" tab on the book page explains how the book was matched, why a release was grabbed or skipped, and how imports went.
- Read-only library root folders: scanned and counted, but never moved, renamed, retagged, converted or deleted.
- {ISBN} and {ASIN} naming tokens.
- New connections: Emby / Jellyfin library rescan, MQTT with Home Assistant discovery, and Grimmory library rescan.
- Open Library and Bookwyrm import lists.
- OpenID Connect settings can be set from environment variables, including a secret file.
- The private tracker's abridged tag is read as an Abridged indexer flag.

### Changed
- The reader hand-off to Calibre-Web-Automated sends each library file once, never overwrites a different book, and lets failed copies be retried.

### Fixed
- Books no longer show blank covers behind placeholder editions.
- Selection counts in the All view count cards, not format rows.

## 0.18.32 - 2026-10-21

### Fixed
- Book page tabs reload when switching between eBook and Audiobook.
- Single-format cards on grouped pages show their quality profile again.

## 0.18.31 - 2026-10-21

### Added
- One book page per work, with an eBook / Audiobook switch.

### Changed
- The All formats book index groups works on the server and pages again.

### Fixed
- The Add series dialog no longer crashes on empty tags, and root folder tags apply.
- The root folder metadata profile picker sees new profiles and keeps the chosen one.
- Completed downloads whose files are not readable yet are retried for 24 hours before being blocked.
- m4a releases are graded the same at search and import.
- Author searches query each media type with its own categories.
- Saving a metadata profile with changed languages no longer hangs on large libraries.
- Adding an author's missing media type no longer stores every book twice.
- Security updates for a cryptography package and safer cover decoding.

## 0.18.30 - 2026-10-21

### Fixed
- Adding a series no longer crashes on empty tags.
- The light theme default label color is gray.

## 0.18.29 - 2026-10-21

### Fixed
- Combined book cards use the ebook cover.

## 0.18.28 - 2026-10-21

### Fixed
- Combined book cards list both formats' quality profiles.

## 0.18.27 - 2026-10-21

### Added
- In the All formats view on Books, a work's ebook and audiobook show as one card with a badge for each format.

## 0.18.26 - 2026-10-21

### Fixed
- Toolbar link buttons use the toolbar text color in the light theme.

## 0.18.25 - 2026-10-21

### Fixed
- The light theme toolbar is Talearr orange instead of the inherited maroon.

## 0.18.24 - 2026-10-21

### Added
- An All / eBooks / Audiobooks toggle on Books.

### Changed
- Library opens on Books, listed before Authors.
- The theme accent is Talearr orange.

## 0.18.23 - 2026-10-21

### Fixed
- Toolbar link buttons show their labels.
- Book titles shown from the edition drop format labels such as "(MP3 Book)".
- Merge All folds plain rows into a set's recording copy instead of skipping the set.

## 0.18.22 - 2026-10-20

### Fixed
- English books whose editions have no ISBN are no longer dropped as translations unless they have very few readers.

## 0.18.21 - 2026-10-20

### Added
- Merge All Books on the Duplicates page.

### Changed
- A shorter menu: Series and Find Duplicates move to the Books toolbar, Bookshelf to Authors, Ignored Downloads to Blocklist and Migrate to Backup. Old addresses keep working.
- Hardcover title variants of one work become one book, and format-only labels are removed from titles.
- Small polish on author pages, author cards and the Quickstart Hardcover button.

## 0.18.20 - 2026-10-20

### Changed
- Books without a cover show a dimmed Talearr logo with a readable title.
- The donations section is about Talearr and credits the Chaptarr team.
- More info links point to Talearr's reference and issues.

### Fixed
- The book grid no longer draws empty loading boxes after the last book.
- A working Hardcover source is no longer listed as a health issue.

## 0.18.19 - 2026-10-20

### Changed
- New installs start on the Talearr (Hardcover) metadata source; existing libraries stay on the metadata server until switched.
- Project files, issue templates and the Unraid template point at Talearr.

## 0.18.18 - 2026-10-20

### Fixed
- English-only profiles hide unknown-language editions whose titles are plainly not English.

## 0.18.17 - 2026-10-20

### Fixed
- Files tagged with only the main title of a subtitled edition now match.

## 0.18.16 - 2026-10-20

### Fixed
- Search queries drop subtitles after a spaced dash.
- Searches that find releases but grab none log the reasons.

## 0.18.15 - 2026-10-20

### Fixed
- An untitled Hardcover book no longer fails an author's whole refresh.

## 0.18.14 - 2026-10-20

### Added
- Talearr metadata source switch (off by default): add, refresh, lookups and import matching from Hardcover.

### Fixed
- The parts and sets rule no longer fails on editions without a title.

## 0.18.13 - 2026-10-20

### Changed
- Hardcover readers count toward the popularity rule, Open Library fills missing release years, and Hardcover series are kept.

## 0.18.12 - 2026-10-20

### Changed
- Help links open Talearr's GitHub issues.

### Fixed
- Hardcover API keys limited to catalog access are accepted.

## 0.18.11 - 2026-10-20

### Added
- The author page lists main works first, with collections, study guides, adaptations, repeat listings and other languages in a collapsed section.

## 0.18.10 - 2026-10-20

### Changed
- User-visible backend messages, help texts and logs say Talearr.

## 0.18.9 - 2026-10-20

### Added
- A Hardcover metadata source in shadow mode: it compares results with the metadata server without changing anything.

## 0.18.8 - 2026-10-20

### Added
- Tracked authors: requesting a single book adds the author in "only my books" mode, keeping only requested and owned books. Switch with "Only my books" / "Follow all books" on the author page.

### Fixed
- Explicitly requested books pass the metadata profile's book filters instead of staying pending.

## 0.18.7 - 2026-10-20

### Fixed
- Monitored books are protected by their per-format monitored flag.
- Pending request messages say Talearr.

## 0.18.6 - 2026-10-20

### Changed
- Monitored books are kept through metadata profile filters, and views default to the library.
- Fresh installs get light default filters (sets, omnibuses, study guides, unrated).

## 0.18.5 - 2026-10-20

### Changed
- Format toggles are ordered All, eBooks, Audiobooks.
- Authors without a photo show the plain Talearr logo behind their name.

## 0.18.4 - 2026-10-20

### Changed
- Authors without a photo show a muted Talearr icon.
- Links point at the renamed Talearr repository.

## 0.18.3 - 2026-10-19

### Changed
- The name is spelled Talearr everywhere it is shown.

## 0.18.2 - 2026-10-19

### Changed
- Every private tracker timing and aggressiveness value is a user setting, with suggested values in the help text.

## 0.18.1 - 2026-10-17

### Added
- Taste preferences for the private tracker freeleech filler: preferred and excluded categories and authors, and optional import of preferred picks while they keep seeding.

## 0.18.0 - 2026-10-17

### Added
- Private tracker ratio guard: grabs are blocked while the account is seed-only, and non-freeleech grabs must keep the ratio above a minimum.
- Optional freeleech seeding filler that fills free slots with freeleech torrents.
- Optional bonus points auto-spend for VIP, upload credit and wedges, with a reserve and daily limits.
- Power user progress on Activity > Seeding.

## 0.17.2 - 2026-10-17

### Added
- Activity > Seeding and a seeding API: what each torrent client is seeding, ratios, seed time and hit-and-run risk, with a health warning for at-risk private torrents.

## 0.17.1 - 2026-10-17

### Added
- Scheduled missing-books search (off by default): weekly or daily, a set number of books per run.

### Fixed
- Stricter release identity checks stop wrong grabs from bundles that only mention a short title, releases by another author, and releases that only name the series.

## 0.17.0 - 2026-10-17

### Added
- Migration from Readarr, Bookshelf and LazyLibrarian (System > Migrate), with a preview before applying.
- Ebook delivery: an OPDS catalog and Send to Kindle.
- Book tags, more custom filters and genre rules in metadata profiles.
- Manual import shows match confidence and language warnings and offers a Copy (no hardlinks) mode.
- Notifications: health restored, manual interaction required, failure payloads for webhooks, and richer Discord and Pushover messages.
- Optional Soulseek search and grab through slskd.
- Open Library fallback audiobooks are enriched from Audnexus.
- Private tracker freeleech wedge balance, with a warning when none are left.
- An Unraid template.

### Changed
- Torrent imports are always copied or hardlinked, never moved, so torrents keep seeding. A warning appears when hardlinks keep falling back to full copies.
- Merged duplicates stay merged across refreshes and adds.

### Fixed
- An upgrade that the recording guard rejects no longer loops; the book pauses upgrades for 7 days.
- qBittorrent works better with debrid proxies.

## 0.16.0 - 2026-10-17

### Added
- Bundles are grabbed only as a last resort, and only the wanted books from a bundle are imported. Interactive search marks bundles.
- Open Library fallback for search and add while the metadata server is down.
- A merge tool for duplicate authors and books (Library > Duplicates).
- Audio quality ladder, ebook format order and a Convert library to M4B task.
- Reader hand-off: copy imported ebooks to a watch folder such as Calibre-Web-Automated's, with optional KEPUB conversion.
- Private tracker session health checks, dynamic seedbox IP updates and freeleech ranking.

### Changed
- Faster library pages on large libraries.

## 0.15.3 - 2026-10-16

### Fixed
- The favicon loads without signing in.

## 0.15.2 - 2026-10-16

### Fixed
- Safari shows a proper tab icon.

## 0.15.1 - 2026-10-16

### Fixed
- metadata.json uses the book's clean title and tidy author and narrator names.
- cover.jpg uses the largest available cover and is never replaced by a smaller one.

## 0.15.0 - 2026-10-16

### Added
- Per-book abridged policy and an "abridged only" option.
- Audible facts are looked up right after a book is added.

## 0.14.1 - 2026-10-16

### Fixed
- A grab that fails with an indexer error (429, 5xx or timeout) is retried later instead of waiting for the next search.
- Adding a book for an existing author no longer stores each audiobook twice. An opt-in cleanup removes earlier duplicates.

## 0.14.0 - 2026-10-16

### Added
- Series monitoring: monitor a series as audiobooks, ebooks or both, including books that join it later.
- Wishlists from StoryGraph, Audible, Goodreads CSV exports and Hardcover lists.
- Format policy: prefer audiobook, audiobook only, ebook only or both, for the library and per author.

### Fixed
- Folders and files are named after the book title without format noise.

## 0.13.0 - 2026-10-16

### Added
- Audiobookshelf awareness: Talearr reads your Audiobookshelf library, shows an "In Audiobookshelf" badge, and can skip releases for books you already have there.

## 0.12.0 - 2026-10-16

### Added
- Audiobook recordings: track more than one recording of a book (abridged and unabridged, different narrators), with Audible facts per edition and a Recordings section on the book page.
- A recording-aware upgrade guard so a download only replaces files with a better copy of the same recording, with an option to keep both.
- Multi-disc and multi-file audiobooks import as one book in the right order.
- Audiobook naming tokens and an Audiobookshelf folder layout.
- Merge multi-file audiobooks into one chaptered M4B.

## 0.11.0 - 2026-10-16

### Added
- Grab policy settings: prefer unabridged audiobooks, opt-in bundles, fail imports that can never succeed, and cap automatic retries.
- Zip, rar and 7z downloads are unpacked before import, leaving the original download to keep seeding.
- Downloaded files in the wrong language are rejected.
- Interactive search shows a short, readable reason for each rejected release.
- A metadata server health probe; refreshes pause quietly while the server is down.

### Changed
- The app shows the Talearr name and new logo, including a dark sign-in page.
- Release titles match with or without a subtitle, article or series prefix, with guards against sequels and other books in a series.
- Edition choice prefers your language over popular translations.
- Audible-only duplicate books are folded into the matching book.
- Adding one book monitors only that book by default.
- The built-in updater is blocked inside Docker so it cannot install an upstream Chaptarr build.

### Fixed
- The monitored edition can be switched on books that had several monitored editions.

## 0.10.0 - 2026-10-15

### Added
- First Talearr version, starting from Chaptarr. Talearr gets its own version number, shown in System > Status with the Chaptarr version it is based on.
- A Talearr logo and name in the page title, login page and app manifest.
