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
]

# (folder, file glob, regex, why). No file under the folder may match.
MUST_NOT_HAVE_IN_TREE = [
    ("src/NzbDrone.Core/Datastore/Migration", "*.cs", r"(?i)talearr", "TaleArr adds no chaptarr.db migrations"),
    ("src/NzbDrone.Core/Talearr", "*.cs", r"IMainDatabase|\[Migration\(", "TaleArr code keeps its data out of the chaptarr.db schema"),
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

    for rel, pattern, why in MUST_HAVE_BRAND + MUST_KEEP_UPSTREAM + MUST_KEEP_TALEARR_HOOKS + MUST_KEEP_RECORDINGS:
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
             len(MUST_KEEP_RECORDINGS) + len(MUST_NOT_HAVE) + len(MUST_NOT_HAVE_IN_TREE))
    print(f"TaleArr brand check passed ({total} checks).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
