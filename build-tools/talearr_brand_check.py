#!/usr/bin/env python3
"""TaleArr rebrand guard.

Run after merging upstream Chaptarr changes. It fails if an upstream merge
quietly undid the user-visible TaleArr branding, or if the fork drifted away
from the technical identifiers that keep TaleArr a drop-in replacement for
Chaptarr (config prefix, database and config file names, assembly name).

Usage: python3 build-tools/talearr_brand_check.py
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# (file, regex, why). Each regex must match somewhere in the file.
MUST_HAVE_BRAND = [
    ("frontend/src/index.ejs", r"<title>TaleArr</title>", "browser tab title"),
    ("frontend/src/login.html", r"<title>Login - TaleArr</title>", "login page title"),
    ("frontend/src/login.html", r"<span>TaleArr</span>", "login page name"),
    ("frontend/src/Content/Images/Icons/manifest.json", r'"name": "TaleArr"', "PWA manifest name"),
    ("frontend/src/Utilities/String/translate.ts", r"tokens\.appName = 'TaleArr'", "UI {appName} token"),
    ("frontend/src/System/Status/About/About.js", r"talearrVersion", "TaleArr version in System > Status"),
    ("frontend/src/System/Status/MoreInfo/MoreInfo.js", r"DanCue44/TaleArr", "More Info source link"),
    ("src/NzbDrone.Common/EnvironmentInfo/BuildInfo.cs", r'ProductName \{ get; \} = "TaleArr"', "backend product name"),
    ("src/NzbDrone.Common/EnvironmentInfo/BuildInfo.cs", r'"TalearrVersion"', "TaleArr version metadata read"),
    ("src/NzbDrone.Core/Localization/LocalizationService.cs", r"TalearrBranding\.Apply", "translation rebranding hook"),
    ("src/NzbDrone.Core/Localization/LocalizationService.cs", r'TryAdd\("appName", BuildInfo\.ProductName\)', "backend {appName} token"),
    ("src/NzbDrone.Core/Notifications/NotificationBase.cs", r'"TaleArr - "', "notification titles"),
    ("src/NzbDrone.Core/Configuration/ConfigFileProvider.cs", r"BuildInfo\.ProductName", "instance name default"),
    ("src/NzbDrone.Host/Bootstrap.cs", r'"Starting TaleArr', "startup log line"),
    ("src/Directory.Build.props", r'<Import Project="\$\(MSBuildThisFileDirectory\)Talearr\.props" />', "Talearr.props import"),
    ("src/Talearr.props", r"<TalearrVersion[^>]*>\d+\.\d+\.\d+</TalearrVersion>", "TaleArr version"),
    ("docker-entrypoint.sh", r"TaleArr Docker Entrypoint", "container log banner"),
    ("frontend/src/Components/Page/Header/PageHeader.js", r"wordmarkAccent", "header wordmark"),
    ("frontend/src/System/Status/Status.js", r"<WorksBestWith />", "Works best with block"),
    ("Logo/talearr-icon-master.png", r"PNG", "icon master"),
]

# Technical identity that must stay upstream so TaleArr can run on a Chaptarr
# config folder and database unchanged.
MUST_KEEP_UPSTREAM = [
    ("src/NzbDrone.Common/EnvironmentInfo/BuildInfo.cs", r'AppName \{ get; \} = "Chaptarr"', "technical app name"),
    ("src/NzbDrone.Common/Extensions/PathExtensions.cs", r'DB = "chaptarr\.db"', "database file name"),
    ("src/NzbDrone.Common/Extensions/PathExtensions.cs", r'APP_CONFIG_FILE = "config\.xml"', "config file name"),
    ("src/NzbDrone.Host/Bootstrap.cs", r'"Chaptarr:Postgres"', "Chaptarr__ env config prefix"),
    ("Dockerfile.cross", r'"/app/Chaptarr\.dll"', "entry assembly name"),
    ("Dockerfile.cross", r"EXPOSE 8789", "default port"),
    ("src/NzbDrone.Core/Update/InstallUpdateService.cs", r"Built-in updater disabled in the TaleArr image", "in-app updater disabled in Docker"),
]

# TaleArr feature hooks inside upstream files. An upstream merge that drops one of these
# silently turns the feature off, so check they are still wired in.
MUST_KEEP_TALEARR_HOOKS = [
    ("src/NzbDrone.Core/Parser/ReleasePackDetector.cs", r"TalearrBundleDetector\.Detect", "grab policy: bundle detection hook"),
    ("src/NzbDrone.Core/DecisionEngine/Specifications/MultiBookReleaseSpecification.cs", r"AllowBundles", "grab policy: bundles opt-in"),
    ("src/NzbDrone.Core/DecisionEngine/DownloadDecisionComparer.cs", r"CompareUnabridged", "grab policy: unabridged ranking"),
    ("src/NzbDrone.Core/DecisionEngine/DownloadDecisionPriorizationService.cs", r"TalearrGrabPolicyService\.Resolve", "grab policy: ranking uses the policy"),
    ("src/NzbDrone.Core/Download/RedownloadFailedDownloadService.cs", r"_talearrRetryService", "grab policy: retry cap and history notes"),
    ("frontend/src/Settings/Indexers/IndexerSettings.js", r"TalearrGrabPolicyConnector", "grab policy: settings section"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"TalearrNamingTokens\.Add\(", "audiobooks: {Book Year} and abridged tokens"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"TalearrNamingTokens\.FormatSeriesPosition", "audiobooks: zero-padded series position"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"TalearrNamingTokens\.GetOriginalFileName", "audiobooks: multi-disc file names kept apart"),
    ("src/NzbDrone.Core/MediaFiles/PartAssignmentHelper.cs", r"AudiobookTrackOrder\.Order", "audiobooks: disc and track order on import"),
    ("src/NzbDrone.Core/MediaFiles/BookImport/FileMatchingService.cs", r"BuildTalearrSharedFolderBookKeys", "audiobooks: two books in one folder are not merged"),
    ("frontend/src/Settings/Conversion/ConversionSettings.js", r"TalearrM4bConnector", "audiobooks: merge to M4B setting"),
    ("frontend/src/Book/Details/BookDetails.js", r"onConvertToM4bPress", "audiobooks: Convert to M4B action"),
    ("frontend/src/Settings/MediaManagement/Naming/NamingModal.js", r"audiobookLayouts", "audiobooks: naming tokens and layouts"),
]

# Phase 2 recordings wiring (docs/TALEARR_RECORDINGS_DESIGN.md). TaleArr-only files, but the
# drop-in promise depends on what they contain, so pin the parts that matter.
MUST_KEEP_RECORDINGS = [
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrDatabase.cs", r'FileName = "talearr\.db"', "recordings: cache lives in its own talearr.db"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrDatabase.cs", r"PRAGMA user_version", "recordings: talearr.db versions itself, not through FluentMigrator"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrRecordingsSettings.cs", r'"talearr\.recordings\.marketplace"', "recordings: settings are talearr.* Config rows"),
    ("src/Chaptarr.Api.V1/Config/TalearrRecordingsConfigController.cs", r'V1ApiController\("config/talearrrecordings"\)', "recordings: settings route used by AllSeerr detection"),
    ("src/NzbDrone.Core/Jobs/TaskManager.cs", r"RefreshAudibleRecordingFactsCommand", "recordings: scheduled Audible facts refresh"),
    ("src/NzbDrone.Core/MetadataSource/Audible/AudibleCatalogProxy.cs", r"UserAgents => AudibleUserAgents", "recordings: Audible client shares upstream's user agents"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrAudibleClient.cs", r"RateLimit = MinimumInterval", "recordings: Audible requests are rate limited"),
    ("src/NzbDrone.Core/DecisionEngine/Specifications/AbridgedReleaseSpecification.cs", r"WantsAbridged\(subject, _abridgedResolver\)", "recordings: abridged soft filter uses the resolved value"),
    ("src/NzbDrone.Core/DecisionEngine/DownloadDecisionComparer.cs", r"WantsAbridged\(abridged\.RemoteBook, _abridgedResolver\)", "recordings: abridged ranking uses the resolved value"),
    ("src/NzbDrone.Core/DecisionEngine/DownloadDecisionPriorizationService.cs", r"_abridgedResolver\);", "recordings: ranking gets the abridged resolver"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"TalearrNamingTokens\.Add\(tokenHandlers, edition, _abridgedResolver\)", "recordings: {Abridged} naming uses the resolved value"),
    ("src/Chaptarr.Api.V1/Talearr/TalearrRecordingsController.cs", r'V1ApiController\("talearr/recordings"\)', "recordings: API route for AllSeerr and NoBookEnds"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrRecordingsService.cs", r"_bookService\.AddWantedEdition\(", "recordings: new recordings are upstream wanted copies"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrEditionKey.cs", r"GetTrustedForeignEditionId", "recordings: facts keyed the way upstream keys cloned editions"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"public partial class FileNameBuilder", "recordings: FileNameBuilder stays partial for the TaleArr half"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"components = TalearrDisambiguateRecordingFolder\(", "recordings: copy folders are disambiguated"),
    ("src/NzbDrone.Core/Organizer/FileNameBuilder.cs", r"Lazy<ITalearrRecordingFolders> recordingFolders = null", "recordings: folder rule is optional and lazy (no container cycle)"),
    ("src/NzbDrone.Core/MediaFiles/BookImport/ImportApprovedBooks.cs", r"TalearrRecordingFolders\.FindImportConflict\(", "recordings: import refuses a folder holding another recording's files"),
    ("src/NzbDrone.Core/MediaFiles/BookImport/ImportApprovedBooks.cs", r"_talearrUpgradeGuard\.Evaluate\(", "upgrades: recording-aware upgrade guard runs before old files are touched"),
    ("src/NzbDrone.Core/MediaFiles/BookImport/ImportApprovedBooks.cs", r"ResolveKeepBothDestination\(book\)", "upgrades: keep both reroutes an unabridged download to its own recording"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrRecordingsSettings.cs", r'"talearr\.upgrade\.guard"', "upgrades: guard settings are talearr.* Config rows"),
    ("frontend/src/Book/Details/BookDetails.js", r"<BookRecordings", "recordings: Recordings section on the book page"),
    ("frontend/src/Settings/Metadata/MetadataSettings.js", r"<TalearrRecordingsConnector", "recordings: settings block on Settings > Metadata"),
    ("frontend/src/Store/Actions/settingsActions.js", r"talearrRecordings\.actionHandlers", "recordings: settings store section"),
    ("frontend/src/Book/Recordings/BookRecordings.js", r"url: '/talearr/recordings'", "recordings: book page uses the TaleArr recordings API"),
]

# Phase 3 Audiobookshelf library awareness (docs/TALEARR_AUDIOBOOKSHELF.md). The connection is
# upstream's own AudioBookShelf provider row; TaleArr adds keys to its Settings JSON and keeps the
# library cache in talearr.db. Pin the parts the rollback promise depends on.
MUST_KEEP_AUDIOBOOKSHELF = [
    ("src/NzbDrone.Core/Notifications/AudioBookShelf/AudioBookShelfSettings.cs", r"public partial class AudioBookShelfSettings", "abs: upstream settings stay partial for the TaleArr fields"),
    ("src/NzbDrone.Core/Notifications/AudioBookShelf/AudioBookShelfSettings.Talearr.cs", r"public bool TalearrSyncLibrary \{ get; set; \} = true;", "abs: library sync is on by default"),
    ("src/NzbDrone.Core/Notifications/AudioBookShelf/AudioBookShelfSettings.Talearr.cs", r"public bool TalearrSkipOwned \{ get; set; \} = true;", "abs: skip books already in Audiobookshelf, on by default, can be turned off"),
    ("src/NzbDrone.Core/Notifications/AudioBookShelf/AudioBookShelf.cs", r"TalearrAbsClient\.TestLibraryRead\(", "abs: Test button checks the libraries can be read"),
    ("src/NzbDrone.Core/ThingiProvider/ProviderRepository.cs", r"PropertyNameCaseInsensitive = true", "abs: provider settings JSON read the way stock Chaptarr reads it"),
    ("src/NzbDrone.Core/Talearr/Recordings/TalearrDatabase.cs", r"CREATE TABLE IF NOT EXISTS AbsItems", "abs: library cache lives in talearr.db"),
    ("src/NzbDrone.Core/Jobs/TaskManager.cs", r"SyncAudiobookshelfLibraryCommand", "abs: scheduled library sync"),
    ("src/NzbDrone.Core/Talearr/Audiobookshelf/TalearrInAudiobookshelfSpecification.cs", r'Category = "In Audiobookshelf"', "abs: rejection label in interactive search"),
    ("src/NzbDrone.Core/Talearr/Audiobookshelf/TalearrInAudiobookshelfSpecification.cs", r"Decision\.RejectSoftFilter\(", "abs: owned copies are a soft filter, still grabbable on purpose"),
    ("src/NzbDrone.Core/Talearr/Audiobookshelf/TalearrAbsLibraryService.cs", r"AbridgementFits\(marker, row\.WantsAbridged\)", "abs: an abridged copy does not satisfy a wanted unabridged recording"),
    ("src/Chaptarr.Api.V1/Talearr/TalearrAudiobookshelfController.cs", r'V1ApiController\("talearr/audiobookshelf"\)', "abs: additive API route"),
    ("frontend/src/Book/Details/BookDetailsHeader.js", r"<TalearrAbsBadge", "abs: In Audiobookshelf badge on the book page"),
    ("frontend/src/Settings/Notifications/Notifications/EditNotificationModalContent.js", r"<TalearrAbsSyncStatus", "abs: sync status and Sync now on the connection"),
]

# Phase 3 format policy (docs/TALEARR_FORMAT_POLICY.md).
MUST_KEEP_FORMAT_POLICY = [
    ("src/NzbDrone.Core/Talearr/FormatPolicy/TalearrFormatPolicy.cs", r'DefaultKey = "talearr\.formatpolicy\.default"', "format policy: settings are talearr.* Config rows"),
    ("src/NzbDrone.Core/Talearr/FormatPolicy/TalearrFormatPolicy.cs", r"public FormatPolicyMode Default \{ get; set; \} = FormatPolicyMode\.Both;", "format policy: upstream behaviour by default"),
    ("src/NzbDrone.Core/Talearr/FormatPolicy/TalearrFormatPolicySpecification.cs", r"Decision\.RejectSoftFilter\(reason, Category\)", "format policy: a soft filter, still grabbable from interactive search"),
    ("src/NzbDrone.Core/Talearr/FormatPolicy/TalearrFormatPolicySpecification.cs", r"TalearrRecordingGroupService\.IsCopySlug", "format policy: additional recordings are not the book's audiobook"),
    ("src/Chaptarr.Api.V1/Config/TalearrFormatPolicyConfigController.cs", r'V1ApiController\("config/talearrformatpolicy"\)', "format policy: additive API route"),
    ("frontend/src/Settings/Indexers/IndexerSettings.js", r"<TalearrFormatPolicy />", "format policy: section on Settings > Indexers"),
]

# (folder, file glob, regex, why). No file under the folder may match.
MUST_NOT_HAVE_IN_TREE = [
    ("src/NzbDrone.Core/Datastore/Migration", "*.cs", r"(?i)talearr", "TaleArr adds no chaptarr.db migrations"),
    ("src/NzbDrone.Core/Talearr", "*.cs", r"IMainDatabase|\[Migration\(", "TaleArr code keeps its data out of the chaptarr.db schema"),
    ("src/NzbDrone.Core/Talearr", "*.cs", r"NotificationBase<|IProviderConfig\b", "TaleArr adds no provider implementation (stock Chaptarr deletes unknown ones on start)"),
    ("src/NzbDrone.Core/Talearr/Audiobookshelf", "*.cs", r"HttpMethod\.(Post|Put|Patch|Delete)", "TaleArr only reads Audiobookshelf; scans stay upstream's"),
]

# Visible strings that should not come back in UI shells after a merge.
MUST_NOT_HAVE = [
    ("frontend/src/index.ejs", r"<title>Chaptarr</title>", "upstream tab title"),
    ("frontend/src/login.html", r"Login - Chaptarr", "upstream login title"),
    ("frontend/src/Content/Images/Icons/manifest.json", r'"name": "Chaptarr"', "upstream manifest name"),
]


def read(rel: str) -> str | None:
    path = ROOT / rel
    if not path.exists():
        return None
    return path.read_text(encoding="utf-8", errors="replace")


def main() -> int:
    failures: list[str] = []

    for rel, pattern, why in MUST_HAVE_BRAND + MUST_KEEP_UPSTREAM + MUST_KEEP_TALEARR_HOOKS + MUST_KEEP_RECORDINGS + MUST_KEEP_AUDIOBOOKSHELF + MUST_KEEP_FORMAT_POLICY:
        text = read(rel)
        if text is None:
            failures.append(f"missing file {rel} ({why})")
        elif not re.search(pattern, text):
            failures.append(f"{rel}: expected /{pattern}/ ({why})")

    for rel, pattern, why in MUST_NOT_HAVE:
        text = read(rel)
        if text is not None and re.search(pattern, text):
            failures.append(f"{rel}: found /{pattern}/ ({why})")

    for folder, glob, pattern, why in MUST_NOT_HAVE_IN_TREE:
        base = ROOT / folder
        if not base.exists():
            failures.append(f"missing folder {folder} ({why})")
            continue
        for path in sorted(base.rglob(glob)):
            if re.search(pattern, path.read_text(encoding="utf-8", errors="replace")):
                failures.append(f"{path.relative_to(ROOT)}: found /{pattern}/ ({why})")

    if failures:
        print("TaleArr brand check failed:")
        for failure in failures:
            print(f"  - {failure}")
        print("Re-apply the TaleArr change (see docs/TALEARR_UPSTREAM_SYNC.md).")
        return 1

    total = (len(MUST_HAVE_BRAND) + len(MUST_KEEP_UPSTREAM) + len(MUST_KEEP_TALEARR_HOOKS) +
             len(MUST_KEEP_RECORDINGS) + len(MUST_KEEP_AUDIOBOOKSHELF) + len(MUST_KEEP_FORMAT_POLICY) + len(MUST_NOT_HAVE) + len(MUST_NOT_HAVE_IN_TREE))
    print(f"TaleArr brand check passed ({total} checks).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
