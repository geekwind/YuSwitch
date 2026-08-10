#!/usr/bin/env bash
# Assemble a macOS .app bundle + DMG from a single-file publish output.
#
#   make_macos_app.sh <publish-dir> <logo.png> <version-label> <rid> [out-dir]
#
#   publish-dir    folder containing the single-file "YuSwitch" executable
#   logo.png       square app-icon source (1024x1024 or larger, any format sips reads)
#   version-label  e.g. v0.0.1 (tag push) or "latest" (manual dispatch)
#   rid            osx-x64 / osx-arm64 — used in the DMG filename
#   out-dir        where YuSwitch-<rid>-<version>.dmg lands (default: dist)
#
# Runs on a macOS runner only: sips + iconutil build the .icns, hdiutil makes
# the DMG. The .app wraps the SAME single-file binary as the headless tar.gz —
# opened normally it launches the Photino GUI; run with --headless it's the
# plain server.
set -euo pipefail

PUBLISH_DIR="${1:?usage: make_macos_app.sh <publish-dir> <logo.png> <version-label> <rid> [out-dir]}"
LOGO="${2:?}"
VERSION_LABEL="${3:?}"
RID="${4:?}"
OUT_DIR="${5:-dist}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

APP_NAME="YuSwitch"
BINARY="${PUBLISH_DIR}/${APP_NAME}"
[ -x "$BINARY" ] || { echo "::error::single-file binary not found: $BINARY"; exit 1; }

BUNDLE_VER="${VERSION_LABEL#v}"
case "$BUNDLE_VER" in *[!0-9.]*) BUNDLE_VER="0.0.1";; esac

APP_BUNDLE="${OUT_DIR}/${APP_NAME}.app"
ICONSET_DIR="$(mktemp -d)/AppIcon.iconset"

mkdir -p "$OUT_DIR"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources" "$ICONSET_DIR"

# --- App icon: square png → iconset → icns ---
for spec in \
  "16 icon_16x16.png" \
  "32 icon_16x16@2x.png" \
  "32 icon_32x32.png" \
  "64 icon_32x32@2x.png" \
  "128 icon_128x128.png" \
  "256 icon_128x128@2x.png" \
  "256 icon_256x256.png" \
  "512 icon_256x256@2x.png" \
  "512 icon_512x512.png" \
  "1024 icon_512x512@2x.png"; do
  size="${spec%% *}"; name="${spec##* }"
  sips -z "$size" "$size" "$LOGO" --out "$ICONSET_DIR/$name" >/dev/null
done
iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/AppIcon.icns"

# --- Executable ---
cp "$BINARY" "$APP_BUNDLE/Contents/MacOS/${APP_NAME}"
chmod +x "$APP_BUNDLE/Contents/MacOS/${APP_NAME}"

# --- Menu-bar helper (menu-bar status item + window hide/show; Photino.NET
# has no status-item API). Compiled with clang, which every macOS runner has.
# Placed next to the binary so DllImport("YuSwitchHelper") resolves it. ---
HELPER_SRC="${SCRIPT_DIR}/macos/StatusItem.m"
HELPER_DYLIB="$APP_BUNDLE/Contents/MacOS/libYuSwitchHelper.dylib"
if [ -f "$HELPER_SRC" ]; then
  clang -fobjc-arc -dynamiclib "$HELPER_SRC" \
    -o "$HELPER_DYLIB" -framework Cocoa -Wno-deprecated-declarations
  echo "Wrote $HELPER_DYLIB"
else
  echo "::warning::$HELPER_SRC not found; app ships without the menu-bar helper (close = quit)"
fi

# --- Info.plist ---
cat > "$APP_BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key><string>zh_CN</string>
    <key>CFBundleExecutable</key><string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key><string>ai.yuswitch.app</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>CFBundleName</key><string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key><string>禹枢</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${BUNDLE_VER}</string>
    <key>CFBundleVersion</key><string>${BUNDLE_VER}</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>10.15</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsLocalNetworking</key><true/>
    </dict>
</dict>
</plist>
PLIST

# --- DMG (drag-to-Applications install) ---
DMG="${OUT_DIR}/${APP_NAME}-${RID}-${VERSION_LABEL}.dmg"
hdiutil create -volname "${APP_NAME}" -srcfolder "$APP_BUNDLE" -ov -format UDZO "$DMG" >/dev/null
echo "Wrote ${DMG}"
