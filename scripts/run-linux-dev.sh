#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/VoiceInput/VoiceInput.csproj"
APP_EXEC="$ROOT/scripts/run-linux-dev.sh"
APP_ICON_SOURCE="$ROOT/VoiceInput/Assets/voiceinput.png"

DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
DESKTOP_FILE="$DESKTOP_DIR/com.chihaicheng.voiceinput.desktop"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
ICONS_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"

mkdir -p "$DESKTOP_DIR"
mkdir -p "$ICON_DIR"
cp "$APP_ICON_SOURCE" "$ICON_DIR/com.chihaicheng.voiceinput.png"

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$ICONS_ROOT" >/dev/null 2>&1 || true
fi

sed \
  -e "s|@APP_EXEC@|$APP_EXEC|g" \
  "$ROOT/packaging/linux/com.chihaicheng.voiceinput.desktop.in" \
  > "$DESKTOP_FILE"

chmod +x "$APP_EXEC"

exec systemd-run --user --scope --unit="app-com.chihaicheng.voiceinput-$$" \
  dotnet run --project "$PROJECT" -f net10.0 -p:EnableWindowsTargeting=true
