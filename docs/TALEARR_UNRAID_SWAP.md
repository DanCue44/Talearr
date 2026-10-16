# Swapping the Unraid Chaptarr container to TaleArr

TaleArr reads the same `/config` folder, `config.xml` and `chaptarr.db` as Chaptarr and
keeps the same API, port and environment variables. Swapping is a change of the image
only. Volumes, port, `PUID`/`PGID`/`UMASK` and the container network stay as they are,
so anything that talks to Chaptarr (AllSeerr, Prowlarr, NPM proxy hosts) keeps working.

Current container (for reference): image `chaptarr/chaptarr:latest`, `PUID=99`,
`PGID=100`, `UMASK=000`, port `8789`, volumes `/config`, `/audiobooks`, `/ebooks`,
`/downloads`.

## 0. Pre-checks

1. **Version.** In Chaptarr, open System > Status and note the Version (for example
   `0.9.965.0`). Open the TaleArr image's "Based on Chaptarr" version (TaleArr System >
   Status, or the `io.talearr.upstream.version` label:
   `docker inspect -f '{{ index .Config.Labels "io.talearr.upstream.version" }}' ghcr.io/dancue44/talearr:develop`).
   TaleArr must be based on the **same or a newer** Chaptarr version than the one running.
   If Chaptarr is newer, merge upstream into TaleArr first (see
   [TALEARR_UPSTREAM_SYNC.md](TALEARR_UPSTREAM_SYNC.md)) and wait for the new image.
2. **Registry access.** The GHCR package is private while the repo is private. On Unraid:
   ```bash
   echo <github-token-with-read:packages> | docker login ghcr.io -u DanCue44 --password-stdin
   ```
   `/root` on Unraid lives in RAM, so the login is lost on reboot. Either make the
   `talearr` package public in GitHub (Packages > talearr > Package settings), or persist
   the login: `cp /root/.docker/config.json /boot/config/docker-ghcr.json` and add
   `mkdir -p /root/.docker && cp /boot/config/docker-ghcr.json /root/.docker/config.json`
   to `/boot/config/go`.
3. **Pull first**, so the swap itself is quick:
   ```bash
   docker pull ghcr.io/dancue44/talearr:develop
   ```

## 1. Back up

1. In Chaptarr: System > Backup > Backup Now (keeps a zip in `/config/Backups`).
2. Stop the container and copy the whole appdata folder (use the host path mapped to
   `/config` in the template, for example `/mnt/user/appdata/chaptarr`):
   ```bash
   docker stop Chaptarr
   tar -C /mnt/user/appdata -czf /mnt/user/appdata/_chaptarr-backup-$(date +%Y%m%d-%H%M).tgz chaptarr
   ```

## 2. Swap the image

In the Unraid web UI: Docker > Chaptarr > Edit.

1. Change **Repository** from `chaptarr/chaptarr:latest` to
   `ghcr.io/dancue44/talearr:develop`.
2. Leave every path, port and variable as is. Optionally set the Icon URL and rename the
   container; keeping the name `Chaptarr` means nothing that points at it has to change.
3. Apply. Unraid recreates the container with the new image and the same template.

Command line equivalent (same values as the template, adjust host paths):

```bash
docker rm -f Chaptarr
docker run -d --name Chaptarr --restart unless-stopped \
  -p 8789:8789 \
  -e PUID=99 -e PGID=100 -e UMASK=000 -e TZ=America/New_York \
  -v /mnt/user/appdata/chaptarr:/config \
  -v <host audiobooks>:/audiobooks \
  -v <host ebooks>:/ebooks \
  -v <host downloads>:/downloads \
  ghcr.io/dancue44/talearr:develop
```

## 3. Verify

```bash
docker logs Chaptarr 2>&1 | grep -m1 'Starting TaleArr'
docker exec Chaptarr stat -c '%u:%g' /proc/1          # expect 99:100
KEY=$(sed -n 's:.*<ApiKey>\(.*\)</ApiKey>.*:\1:p' /mnt/user/appdata/chaptarr/config.xml)
curl -s -H "X-Api-Key: $KEY" http://localhost:8789/api/v1/system/status | head -20
```

Expect `"appName": "Chaptarr"` (unchanged for API clients), `"instanceName": "TaleArr"`
and the same `"version"` and `"migrationVersion"` as
before. The UI tab title and
System > Status should say TaleArr. Check that the library, wanted list and download
clients look the same, and run a test from one notification and one download client.

## Rollback

1. Docker > Chaptarr > Edit, set **Repository** back to `chaptarr/chaptarr:<version>`.
   Use the "Based on Chaptarr" version TaleArr reported, or newer. `latest` is fine as long
   as it is not older than that.
2. Apply. Chaptarr starts on the same `/config`.
3. Only if Chaptarr fails to start or data looks wrong: stop it, restore the tarball from
   step 1 and start again.
   ```bash
   docker stop Chaptarr
   mv /mnt/user/appdata/chaptarr /mnt/user/appdata/chaptarr.talearr-failed
   tar -C /mnt/user/appdata -xzf /mnt/user/appdata/_chaptarr-backup-<stamp>.tgz
   docker start Chaptarr
   ```

TaleArr does not add database migrations or rewrite `config.xml`, so a plain image swap
back is normally enough. Settings changed while running TaleArr are kept. The one
TaleArr-only behaviour that carries over is cosmetic: an instance name containing
"TaleArr" is shown as "Chaptarr" by upstream.

### Recordings (TaleArr 0.12.0 and later)

Recordings need nothing extra for a rollback, but check these before and after:

- **Additional recordings stay.** They are Chaptarr's own copy entries (`_wanted_` and `_copy_`
  in the URL), so Chaptarr keeps, searches and upgrades each one by itself. Without TaleArr
  there is no upgrade guard and no keep both reroute: an unabridged download may replace an
  abridged recording's files again, as in stock Chaptarr.
- **Preview before renaming.** TaleArr gives a copy its own folder (` (Abridged)`, the narrator,
  the year or the ASIN appended) when the naming format cannot tell two recordings apart.
  Chaptarr does not know that rule. Files stay where they are, but before running Rename files
  in Chaptarr, look at the preview (Preview Rename on the book page, or
  `GET /api/v1/rename?authorId=...`) and skip any move of a copy into the primary recording's
  folder.
- **`talearr.db`** in `/config` is not used by Chaptarr. Keep it for a return to TaleArr or
  delete it (`talearr.db`, `talearr.db-wal`, `talearr.db-shm`); it is a cache and is fetched
  again.
- **`talearr.*` rows** in the `Config` table (`talearr.recordings.*`, `talearr.upgrade.*`,
  `talearr.grabpolicy.*`, `talearr.audiobook.*`) are ignored by Chaptarr and can stay.
- **Scheduled task.** TaleArr's "Refresh Audible Recording Facts" task row is dropped by
  Chaptarr on its next start.

## Notes

- The Unraid "update available" check works with `ghcr.io` images.
- Inside Docker, TaleArr refuses to install in-app updates, because the update feed serves
  upstream Chaptarr builds that would replace TaleArr. System > Updates may still list newer Chaptarr versions; ignore them and
  update the TaleArr image instead.
- Tested locally (native build, macOS): a fresh Chaptarr 0.9.965 config folder was copied
  and started with TaleArr. The database schema and `config.xml` were byte-for-byte
  unchanged afterwards, no migrations ran, and `/api/v1/system/status` returned the same
  fields (only `instanceName` differs: `Chaptarr` -> `TaleArr`). The Docker workflow repeats
  this check with the real images on every build.
