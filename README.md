<div align="center">

<img src="Logo/talearr.png" width="160" alt="TaleArr logo">

# TaleArr

An ebook and audiobook collection manager, based on Chaptarr.

</div>

> TaleArr is a private fork and early work in progress.

## What is TaleArr?

TaleArr is a fork of [Chaptarr](https://github.com/Chaptarr/chaptarr), which is itself a
fork of [Readarr](https://github.com/Readarr/Readarr) from the
[Servarr](https://wiki.servarr.com/) project family. It manages audiobook and ebook
libraries in one instance: narrator and edition aware metadata, separate audiobook and
ebook root folders, series handling, quality profiles and upgrades, and the usual
download client and indexer integrations.

TaleArr tracks Chaptarr closely. The differences today are the name and icon, a separate
TaleArr version, a "Works best with" block in System > Status (AllSeerr, NoBookEnds), and
its own Docker image and CI. The icon master is `Logo/talearr-icon-master.png`.

## Drop-in compatibility with Chaptarr

TaleArr is built so it can start on an existing Chaptarr config folder and database
unchanged, and so Chaptarr can start on it again if you roll back.

Kept exactly as upstream:

- `config.xml`, `chaptarr.db`, `logs.db`, `cache.db` and the database schema and migrations
- the API routes and response fields (`/api/v1/...`; `appName` still reports `Chaptarr`
  so existing API clients keep working)
- environment variable config (`Chaptarr__Postgres__Host`, `Chaptarr__Server__Port`, ...)
- custom script environment variables (`Chaptarr_EventType`, ...)
- C# namespaces and assembly names (`NzbDrone.*`, `Chaptarr.*`), so upstream merges stay easy
- the metadata server (`api2.chaptarr.com`) and the user agent sent to it

Changed for users: the product name in the UI, page titles, login page, PWA manifest,
notifications, startup logs, the logo and icons, and the Docker image name. New native
installs use a `TaleArr` data folder; an existing `Chaptarr` data folder is reused.

## Versioning

TaleArr has its own version, set in `src/Talearr.props` (currently `0.10.0`). System >
Status shows it as the version, with a "Based on Chaptarr x.y.z" line underneath. The
assembly version stays the upstream Chaptarr version (from `package.json` and
`src/Directory.Build.props`) on purpose, because update checks, backups and metadata
requests key on it.

`build-tools/talearr_versions.sh` prints both.

## Docker

Images are published to GitHub Container Registry by `.github/workflows/talearr-docker.yml`:

| Tag | Built from |
|---|---|
| `ghcr.io/dancue44/talearr:develop` | every push to `develop` |
| `ghcr.io/dancue44/talearr:latest` | every push to `main` |
| `ghcr.io/dancue44/talearr:0.10.0` | `main` (TaleArr version) and `v0.10.0` git tags |
| `ghcr.io/dancue44/talearr:<branch>` | pushes to `feat/**` branches (amd64 only) |
| `ghcr.io/dancue44/talearr:sha-<commit>` | every build |

`develop`, `main` and version tags are multi-arch (`linux/amd64`, `linux/arm64`). The
repository is private, so the package is private too: run `docker login ghcr.io` with a
GitHub token that has `read:packages` on the machine that pulls it.

```bash
docker run -d \
  --name talearr \
  -p 8789:8789 \
  -e PUID=99 \
  -e PGID=100 \
  -e UMASK=002 \
  -v /path/to/config:/config \
  -v /path/to/audiobooks:/audiobooks \
  -v /path/to/ebooks:/ebooks \
  -v /path/to/downloads:/downloads \
  --restart unless-stopped \
  ghcr.io/dancue44/talearr:develop
```

The container starts as root only long enough to create the `PUID`/`PGID` user and fix
ownership of `/config`, then drops to that user with `gosu`. If `PUID`/`PGID` are not set
it defaults to `99:100`. `UMASK` is optional.

To move an existing Chaptarr container to TaleArr (and back), see
[docs/TALEARR_UNRAID_SWAP.md](docs/TALEARR_UNRAID_SWAP.md).

## Building from source

Requires the .NET 10 SDK, Node.js 20 (22 also works) and Yarn 1.

```bash
yarn install --frozen-lockfile
yarn build                      # frontend -> _output/UI
dotnet build src/Chaptarr.NoTests.sln -c Release
dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj -c Release

# run from a publish folder
dotnet publish src/NzbDrone.Console/Chaptarr.Console.csproj -c Release -f net10.0 -o _output/publish
cp -r _output/UI _output/publish/UI
dotnet _output/publish/Chaptarr.dll -nobrowser -data=/tmp/talearr-data
```

The entry assembly keeps its upstream name (`Chaptarr.dll`). Install FFmpeg when running
natively; the Docker image bundles it along with m4b-tool and mp4v2.

`python3 build-tools/talearr_brand_check.py` checks that the rebrand is intact and that the
drop-in identifiers are unchanged. CI runs it on every push.

## Keeping up with Chaptarr

See [docs/TALEARR_UPSTREAM_SYNC.md](docs/TALEARR_UPSTREAM_SYNC.md).

## Privacy

Like Chaptarr, TaleArr uses `api2.chaptarr.com` for metadata and matching, and the update
check contacts Chaptarr's update service. See the upstream README for what those requests
contain.

## License and credits

TaleArr is free software under the [GNU GPL v3](LICENSE). See [COPYRIGHT.md](COPYRIGHT.md).

TaleArr is based on [Chaptarr](https://github.com/Chaptarr/chaptarr) by the Chaptarr
contributors, which is based on [Readarr](https://github.com/Readarr/Readarr) and the
shared codebase of the Servarr team ([Sonarr](https://github.com/Sonarr/Sonarr),
[Radarr](https://github.com/Radarr/Radarr), [Lidarr](https://github.com/Lidarr/Lidarr),
[Prowlarr](https://github.com/Prowlarr/Prowlarr)). Thank you to all of them.

TaleArr is not affiliated with or supported by the Chaptarr project or the Servarr team.
Please do not send TaleArr support requests to them.

- Copyright 2026 TaleArr contributors
- Portions copyright 2026 Chaptarr contributors
- Portions copyright 2010-2026 the Servarr team and contributors
