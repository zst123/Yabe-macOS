#!/usr/bin/env bash
# Builds and launches the macOS GUI. Run ./setup.sh first.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export PATH="$HOME/.dotnet:$PATH"
[ -d "$ROOT/external/yabe-svn/Yabe" ] || { echo "Run ./setup.sh first." >&2; exit 1; }
exec dotnet run --project "$ROOT/src/YabeMac.App" -c Release "$@"
