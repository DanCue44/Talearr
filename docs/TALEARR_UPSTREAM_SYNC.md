# Pulling upstream Chaptarr into Talearr

Remotes: `origin` is `github.com/DanCue44/Talearr`, `upstream` is
`github.com/Chaptarr/chaptarr`. Talearr's `develop` follows upstream `develop`.

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

The brand check also fails on any new C# string literal under `src/` that still says
"Chaptarr" to the user (help texts, validation and health messages, exceptions, logs).
Change the word to Talearr in that string. If it really is a technical identifier or
means the real Chaptarr project, add it to `VISIBLE_NAME_TOKENS` or
`VISIBLE_NAME_ALLOWED_TEXT` in the script instead.

Push the `sync/...` branch. **Talearr CI** (`.github/workflows/talearr-ci.yml`) runs the
same checks. Open a PR into `develop` and merge when green; the Docker workflow then
publishes `ghcr.io/dancue44/talearr:develop`. Use `git merge`, not rebase, so upstream
history stays intact and later merges stay small.

To follow a release instead of the tip: `git merge v0.9.970` (upstream tags are fetched
with `--tags`).

## Where conflicts happen

Talearr keeps its changes small and in few places on purpose:

| Area | Talearr change | On conflict |
|---|---|---|
| Logos and icons (`Logo/`, `frontend/src/Content/Images/`) | files replaced in place, same names | keep ours: `git checkout --ours <file>` |
| `src/Directory.Build.props` | one `<Import>` line at the end | keep upstream's version, re-add the import |
| `src/Talearr.props`, `TalearrBranding.cs`, `build-tools/talearr_*`, `docs/TALEARR_*`, `.github/workflows/talearr-*` | Talearr-only files | never conflict |
| `BuildInfo.cs`, `ConfigFileProvider.cs` (InstanceName), `AppFolderInfo.cs`, `LocalizationService.cs`, `RuleBuilderExtensions.cs`, `InstallUpdateService.cs` | small marked blocks (`// Talearr:`) | take upstream, re-apply the block |
| Grab policy: `ReleasePackDetector.cs`, `MultiBookReleaseSpecification.cs`, `DiscographySpecification.cs`, `DownloadDecisionComparer.cs` and `DownloadDecisionPriorizationService.cs` (optional `ITalearrBookGrabPolicyService` parameter for the per-book abridged policy), `ReleaseSearchService.cs` (optional lazy `ITalearrAbridgedEditionSwitch` parameter and the `ApplyBeforeSearch` call in `BookSearch`), `RedownloadFailedDownloadService.cs`, `ProcessDownloadDecisions.cs` (optional `ITalearrGrabRetryService`, three `TalearrGrabRetryService` calls), `IndexerSettings.js`, `settingsActions.js` | small marked blocks (`// Talearr:`) and optional constructor parameters; the rest lives in `src/NzbDrone.Core/Talearr/GrabPolicy/` and Talearr-only frontend files | take upstream, re-apply the block (the brand check lists each hook) |
| Notification titles and test messages, health check text, startup logs | the word Chaptarr -> Talearr in literals | take upstream, re-apply the word change |
| Frontend strings (`translate.ts`, `login.html`, `index.ejs`, `manifest.json`, About, More Info, a few modals) | the word Chaptarr -> Talearr | take upstream, re-apply |
| `README.md`, `COPYRIGHT.md`, `docker-compose.yml`, `docker-entrypoint.sh` | Talearr text | keep ours, carry over relevant upstream content |
| `src/NzbDrone.Core/MediaFiles/TalearrImport/`, `src/Chaptarr.Core.Test/MediaFiles/TalearrImport/` | archive unpacking and downloaded-file language check | Talearr-only, never conflict |
| `DownloadedBooksImportService.cs` (second constructor, `ProcessPath` unpack wrapper, `Verify` calls), `CompletedDownloadService.cs` (`IsWrongLanguagePayload` branch), `ConfigService.cs`/`IConfigService.cs`/`MediaManagementConfigResource.cs` (two `Talearr*` settings), `MediaManagement.js` (two checkboxes), `Directory.Packages.props`/`Chaptarr.Core.csproj` (SharpCompress) | marked `Talearr` blocks, see [TALEARR_IMPORT.md](TALEARR_IMPORT.md) | take upstream, re-apply the block |
| Phase 1 quick wins: `MetadataServerHealthService.cs` (ReportSuccess), `MetadataServerCheck.cs`, `BookInfoProxy.cs` (RefreshAuthorInfo error paths), `RefreshResult.cs`, `RefreshAuthorService.cs` (RefreshReason.Error log), `Rejection.cs` (Source), `DownloadDecisionMaker.cs` (EvaluateSpec), `ReleaseResource.cs` (RejectionDetail.Label/Source), `AddBookService.cs` (BuildRequestedBookMonitoringConfig), `InteractiveSearchRow.js/.css`, `InteractiveSearch.css`, `searchActions.js` (bookDefaults), `migrate.js` | small marked blocks (`// Talearr:`); the logic lives in `Talearr*` files | take upstream, re-apply the block |
| `EditionSelector.cs`, `BookController.cs` (UpdateBookInternal), `RefreshBookService.cs` (MonitorSingleEdition), `RefreshAuthorService.cs` (NormalizeRemoteBooks), `DownloadedBooksImportService.cs` (CanRetargetSameWorkMatch), `ReleaseTitleMatchScorer.cs` | matching and edition fixes, marked `// Talearr:` (see [TALEARR_MATCHING.md](TALEARR_MATCHING.md)) | take upstream, re-apply the block |
| `Talearr*.cs`, `ReleaseTitleMatchScorer.Talearr.cs` and their `Talearr*Fixture.cs` tests | Talearr-only files | never conflict |
| `RefreshAuthorService.cs` (`ReconcileAuthorBlob` takes `TalearrAuthorRefreshGate.Enter(AuthorMetadataRefreshGate, ...)`), `AddBookService.cs` (`ResolveLocalRequestedBook` uses `TalearrDuplicateBooks.PickRequestedBook`); the rest lives in `src/NzbDrone.Core/Talearr/Catalog/` and `TalearrDuplicatesController.cs` | duplicate rows on add and the duplicate cleanup (see [TALEARR_MATCHING.md](TALEARR_MATCHING.md)), marked `// Talearr:` | take upstream, re-apply the block (the brand check lists each hook) |
| Audiobooks (see [TALEARR_AUDIOBOOKS.md](TALEARR_AUDIOBOOKS.md)): `FileNameBuilder.cs` (`TalearrNamingTokens` calls, `{Book SeriesPosition}` padding, narrator fallback), `PartAssignmentHelper.cs` (`AudiobookTrackOrder.Order`), `FileMatchingService.cs` (`BuildTalearrSharedFolderBookKeys` and the `talearr-book` sub key), `CommandResource.cs` (command display name), `NamingModal.js`/`NamingOption.js` (tokens, layouts, `isLiteral`), `ConversionSettings*.js` (M4B section), `BookDetails*.js` (Convert to M4B), `commandNames.js`, `settingsActions.js` | small marked `Talearr` blocks; the logic lives in `src/NzbDrone.Core/Talearr/Audiobook/` | take upstream, re-apply the block (the brand check lists each hook) |
| Phase 2 recordings: `src/NzbDrone.Core/Talearr/Recordings/`, `src/Chaptarr.Api.V1/Config/TalearrRecordingsConfigController.cs`, `src/Chaptarr.Core.Test/Talearr/Recordings/` | `talearr.db` cache and `talearr.recordings.*` settings (see [TALEARR_RECORDINGS_DESIGN.md](TALEARR_RECORDINGS_DESIGN.md)) | Talearr-only files, never conflict |
| `FileNameBuilder.cs` (`partial`, optional `Lazy<ITalearrRecordingFolders>` parameter, `TalearrDisambiguateRecordingFolder` call; the rest is in `FileNameBuilder.Talearr.cs`), `ImportApprovedBooks.cs` (optional `ITalearrRecordingFolders` and `ITalearrUpgradeGuard` parameters; the upgrade guard and keep both block and the `FindImportConflict` block before the per-file loop) | Phase 2 recordings: copy folder disambiguation and the upgrade guard, marked `// Talearr:` | take upstream, re-apply the block (the brand check lists each hook) |
| `TaskManager.cs` (RefreshAudibleRecordingFactsCommand scheduled task), `AudibleCatalogProxy.cs` (`UserAgents` accessor), `DownloadDecisionComparer.cs`, `DownloadDecisionPriorizationService.cs` and `FileNameBuilder.cs` (optional `ITalearrAbridgedResolver` parameter) | Phase 2 recordings hooks, marked `// Talearr:` | take upstream, re-apply the block (the brand check lists each hook) |
| `BookDetails.js` (`BookRecordings` above the tabs), `MetadataSettings.js` (second save callback for `TalearrRecordingsConnector`), `settingsActions.js` (`talearrRecordings` section) | Phase 2 recordings UI, marked `Talearr`; the rest lives in `frontend/src/Book/Recordings/`, `Settings/Metadata/TalearrRecordings*.js` and `Store/Actions/Settings/talearrRecordings.js` | take upstream, re-apply the block (the brand check lists each hook) |
| Phase 3 Audiobookshelf: `src/NzbDrone.Core/Talearr/Audiobookshelf/`, `AudioBookShelfSettings.Talearr.cs`, `src/Chaptarr.Api.V1/Talearr/TalearrAudiobookshelfController.cs`, `frontend/src/Book/Audiobookshelf/`, `TalearrAbsSyncStatus.js`, `src/Chaptarr.Core.Test/Talearr/Audiobookshelf/` | library sync, owned-copy specification, badge (see [TALEARR_AUDIOBOOKSHELF.md](TALEARR_AUDIOBOOKSHELF.md)) | Talearr-only files, never conflict |
| `AudioBookShelfSettings.cs` (`partial`), `AudioBookShelf.cs` (`TalearrAbsClient.TestLibraryRead` in `Test`), `TaskManager.cs` (`SyncAudiobookshelfLibraryCommand` task), `TalearrDatabase.cs` (schema version 2; version 3 adds the per-book abridged values), `BookDetailsHeader.js` (`TalearrAbsBadge`), `EditNotificationModalContent.js` (`TalearrAbsSyncStatus`) | Phase 3 Audiobookshelf hooks, marked `Talearr` | take upstream, re-apply the block (the brand check lists each hook) |
| `FileNameBuilder.cs` (`AddBookTokens` and `AddGraphicAudioTokens` take the title from `TalearrBookTitle.ForNaming`), `TalearrM4bService.cs` (`BuildJob`); the logic lives in `src/NzbDrone.Core/Talearr/Naming/` | Phase 3 folder title: names use the book title without format noise (see [TALEARR_AUDIOBOOKS.md](TALEARR_AUDIOBOOKS.md#book-title-not-the-edition-title)) | take upstream, re-apply the block (the brand check lists each hook) |
| `AudioBookShelfMetadata.cs` (`BookImages` returns a `TalearrSidecarImage` with every candidate, `BuildMetadata` title and subtitle from `TalearrSidecarText`, `AddStringArray` name cleanup, `GetOtherEditions`), `MetadataService.cs` (optional `ITalearrSidecarCoverWriter` parameter, `TalearrSidecarImage.SourceKeyOf` for the source hash, the `talearrCover` block in `ProcessBookImages`), `TalearrM4bService.cs` (`TalearrSidecarText.Name` for author and narrator); the logic lives in `src/NzbDrone.Core/Talearr/Metadata/` | sidecar title, names and cover choice (see [TALEARR_AUDIOBOOKSHELF.md](TALEARR_AUDIOBOOKSHELF.md#sidecar-files)), marked `Talearr` | take upstream, re-apply the block (the brand check lists each hook) |
| `AudioBookShelfMetadataFixture.cs` (title and book cover expectations) | expectations flipped, marked `Talearr:` | take upstream, re-apply the flip |
| Phase 3 series monitoring: `src/NzbDrone.Core/Talearr/SeriesMonitoring/`, `src/Chaptarr.Api.V1/Talearr/TalearrSeriesController.cs`, `frontend/src/Series/Talearr/`, `src/Chaptarr.Core.Test/Talearr/SeriesMonitoring/` | series rules in `talearr.series.monitor.*` Config rows (see [TALEARR_SERIES.md](TALEARR_SERIES.md)) | Talearr-only files, never conflict |
| `TaskManager.cs` (`ApplySeriesMonitoringCommand` task), `AppRoutes.js` (`/talearr/series` routes), `PageSidebar.js` (Library > Series), `AuthorDetailsSeries.js`/`.css` (series page link) | Phase 3 series monitoring hooks, marked `Talearr` | take upstream, re-apply the block (the brand check lists each hook) |
| Phase 3 wishlists: `src/NzbDrone.Core/Talearr/Wishlists/`, `src/Chaptarr.Api.V1/Talearr/TalearrWishlistsController.cs`, `frontend/src/Settings/ImportLists/Talearr/`, `src/Chaptarr.Core.Test/Talearr/Wishlists/` | wishlists in `talearr.wishlist.*` Config rows (see [TALEARR_WISHLISTS.md](TALEARR_WISHLISTS.md)) | Talearr-only files, never conflict |
| `TaskManager.cs` (`SyncWishlistsCommand` task), `ImportListSettings.js` (`<TalearrWishlists />`) | Phase 3 wishlists hooks, marked `Talearr` | take upstream, re-apply the block (the brand check lists each hook) |
| Migration: `src/NzbDrone.Core/Talearr/Migration/`, `src/Chaptarr.Api.V1/Talearr/TalearrMigrationController.cs`, `frontend/src/System/Talearr/`, `src/Chaptarr.Core.Test/Talearr/Migration/`, `unraid/talearr.xml` | Readarr/Bookshelf/LazyLibrarian migration (see [TALEARR_MIGRATION.md](TALEARR_MIGRATION.md)) and the Unraid template | Talearr-only files, never conflict |
| `AppRoutes.js` (`/system/migrate`), `PageSidebar.js` (System > Migrate), `Chaptarr.Core.Test.csproj` (migration fixtures copied to output) | migration hooks, marked `Talearr` | take upstream, re-apply the block (the brand check lists each hook) |
| Phase 3 format policy: `src/NzbDrone.Core/Talearr/FormatPolicy/`, `src/Chaptarr.Api.V1/Config/TalearrFormatPolicyConfigController.cs`, `frontend/src/Settings/Indexers/Options/TalearrFormatPolicy.*`, `src/Chaptarr.Core.Test/Talearr/FormatPolicy/` | format policy settings and decision specification (see [TALEARR_FORMAT_POLICY.md](TALEARR_FORMAT_POLICY.md)) | Talearr-only files, never conflict |
| `IndexerSettings.js` (`<TalearrFormatPolicy />`) | Phase 3 format policy hook, marked `Talearr` | take upstream, re-apply the block (the brand check lists it) |
| Scorer and edition tests changed to Talearr behavior (`ReleaseTitleMatchScorerFixture.cs`, `ReleaseTitleMatchSpecificationFixture.cs`, `EditionSelectorInvariantFixture.cs`, one row in `release-title-scorecard.ndjson`) | expectations flipped, marked `Talearr:` | take upstream, re-apply the flip |

Translation files (`src/NzbDrone.Core/Localization/Core/*.json`) are **not** edited:
`TalearrBranding` rewrites "Chaptarr" to "Talearr" when they load, so new upstream strings
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
take those as they come (they are the "Based on Chaptarr" version). Bump Talearr's own
version in `src/Talearr.props` when releasing Talearr: patch for upstream syncs and
fixes, minor for Talearr features. Tag `vX.Y.Z` on `main` to publish a versioned image.

## Upstream workflows

The upstream workflows (`build.yml`, `release.yml`, `docker-publish.yml`) stay in the repo
so merges are clean, but are meant to stay disabled in DanCue44/Talearr (they target
Chaptarr's Docker Hub and run a three-OS matrix):

```bash
gh workflow disable "Build Chaptarr" -R DanCue44/Talearr
gh workflow disable "Release" -R DanCue44/Talearr
gh workflow disable "Docker Build and Publish" -R DanCue44/Talearr
```
