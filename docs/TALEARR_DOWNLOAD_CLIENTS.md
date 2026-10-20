# Download clients in Talearr

## What is already there

Chaptarr (and so Talearr) ships Readarr's download clients with per-media-type categories:

| Client | Protocol | Audiobook / ebook category or folder | Post-import category |
|---|---|---|---|
| qBittorrent | torrent | categories | yes |
| Deluge | torrent | labels | yes |
| rTorrent | torrent | labels | yes |
| uTorrent | torrent | labels | yes |
| Transmission | torrent | categories, directory | no (Transmission has no labels to move) |
| Vuze | torrent | as Transmission | no |
| Flood | torrent | audiobook and ebook tags | post-import tags |
| Download Station | both | categories, directory | no |
| Aria2, Hadouken | torrent | single category or none | no |
| SABnzbd, NZBGet, NZBVortex | usenet | categories | no (usenet clients have none) |
| Torrent / Usenet Blackhole, Pneumatic | both | watch folder | no |

Post-import categories pick the audiobook or ebook value from the download's media type, and
fall back to the category when only one is set (`PostImportCategoryResolver`). Nothing is
missing for the common clients, so Talearr adds no new client type.

**Rollback:** stock Chaptarr deletes download client (and indexer, connection, import list)
rows whose implementation it does not know on start (`ProviderFactory.RemoveMissingImplementations`).
That is why Talearr adds no provider types: a Talearr-only client would be silently removed by a
rollback, and its grabs could carry values Chaptarr cannot read. Soulseek is a separate feature
for that reason, see [TALEARR_SOULSEEK.md](TALEARR_SOULSEEK.md).

## Debrid through a qBittorrent-compatible proxy

Real-Debrid, AllDebrid, TorBox and the like work through a proxy that speaks qBittorrent's Web
API, such as [rdt-client](https://github.com/rogerfar/rdt-client) or
[Decypharr](https://github.com/sirrobot01/decypharr). Add it in Talearr as a normal
**qBittorrent** download client:

1. Host and port of the proxy (rdt-client 6500, Decypharr 8282), its user name and password.
2. Audiobook and ebook categories as usual (`audiobooks`, `ebooks`); the proxy creates the folders.
3. In the proxy, use a download folder Talearr can see, with the category as a subfolder, and
   let it finish the download to disk (rdt-client "Download client: internal" or a symlink mode
   Decypharr supports). Symlinks into a mount are copied on import, not hardlinked.
4. Leave seeding off in the proxy; a debrid download is finished when the files are there.

Talearr changes for these proxies (stock qBittorrent behaves exactly as before):

- The Web API version is read leniently (`v2.8`, `2.8.3-rdt`, `2`); a proxy that answers
  something else is treated as the oldest v2 API instead of failing the client.
- A torrent whose `content_path` is empty uses `save_path` + name, and a content path that is the
  save path with or without a trailing slash is recognised as the base folder (with the usual
  "Keep top-level folder" warning) instead of importing the whole download folder.

Possible limits of the proxies, not of Talearr: some may report the category save path as the content
path for single-file torrents (set "Create subfolder" in the proxy), and some may never report seeding
states, so a download only leaves the queue when the proxy removes it.
