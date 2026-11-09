#!/usr/bin/env bash
# Builds build/Yabe.app (proper macOS bundle: Dock/menu-bar name "Yabe" and the Yabe icon) and optionally opens it.
# usage: ./make-app.sh [--open]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
export PATH="$HOME/.dotnet:$PATH"
[ -d "$ROOT/external/yabe-svn/Yabe" ] || { echo "Run ./setup.sh first." >&2; exit 1; }
APP="$ROOT/build/Yabe.app"
case "$(uname -m)" in arm64) RID=osx-arm64;; *) RID=osx-x64;; esac

rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# Self-contained (no DOTNET_ROOT needed). No PDBs, and PathMap strips the build dir from embedded paths,
# so the bundle contains no username / local paths.
dotnet publish "$ROOT/src/YabeMac.App" -c Release -r "$RID" --self-contained true \
  -p:DebugType=none -p:DebugSymbols=false -p:DeterministicSourcePaths=true "-p:PathMap=$ROOT=/src" \
  -o "$APP/Contents/MacOS" >/dev/null
mv "$APP/Contents/MacOS/YabeMac.App" "$APP/Contents/MacOS/Yabe"

# icon: assets/Yabe-1024.png -> Yabe.icns
ICONSET="$ROOT/build/Yabe.iconset"; rm -rf "$ICONSET"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/assets/Yabe-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/assets/Yabe-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Yabe.icns"; rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Yabe</string>
  <key>CFBundleDisplayName</key><string>Yabe</string>
  <key>CFBundleIdentifier</key><string>net.sourceforge.yabe.mac</string>
  <key>CFBundleExecutable</key><string>Yabe</string>
  <key>CFBundleIconFile</key><string>Yabe</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict></plist>
EOF
echo "Built $APP"
[ "${1:-}" = "--open" ] && open "$APP"
exit 0
