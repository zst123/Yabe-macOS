#!/usr/bin/env bash
# Fetches the original YABE source (SourceForge SVN) into external/yabe-svn and
# verifies the toolchain. The macOS GUI in src/ compiles against those files
# in place; nothing from the SVN tree is copied or modified.
#
# Usage: ./setup.sh [--revision N] [--install-deps]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
SVN_URL="https://svn.code.sf.net/p/yetanotherbacnetexplorer/code/trunk"
DEST="$ROOT/external/yabe-svn"
REV="HEAD"
INSTALL_DEPS=0

while [ $# -gt 0 ]; do
  case "$1" in
    --revision) REV="$2"; shift 2 ;;
    --install-deps) INSTALL_DEPS=1; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

export PATH="$HOME/.dotnet:$PATH"

if ! command -v svn >/dev/null; then
  if [ "$INSTALL_DEPS" = 1 ] && command -v brew >/dev/null; then
    brew install subversion
  else
    echo "svn not found. Install it (brew install subversion) or re-run with --install-deps." >&2
    exit 1
  fi
fi

if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | grep -q '^8\.'; then
  if [ "$INSTALL_DEPS" = 1 ]; then
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "$ROOT/external/dotnet-install.sh" 2>/dev/null \
      || { mkdir -p "$ROOT/external"; curl -sSL https://dot.net/v1/dotnet-install.sh -o "$ROOT/external/dotnet-install.sh"; }
    bash "$ROOT/external/dotnet-install.sh" --channel 8.0 --install-dir "$HOME/.dotnet"
  else
    echo ".NET 8 SDK not found. Install it or re-run with --install-deps (installs to ~/.dotnet)." >&2
    exit 1
  fi
fi

mkdir -p "$ROOT/external"
if [ -d "$DEST/.svn" ]; then
  echo "Updating existing checkout to r$REV ..."
  svn update --quiet -r "$REV" "$DEST"
else
  echo "Checking out YABE trunk r$REV ..."
  svn checkout --quiet -r "$REV" "$SVN_URL" "$DEST"
fi

echo "YABE source at $(svn info --show-item revision "$DEST") -> $DEST"
echo "Next: ./run.sh"
