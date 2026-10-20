#!/usr/bin/env bash
# Prints the Talearr version (src/Talearr.props) and the upstream Chaptarr
# version (package.json) as shell assignments, for CI and local scripts.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
talearr="$(sed -n 's:.*<TalearrVersion[^>]*>\([0-9][0-9.]*\)</TalearrVersion>.*:\1:p' "$root/src/Talearr.props" | head -n1)"
upstream="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1], encoding='utf-8'))['version'])" "$root/package.json")"
if [[ -z "$talearr" || -z "$upstream" ]]; then
  echo "Could not read versions" >&2
  exit 1
fi
echo "TALEARR_VERSION=$talearr"
echo "UPSTREAM_VERSION=$upstream"
