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

    for rel, pattern, why in MUST_HAVE_BRAND + MUST_KEEP_UPSTREAM:
        text = read(rel)
        if text is None:
            failures.append(f"missing file {rel} ({why})")
        elif not re.search(pattern, text):
            failures.append(f"{rel}: expected /{pattern}/ ({why})")

    for rel, pattern, why in MUST_NOT_HAVE:
        text = read(rel)
        if text is not None and re.search(pattern, text):
            failures.append(f"{rel}: found /{pattern}/ ({why})")

    if failures:
        print("TaleArr brand check failed:")
        for failure in failures:
            print(f"  - {failure}")
        print("Re-apply the TaleArr change (see docs/TALEARR_UPSTREAM_SYNC.md).")
        return 1

    print(f"TaleArr brand check passed ({len(MUST_HAVE_BRAND) + len(MUST_KEEP_UPSTREAM) + len(MUST_NOT_HAVE)} checks).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
