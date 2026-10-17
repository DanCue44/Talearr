# Soulseek through slskd

TaleArr can search Soulseek for one book and download a whole folder through your own
[slskd](https://github.com/slskd/slskd). It is **off by default** and only does anything when you
press **Search Soulseek** on a book page. Only search for and download what you are allowed to.

## Setup

Settings > Download Clients > **Soulseek (slskd)**:

| Setting | Default | What it does |
|---|---|---|
| Enable Soulseek | off | Shows Search Soulseek on book pages. |
| slskd URL | empty | For example `http://slskd:5030`. |
| API key | empty | One of slskd's `web.authentication.api_keys`. Returned masked by the API. |
| Downloads folder | empty | slskd's downloads folder as TaleArr sees it. Map it into the TaleArr container. |
| Search time | 15 s | How long slskd collects answers (5 to 120). |
| Free upload slot only | on | Etiquette: only peers that can send now, so you do not queue behind others. |
| Maximum peer queue | 50 | Etiquette: skip peers with more queued uploads (0 = no limit). |
| Minimum peer speed | 0 | Skip slower peers, KiB/s (0 = no limit). |
| Downloads per peer | 1 | Etiquette: unfinished downloads from one peer at a time. |
| Downloads in total | 3 | Unfinished Soulseek downloads at a time. |
| Give up after | 24 h | A download the peer has not started by then is cancelled. |
| Clean up after import | on | Clears the finished transfers in slskd and removes the leftover folder. |

**Test** checks that slskd answers, the API key works, slskd is logged in, and TaleArr can see
the downloads folder. Share something back in slskd; Soulseek runs on goodwill.

## How a search is judged

- The search text is the author and the main title, without punctuation.
- **Audiobooks**: each remote folder with audio files is one release, with its cover art. Disc
  folders (CD1, Disc 2, Part 03) join their parent folder. A folder of several single-file books
  (`Book One.m4b`, `Book Two.m4b`) offers each file on its own unless the names say they are parts
  of one book. Folders with locked (buddies only) audio are skipped because they would arrive
  incomplete, and disc folders whose file names collide (`CD1\01.mp3`, `CD2\01.mp3`) are skipped
  because slskd puts a download into one folder.
- **Ebooks**: each book file is one release, in the best format that folder has for it (EPUB,
  AZW3, MOBI, PDF).
- Each release gets a title from the meaningful folder names plus a format hint
  (`Frank Herbert - Dune [MP3 64kbps]`) and goes through the **normal decision engine** for that
  book: title and series matching, language, packs and bundles (grab policy), size, quality
  profile and the TaleArr quality ladder, release profiles. The results show the same labels as
  interactive search. Rejected results can still be grabbed on purpose.
- A peer whose download of this book failed is not offered for it again.

## What a grab does

1. slskd 0.26 or later gets a batch with TaleArr's own destination folder
   (`<downloads>/talearr-<id>`). Older slskd gets the plain enqueue call; TaleArr then finds the
   files in slskd's default folder (`<downloads>/<remote folder name>`) or one level down.
2. TaleArr checks the transfers after each of upstream's download refreshes (every minute):
   queued, downloading, failed (any file errored, rejected, timed out or cancelled), or done.
3. When every file is there, it is imported for **the book it was grabbed for** through the
   normal import pipeline (Move), so the import rules, naming, recordings and the upgrade guard all
   apply. A failed import leaves the files where they are and says where (use Manual Import).
4. After a successful import, the slskd transfers are cleared and TaleArr's own folder is removed
   (a folder slskd chose is only removed when it is empty).

The book page lists Soulseek downloads for that book with Cancel and Forget.

## Not done (on purpose)

- **No automatic search** and no RSS: Soulseek is searched only when you press the button.
- **Not a download client or indexer provider.** Stock Chaptarr deletes provider rows whose type
  it does not know on start (`ProviderFactory.RemoveMissingImplementations`), and every grab
  through `DownloadService` writes its `DownloadProtocol` into History, Blocklist and pending
  releases. A new protocol value there is exactly what section 2 of
  [TALEARR_FEATURE_RESEARCH.md](TALEARR_FEATURE_RESEARCH.md) warns about. Mapping Soulseek onto
  `Torrent` instead would send its releases to torrent clients and torrent rules (seeding, delay
  profiles). So Soulseek grabs bypass that path: the candidate releases only live in memory, with
  `DownloadProtocol.Unknown` (0, a value every Chaptarr build knows), and are never stored.
- Soulseek downloads do not appear in Activity > Queue, and failed peers are not written to the
  Blocklist (they are kept with the grab instead).

## Rollback

Everything is in the `Config` table: `talearr.soulseek.settings` (JSON) and one
`talearr.soulseek.grab.<id>` row per download, pruned 14 days after it finished. Stock Chaptarr
ignores those rows. An import writes ordinary history rows (download id `slskd-<id>`, download
client text `Soulseek`), which Chaptarr reads like any other. After a rollback unfinished
downloads simply stop being tracked; finished files stay in slskd's downloads folder.

## API

- `GET|PUT /api/v1/config/talearrsoulseek`, `POST /api/v1/config/talearrsoulseek/test`
- `GET /api/v1/talearr/soulseek/status`
- `POST /api/v1/talearr/soulseek/search` `{ "bookId": 42 }` returns a `token` and results
- `POST /api/v1/talearr/soulseek/grab` `{ "token": "...", "key": "..." }`
- `GET /api/v1/talearr/soulseek/grabs?bookId=42`, `POST .../grabs/refresh`,
  `POST .../grabs/{id}/cancel`, `DELETE .../grabs/{id}`
