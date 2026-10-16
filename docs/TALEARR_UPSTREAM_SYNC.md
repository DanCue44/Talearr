# Pulling upstream Chaptarr into TaleArr

Remotes: `origin` is `github.com/DanCue44/TaleArr`, `upstream` is
`github.com/Chaptarr/chaptarr`. TaleArr's `develop` follows upstream `develop`.

## Merge

```bash
git fetch upstream --tags
git checkout develop
git pull origin develop
git checkout -b sync/upstream-$(date +%Y%m%d)
git merge upstream/develop
```

Resolve conflicts (see below), then check locally:

```bash
python3 build-tools/talearr_brand_check.py
yarn install --frozen-lockfile && yarn typecheck && yarn build
dotnet build src/Chaptarr.NoTests.sln -c Release
dotnet test src/Chaptarr.Core.Test/Chaptarr.Core.Test.csproj -c Release
```

Push the `sync/...` branch. **TaleArr CI** (`.github/workflows/talearr-ci.yml`) runs the
same checks. Open a PR into `develop` and merge when green; the Docker workflow then
publishes `ghcr.io/dancue44/talearr:develop`. Use `git merge`, not rebase, so upstream
history stays intact and later merges stay small.

To follow a release instead of the tip: `git merge v0.9.970` (upstream tags are fetched
with `--tags`).

## Where conflicts happen

TaleArr keeps its changes small and in few places on purpose:

| Area | TaleArr change | On conflict |
|---|---|---|
| Logos and icons (`Logo/`, `frontend/src/Content/Images/`) | files replaced in place, same names | keep ours: `git checkout --ours <file>` |
| `src/Directory.Build.props` | one `<Import>` line at the end | keep upstream's version, re-add the import |
| `src/Talearr.props`, `TalearrBranding.cs`, `build-tools/talearr_*`, `docs/TALEARR_*`, `.github/workflows/talearr-*` | TaleArr-only files | never conflict |
| `BuildInfo.cs`, `ConfigFileProvider.cs` (InstanceName), `AppFolderInfo.cs`, `LocalizationService.cs`, `RuleBuilderExtensions.cs`, `InstallUpdateService.cs` | small marked blocks (`// TaleArr:`) | take upstream, re-apply the block |
| Notification titles and test messages, health check text, startup logs | the word Chaptarr -> TaleArr in literals | take upstream, re-apply the word change |
| Frontend strings (`translate.ts`, `login.html`, `index.ejs`, `manifest.json`, About, More Info, a few modals) | the word Chaptarr -> TaleArr | take upstream, re-apply |
| `README.md`, `COPYRIGHT.md`, `docker-compose.yml`, `docker-entrypoint.sh` | TaleArr text | keep ours, carry over relevant upstream content |

Translation files (`src/NzbDrone.Core/Localization/Core/*.json`) are **not** edited:
`TalearrBranding` rewrites "Chaptarr" to "TaleArr" when they load, so new upstream strings
are rebranded automatically.

New upstream strings hard-coded in C# or JS (not in translations) will say Chaptarr until
changed. Find them with:

```bash
git diff ORIG_HEAD..HEAD -U0 -- src frontend/src | grep '^+' | grep -n '"[^"]*Chaptarr[^"]*"'
```

Leave technical identifiers alone: `BuildInfo.AppName`, `Chaptarr__` env config,
`Chaptarr_*` custom script variables, user agents, file names, namespaces and API routes.

## Version

Upstream bumps `package.json` and the `AssemblyVersion` in `src/Directory.Build.props`;
take those as they come (they are the "Based on Chaptarr" version). Bump TaleArr's own
version in `src/Talearr.props` when releasing TaleArr: patch for upstream syncs and
fixes, minor for TaleArr features. Tag `vX.Y.Z` on `main` to publish a versioned image.

## Upstream workflows

The upstream workflows (`build.yml`, `release.yml`, `docker-publish.yml`) stay in the repo
so merges are clean, but are meant to stay disabled in DanCue44/TaleArr (they target
Chaptarr's Docker Hub and run a three-OS matrix):

```bash
gh workflow disable "Build Chaptarr" -R DanCue44/TaleArr
gh workflow disable "Release" -R DanCue44/TaleArr
gh workflow disable "Docker Build and Publish" -R DanCue44/TaleArr
```
