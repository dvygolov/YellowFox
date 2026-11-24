#!/usr/bin/env bash
# Builds the Linux release artifacts (tar.gz and .deb) from published output.
set -euo pipefail

APP_DIR="${1:?application publish directory}"
CLI_DIR="${2:?CLI publish directory}"
OUTPUT_DIR="${3:?output directory}"
VERSION="${4:-0.0.0}"
ARCH="${5:-x64}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ICON_SOURCE="${REPO_ROOT}/YellowFox.Desktop/Assets/yellowfox-icon.png"
VERSION="${VERSION#v}"
NAME="YellowFox-linux-${ARCH}-${VERSION}"
STAGE="${OUTPUT_DIR}/${NAME}"

rm -rf "${STAGE}"
mkdir -p "${STAGE}/YellowFox.Cli" "${OUTPUT_DIR}"

cp -R "${APP_DIR}/." "${STAGE}/"
cp -R "${CLI_DIR}/." "${STAGE}/YellowFox.Cli/"
chmod +x "${STAGE}/YellowFox.Desktop" "${STAGE}/YellowFox.Cli/YellowFox.Cli"
install -m 644 "${ICON_SOURCE}" "${STAGE}/yellowfox.png"

cat > "${STAGE}/YellowFox.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=YellowFox
Comment=Camoufox profile manager
Exec=sh -c 'cd "$(dirname "%k")" && exec ./YellowFox.Desktop'
Icon=yellowfox
Terminal=false
Categories=Network;WebBrowser;
EOF

cat > "${STAGE}/README-LINUX.txt" <<'EOF'
YellowFox for Linux

1. Unpack the archive anywhere and run ./YellowFox.Desktop.
2. Python 3 with the Camoufox packages is required. Run ./setup-python.sh once
   (it creates python/venv inside this folder) before the first launch.
3. To add YellowFox to the application menu, copy YellowFox.desktop to
   ~/.local/share/applications and yellowfox.png to
   ~/.local/share/icons/hicolor/512x512/apps.
EOF

cp "${REPO_ROOT}/setup-python.sh" "${STAGE}/setup-python.sh"
chmod +x "${STAGE}/setup-python.sh"

tar -czf "${OUTPUT_DIR}/${NAME}.tar.gz" -C "${OUTPUT_DIR}" "${NAME}"

if command -v dpkg-deb >/dev/null 2>&1; then
    DEB_ROOT="$(mktemp -d)"
    APP_ROOT="${DEB_ROOT}/opt/yellowfox"
    mkdir -p "${APP_ROOT}/cli" \
             "${DEB_ROOT}/usr/bin" \
             "${DEB_ROOT}/usr/share/applications" \
             "${DEB_ROOT}/usr/share/icons/hicolor/512x512/apps" \
             "${DEB_ROOT}/DEBIAN"
    cp -R "${APP_DIR}/." "${APP_ROOT}/"
    cp -R "${CLI_DIR}/." "${APP_ROOT}/cli/"
    chmod +x "${APP_ROOT}/YellowFox.Desktop" "${APP_ROOT}/cli/YellowFox.Cli"
    install -m 644 "${ICON_SOURCE}" "${DEB_ROOT}/usr/share/icons/hicolor/512x512/apps/yellowfox.png"
    cp "${REPO_ROOT}/setup-python.sh" "${APP_ROOT}/setup-python.sh"

    cat > "${DEB_ROOT}/usr/share/applications/yellowfox.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=YellowFox
Comment=Camoufox profile manager
Exec=/opt/yellowfox/YellowFox.Desktop
Icon=yellowfox
Terminal=false
Categories=Network;WebBrowser;
EOF

    cat > "${DEB_ROOT}/usr/bin/yellowfox" <<'EOF'
#!/bin/sh
exec /opt/yellowfox/YellowFox.Desktop "$@"
EOF

    cat > "${DEB_ROOT}/DEBIAN/control" <<EOF
Package: yellowfox
Version: ${VERSION}
Section: web
Priority: optional
Architecture: $(dpkg --print-architecture)
Maintainer: YellowFox
Description: Camoufox profile manager with anti-detect browser profiles.
EOF

    chmod +x "${DEB_ROOT}/usr/bin/yellowfox"
    dpkg-deb --root-owner-group --build "${DEB_ROOT}" "${OUTPUT_DIR}/YellowFox-linux-${ARCH}.deb" >/dev/null
    rm -rf "${DEB_ROOT}"
fi

rm -rf "${STAGE}"
