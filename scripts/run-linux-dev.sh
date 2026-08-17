#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/VoiceInput/VoiceInput.csproj"
APP_EXEC="$ROOT/scripts/run-linux-dev.sh"
APP_ICON_SOURCE="$ROOT/VoiceInput/Assets/voiceinput.png"

DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
DESKTOP_FILE="$DESKTOP_DIR/com.chihaicheng.voiceinput.desktop"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
ICON_FILE="$ICON_DIR/com.chihaicheng.voiceinput.png"
ICONS_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"

DESKTOP_BACKUP=""
ICON_BACKUP=""
ICON_EXISTED_BEFORE=false

cleanup() {
  if [ -n "$DESKTOP_BACKUP" ] && [ -f "$DESKTOP_BACKUP" ]; then
    mv -f "$DESKTOP_BACKUP" "$DESKTOP_FILE"
  else
    rm -f "$DESKTOP_FILE"
  fi

  if [ -n "$ICON_BACKUP" ] && [ -f "$ICON_BACKUP" ]; then
    mv -f "$ICON_BACKUP" "$ICON_FILE"
  elif [ "$ICON_EXISTED_BEFORE" = false ]; then
    rm -f "$ICON_FILE"
  fi

  if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
  fi

  if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t "$ICONS_ROOT" >/dev/null 2>&1 || true
  fi
}

trap cleanup EXIT

if [ -e "$DESKTOP_FILE" ]; then
  if grep -q '^NoDisplay=true$' "$DESKTOP_FILE" 2>/dev/null; then
    # 这是之前开发脚本留下的临时入口，直接清理，避免继续遮住安装版。
    rm -f "$DESKTOP_FILE"
  else
    DESKTOP_BACKUP="$(mktemp "$DESKTOP_DIR/.voiceinput-desktop.XXXXXX")"
    cp -p "$DESKTOP_FILE" "$DESKTOP_BACKUP"
  fi
fi

if [ -e "$ICON_FILE" ]; then
  ICON_EXISTED_BEFORE=true
  ICON_BACKUP="$(mktemp "$ICON_DIR/.voiceinput-icon.XXXXXX")"
  cp -p "$ICON_FILE" "$ICON_BACKUP"
fi

mkdir -p "$DESKTOP_DIR"
mkdir -p "$ICON_DIR"
cp "$APP_ICON_SOURCE" "$ICON_FILE"

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$ICONS_ROOT" >/dev/null 2>&1 || true
fi

sed \
  -e "s|@APP_EXEC@|$APP_EXEC|g" \
  "$ROOT/packaging/linux/com.chihaicheng.voiceinput.desktop.in" \
  > "$DESKTOP_FILE"

# 开发用 .desktop 只用于让 Portal 识别 app id，不应覆盖安装版快捷方式。
printf 'NoDisplay=true\n' >> "$DESKTOP_FILE"

chmod +x "$APP_EXEC"

systemd-run --user --scope --unit="app-com.chihaicheng.voiceinput-$$" \
  dotnet run --project "$PROJECT" -f net10.0 -p:EnableWindowsTargeting=true

#rm ~/.local/share/applications/com.chihaicheng.voiceinput.desktop
#update-desktop-database ~/.local/share/applications
#gtk-update-icon-cache -f -t /usr/share/icons/hicolor