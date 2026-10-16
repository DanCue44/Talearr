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
| Grab policy: `ReleasePackDetector.cs`, `MultiBookReleaseSpecification.cs`, `DiscographySpecification.cs`, `DownloadDecisionComparer.cs`, `DownloadDecisionPriorizationService.cs`, `RedownloadFailedDownloadService.cs`, `IndexerSettings.js`, `settingsActions.js` | small marked blocks (`// TaleArr:`) and optional constructor parameters; the rest lives in `src/NzbDrone.Core/Talearr/GrabPolicy/` and TaleArr-only frontend files | take upstream, re-apply the block (the brand check lists each hook) |
| Notification titles and test messages, health check text, startup logs | the word Chaptarr -> TaleArr in literals | take upstream, re-apply the word change |
| Frontend strings (`translate.ts`, `login.html`, `index.ejs`, `manifest.json`, About, More Info, a few modals) | the word Chaptarr -> TaleArr | take upstream, re-apply |
| `README.md`, `COPYRIGHT.md`, `docker-compose.yml`, `docker-entrypoint.sh` | TaleArr text | keep ours, carry over relevant upstream content |
| `src/NzbDrone.Core/MediaFiles/TalearrImport/`, `src/Chaptarr.Core.Test/MediaFiles/TalearrImport/` | archive unpacking and downloaded-file language check | TaleArr-only, never conflict |
| `DownloadedBooksImportService.cs` (second constructor, `ProcessPath` unpack wrapper, `Verify` calls), `CompletedDownloadService.cs` (`IsWrongLanguagePayload` branch), `ConfigService.cs`/`IConfigService.cs`/`MediaManagementConfigResource.cs` (two `Talearr*` settings), `MediaManagement.js` (two checkboxes), `Directory.Packages.props`/`Chaptarr.Core.csproj` (SharpCompress) | marked `TaleArr` blocks, see [TALEARR_IMPORT.md](TALEARR_IMPORT.md) | take upstream, re-apply the block |
| Phase 1 quick wins: `MetadataServerHealthService.cs` (ReportSuccess), `MetadataServerCheck.cs`, `BookInfoProxy.cs` (RefreshAuthorInfo error paths), `RefreshResult.cs`, `RefreshAuthorService.cs` (RefreshReason.Error log), `Rejection.cs` (Source), `DownloadDecisionMaker.cs` (EvaluateSpec), `ReleaseResource.cs` (RejectionDetail.Label/Source), `AddBookService.cs` (BuildRequestedBookMonitoringConfig), `InteractiveSearchRow.js/.css`, `InteractiveSearch.css`, `searchActions.js` (bookDefaults), `migrate.js` | small marked blocks (`// TaleArr:`); the logic lives in `Talearr*` files | take upstream, re-apply the block |
| `EditionSelector.cs`, `BookController.cs` (UpdateBookInternal), `RefreshBookService.cs` (MonitorSingleEdition), `RefreshAuthorService.cs` (NormalizeRemoteBooks), `DownloadedBooksImportService.cs` (CanRetargetSameWorkMatch), `ReleaseTitleMatchScorer.cs` | matching and edition fixes, marked `// TaleArr:` (see [TALEARR_MATCHING.md](TALEARR_MATCHING.md)) | take upstream, re-apply the block |
| `Talearr*.cs`, `ReleaseTitleMatchScorer.Talearr.cs` and their `Talearr*Fixture.cs` tests | TaleArr-only files | never conflict |
| Audiobooks (see [TALEARR_AUDIOBOOKS.md](TALEARR_AUDIOBOOKS.md)): `FileNameBuilder.cs` (`TalearrNamingTokens` calls, `{Book SeriesPosition}` padding, narrator fallback), `PartAssignmentHelper.cs` (`AudiobookTrackOrder.Order`), `FileMatchingService.cs` (`BuildTalearrSharedFolderBookKeys` and the `talearr-book` sub key), `CommandResource.cs` (command display name), `NamingModal.js`/`NamingOption.js` (tokens, layouts, `isLiteral`), `ConversionSettings*.js` (M4B section), `BookDetails*.js` (Convert to M4B), `commandNames.js`, `settingsActions.js` | small marked `TaleArr` blocks; the logic lives in `src/NzbDrone.Core/Talearr/Audiobook/` | take upstream, re-apply the block (the brand check lists each hook) |
| Phase 2 recordings: `src/NzbDrone.Core/Talearr/Recordings/`, `src/Chaptarr.Api.V1/Config/TalearrRecordingsConfigController.cs`, `src/Chaptarr.Core.Test/Talearr/Recordings/` | `talearr.db` cache and `talearr.recordings.*` settings (see [TALEARR_RECORDINGS_DESIGN.md](TALEARR_RECORDINGS_DESIGN.md)) | TaleArr-only files, never conflict |
| `FileNameBuilder.cs` (`partial`, optional `Lazy<ITalearrRecordingFolders>` parameter, `TalearrDisambiguateRecordingFolder` call; the rest is in `FileNameBuilder.Talearr.cs`), `ImportApprovedBooks.cs` (optional `ITalearrRecordingFolders` parameter, `FindImportConflict` block before the per-file loop) | Phase 2 recordings: copy folder disambiguation, marked `// TaleArr:` | take upstream, re-apply the block (the brand check lists each hook) |
| `TaskManager.cs` (RefreshAudibleRecordingFactsCommand scheduled task), `AudibleCatalogProxy.cs` (`UserAgents` accessor), `DownloadDecisionComparer.cs`, `DownloadDecisionPriorizationService.cs` and `FileNameBuilder.cs` (optional `ITalearrAbridgedResolver` parameter) | Phase 2 recordings hooks, marked `// TaleArr:` | take upstream, re-apply the block (the brand check lists each hook) |
| Scorer and edition tests changed to TaleArr behavior (`ReleaseTitleMatchScorerFixture.cs`, `ReleaseTitleMatchSpecificationFixture.cs`, `EditionSelectorInvariantFixture.cs`, one row in `release-title-scorecard.ndjson`) | expectations flipped, marked `TaleArr:` | take upstream, re-apply the flip |

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
