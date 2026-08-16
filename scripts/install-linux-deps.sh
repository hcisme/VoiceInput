#!/usr/bin/env bash
set -euo pipefail

sudo apt-get update

ALSA_LIB="libasound2t64"
if ! apt-cache show "$ALSA_LIB" >/dev/null 2>&1; then
  ALSA_LIB="libasound2"
fi

sudo apt-get install -y \
  "$ALSA_LIB" \
  libfontconfig1 \
  libice6 \
  libsm6 \
  libx11-6 \
  libxext6 \
  libxkbcommon0 \
  libxkbcommon-x11-0 \
  libwayland-client0

echo "Linux runtime dependencies installed."
