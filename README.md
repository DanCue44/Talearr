<div align="center">

<img src="Logo/talearr.png" width="160" alt="Talearr logo">

# Talearr

An ebook and audiobook collection manager for Usenet and BitTorrent users.

</div>

Talearr watches the authors and books you want, finds them on your indexers, sends them to your
download client and files them into a tidy library. It handles ebooks and audiobooks in one
instance, each with its own root folders, profiles and monitoring.

Talearr is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is a fork of
[Readarr](https://github.com/Readarr/Readarr). It reads the same config folder and database as
Chaptarr, so you can move over and back.

## Features

Everything Chaptarr does: authors and books, audiobook and ebook root folders, quality and
metadata profiles, Usenet and torrent download clients, indexers through Prowlarr, series,
calendar, notifications and the Readarr API. On top of that:

### Metadata

- **Its own metadata source built on [Hardcover](https://hardcover.app)**, with a free API token.
  Authors, books, editions (ISBN, ASIN, audio and ebook formats), series and covers come from
  Hardcover, so the library does not depend on one project's server. New installs start on it.
- **Less clutter.** Study guides, box sets, unrated listings and translations are left out by
  default. Books you monitor, request or already have are never removed by a filter.
- **Only my books.** An author added for one book request brings in that book, not the whole
  bibliography, until you choose Follow all books.
- **Author pages** show the main works first and group other listings (translations, sets,
  study guides) underneath.
- **Open Library fallback** keeps search and add working when a metadata service is down.

### Finding and grabbing

- **The right edition**: English editions by plain title, subtitles handled on both sides,
  unabridged audiobooks by default and rejection reasons shown for every release.
- **Release identity checks**: short titles, series names and wrong authors no longer grab or
  import the wrong book.
- **Automatic retry** with another release when a download fails, across all indexers.
- **Scheduled missing search** that works through monitored missing books a few at a time.
- **Bundles only as a last resort**, and only when the bundle names the book.
- **MyAnonamouse**: freeleech preference, seeding status and hit and run warnings, wedge and
  bonus point tools, with every timing under your control. Talearr only uses your `mam_id`
  session and MAM's allowed endpoints.
- **Soulseek** through slskd (manual search), and debrid through qBittorrent compatible proxies.

### Audiobooks

- **Recordings**: keep the abridged and the unabridged reading, or two narrators, side by side.
  Upgrades only replace files of the same recording.
- **Audible facts**: narrator, runtime and abridged or unabridged for each edition.
- **Multi-disc import**, naming tokens, an audio quality ladder and an optional merge to M4B.

### Library

- **Seeding-safe imports**: torrent imports are hardlinks, so the client keeps seeding.
- **Archives are unpacked** before import, and files in the wrong language are rejected.
- **Manual import** with a confidence rating for every match.
- **Merge duplicates** (authors and books), book tags and genre or content filters.
- **Series monitoring**, wishlists (Hardcover lists, StoryGraph and Audible exports) and
  migration from Readarr, Bookshelf or LazyLibrarian (System > Migrate).

### Reading and listening

- **Audiobookshelf** awareness: badges, no duplicate grabs and sidecar files.
- **Ebook delivery**: an OPDS catalog, Send to Kindle and a reader hand-off folder (with KEPUB
  conversion for Kobo).
- Works with [Allseerr](https://github.com/DanCue44/Allseerr) for book and audiobook requests.

The full list of changes, settings and API routes is in
[docs/TALEARR_REFERENCE.md](docs/TALEARR_REFERENCE.md).

## Install

Talearr is published as a Docker image: `ghcr.io/dancue44/talearr:latest` (amd64 and arm64). It
listens on port `8789` and keeps its data in `/config`.

### Docker run

```bash
docker run -d \
  --name talearr \
  -p 8789:8789 \
  -e PUID=1000 \
  -e PGID=1000 \
  -e UMASK=002 \
  -e TZ=America/New_York \
  -v /path/to/appdata/talearr:/config \
  -v /path/to/data:/data \
  --restart unless-stopped \
  ghcr.io/dancue44/talearr:latest
```

### Docker Compose

```yaml
services:
  talearr:
    image: ghcr.io/dancue44/talearr:latest
    container_name: talearr
    environment:
      - PUID=1000
      - PGID=1000
      - UMASK=002
      - TZ=America/New_York
    ports:
      - 8789:8789
    volumes:
      - /path/to/appdata/talearr:/config
      - /path/to/data:/data
    restart: unless-stopped
```

Map one parent folder that holds both your downloads and your library (for example
`/data/torrents` and `/data/media/audiobooks`) so imports can be hardlinks. Then open
`http://<your-server>:8789`.

### Unraid

Search for **Talearr** in the **Apps** tab (Community Applications) and install the template.
Keep downloads and the library in the same share so hardlinks work.

## Getting started

1. **Add a free Hardcover token.** Sign up at [hardcover.app](https://hardcover.app), open
   Settings > API and copy the token into Talearr under Settings > Metadata. Until then
   Talearr uses the Chaptarr metadata server, and System > Status reminds you.
2. **Add root folders** for ebooks and audiobooks (Settings > Media Management).
3. **Add a download client** and your indexers, or connect Prowlarr: add Talearr as a
   **Readarr** application with the URL `http://<host>:8789` and Talearr's API key.
4. **Add an author or a book** and pick what to monitor.

## Moving from Chaptarr, Readarr or LazyLibrarian

- **Chaptarr:** back up Chaptarr's config folder, then start Talearr on it (or on a copy).
  The database, settings, API key and API stay the same, so Prowlarr, Allseerr and other
  clients keep working. An existing library stays on the Chaptarr metadata server until you
  switch to Hardcover in Settings > Metadata. To go back, start Chaptarr on the same folder.
  See [docs/TALEARR_UNRAID_SWAP.md](docs/TALEARR_UNRAID_SWAP.md).
- **Readarr, Bookshelf or LazyLibrarian:** use System > Migrate. It previews what it will bring
  over before anything changes. See [docs/TALEARR_MIGRATION.md](docs/TALEARR_MIGRATION.md).

## Update channels

| Tag | Channel |
|---|---|
| `latest` | The newest release, for amd64 and arm64. Recommended. |
| `0.18.45` | A fixed release (every release has its own tag). |
| `develop` | Testing builds of upcoming changes. |

Talearr has its own version (System > Status), with the Chaptarr version it is based on
shown underneath.

## Outbound connections and privacy

Talearr has **no telemetry, analytics or tracking**. Besides your own indexers, download
clients and the services you connect, it talks to:

| Service | When |
|---|---|
| **Hardcover** (`api.hardcover.app`) | With a Hardcover token: authors, books, editions, series, lists and search. |
| **Chaptarr metadata server** (`api2.chaptarr.com`) | Without a Hardcover token, and for books known only by other ids. |
| **Open Library** (`openlibrary.org`, `covers.openlibrary.org`) | Publication dates, covers, and search while a metadata service is down (Settings > Metadata). |
| **Audible** (`api.audible.com`) and **Audnexus** (`api.audnex.us`) | Audiobook narrators, runtime and abridged or unabridged. Public catalogs, no account. |
| **Goodreads** (`www.goodreads.com`) | Search suggestions, as in Chaptarr. |
| **Chaptarr services** (`services.chaptarr.com`) | The update check, as in Chaptarr. |
| **Notification services** | Only the ones you enable. |

## Support

Report bugs and ask for features at
[github.com/DanCue44/Talearr/issues](https://github.com/DanCue44/Talearr/issues). Please do not
report Talearr problems to the Chaptarr or Servarr teams.

## About this project

Talearr started as a fork of Chaptarr that I built for my own library, to fix the things that
kept going wrong: wrong editions, wrong books, clutter, and a metadata server that could go
away. It grew into something I think other people can use, so I am sharing it. I will maintain
it and keep improving it as I have time.

## Building from source

Requires the .NET 10 SDK, Node.js 20 and Yarn 1.

```bash
yarn install --frozen-lockfile
yarn build
dotnet build src/Chaptarr.NoTests.sln -c Release
dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj -c Release
```

See [docs/TALEARR_REFERENCE.md](docs/TALEARR_REFERENCE.md#building-from-source) for running a
local build, and [docs/TALEARR_UPSTREAM_SYNC.md](docs/TALEARR_UPSTREAM_SYNC.md) for how Talearr
keeps up with Chaptarr.

## Credits

Based on [Chaptarr](https://github.com/Chaptarr/chaptarr) by the Chaptarr contributors, which is
based on [Readarr](https://github.com/Readarr/Readarr) and the shared codebase of the Servarr
team ([Sonarr](https://github.com/Sonarr/Sonarr), [Radarr](https://github.com/Radarr/Radarr),
[Lidarr](https://github.com/Lidarr/Lidarr), [Prowlarr](https://github.com/Prowlarr/Prowlarr)).
Book data comes from Hardcover, Open Library, Audible and Audnexus. Thank you to all of them.

Talearr is not affiliated with or endorsed by the Chaptarr project or the Servarr team.

## License

Talearr is free software under the [GNU GPL v3](LICENSE). See [COPYRIGHT.md](COPYRIGHT.md).
