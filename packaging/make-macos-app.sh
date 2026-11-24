#!/usr/bin/env bash
# Builds the macOS app bundle from published output and zips it.
set -euo pipefail

APP_DIR="${1:?application publish directory}"
CLI_DIR="${2:?CLI publish directory}"
OUTPUT_DIR="${3:?output directory}"
VERSION="${4:-0.0.0}"
ARCH="${5:-$(uname -m)}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ICON_SOURCE="${REPO_ROOT}/YellowFox.Desktop/Assets/yellowfox-icon.png"
VERSION="${VERSION#v}"
BUNDLE_ID="com.yellowfox.desktop"
APP="${OUTPUT_DIR}/YellowFox.app"
NAME="YellowFox-macos-${ARCH}-${VERSION}"

rm -rf "${APP}"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources" "${OUTPUT_DIR}"

cp -R "${APP_DIR}/." "${APP}/Contents/MacOS/"
chmod +x "${APP}/Contents/MacOS/YellowFox.Desktop"
cp "${REPO_ROOT}/setup-python.sh" "${APP}/Contents/MacOS/setup-python.sh"
chmod +x "${APP}/Contents/MacOS/setup-python.sh"

ICONSET="$(mktemp -d)/YellowFox.iconset"
mkdir -p "${ICONSET}"
for size in 16 32 128 256 512; do
    sips -z "${size}" "${size}" "${ICON_SOURCE}" --out "${ICONSET}/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "${double}" "${double}" "${ICON_SOURCE}" --out "${ICONSET}/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "${ICONSET}" -o "${APP}/Contents/Resources/YellowFox.icns"
rm -rf "$(dirname "${ICONSET}")"

cat > "${APP}/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>YellowFox</string>
    <key>CFBundleExecutable</key>
    <string>YellowFox.Desktop</string>
    <key>CFBundleIconFile</key>
    <string>YellowFox</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>YellowFox</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
</dict>
</plist>
EOF

# Ad-hoc signing is enough to launch the app from a downloaded zip on Apple Silicon.
# A Developer ID identity can be supplied through MACOS_SIGN_IDENTITY for notarization.
if [ -n "${MACOS_SIGN_IDENTITY:-}" ]; then
    codesign --force --deep --options runtime --sign "${MACOS_SIGN_IDENTITY}" "${APP}"
else
    codesign --force --deep --sign - "${APP}"
fi

STAGE="$(mktemp -d)/${NAME}"
mkdir -p "${STAGE}"
cp -R "${APP}" "${STAGE}/YellowFox.app"
cp -R "${CLI_DIR}" "${STAGE}/YellowFox.Cli"
chmod +x "${STAGE}/YellowFox.Cli/YellowFox.Cli"

ditto -c -k --sequesterRsrc --keepParent "${STAGE}" "${OUTPUT_DIR}/${NAME}.zip"
rm -rf "$(dirname "${STAGE}")"

cat > "${OUTPUT_DIR}/README-MACOS.txt" <<'EOF'
YellowFox for macOS

1. Unpack the archive and move YellowFox.app to ~/Applications (or another
   folder writable by your user).
2. Run YellowFox.app/Contents/MacOS/setup-python.sh once to create the Python
   environment and download Camoufox.
3. Open YellowFox.app. The first launch of an unsigned/ad-hoc build may require
   right-click -> Open.
EOF
