#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT/VoiceInput/VoiceInput.csproj"
PUBLISH_DIR="$ROOT/VoiceInput/bin/Release/net10.0/linux-x64/publish"

VERSION="${1:-0.0.0}"
PACKAGE_NAME="voiceinput"
PACKAGE_DIR="$ROOT/dist/deb/${PACKAGE_NAME}_${VERSION}_amd64"
DEB_FILE="$ROOT/dist/VoiceInput_${VERSION}_amd64.deb"

rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR/DEBIAN"
mkdir -p "$PACKAGE_DIR/opt/voiceinput"
mkdir -p "$PACKAGE_DIR/usr/bin"
mkdir -p "$PACKAGE_DIR/usr/share/applications"
mkdir -p "$PACKAGE_DIR/usr/share/icons/hicolor/256x256/apps"

cp -R "$PUBLISH_DIR/." "$PACKAGE_DIR/opt/voiceinput/"
chmod +x "$PACKAGE_DIR/opt/voiceinput/VoiceInput"

ln -s ../opt/voiceinput/VoiceInput "$PACKAGE_DIR/usr/bin/voiceinput"

sed "s|@APP_EXEC@|/usr/bin/voiceinput|g" \
  "$ROOT/packaging/linux/com.chihaicheng.voiceinput.desktop.in" \
  > "$PACKAGE_DIR/usr/share/applications/com.chihaicheng.voiceinput.desktop"

cp "$ROOT/VoiceInput/Assets/voiceinput.png" \
  "$PACKAGE_DIR/usr/share/icons/hicolor/256x256/apps/com.chihaicheng.voiceinput.png"

cat > "$PACKAGE_DIR/DEBIAN/control" <<EOF
Package: ${PACKAGE_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: chihaicheng
Depends: libc6, libasound2t64 | libasound2, libfontconfig1, libice6, libsm6, libx11-6, libxext6, libxkbcommon0, libxkbcommon-x11-0, libwayland-client0
Recommends: xdg-desktop-portal, xdg-desktop-portal-gnome
Description: Cross-platform voice input tool
 VoiceInput is a push-to-talk speech recognition tool for Windows and Linux.
EOF

dpkg-deb --root-owner-group --build "$PACKAGE_DIR" "$DEB_FILE"

echo "created $DEB_FILE"
