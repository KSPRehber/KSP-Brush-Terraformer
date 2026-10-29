#!/usr/bin/env bash
# Launches the test install (Windows build) through Proton with its own Wine prefix,
# so the Steam copy of KSP and its prefix are never touched.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
STEAM="${STEAM_ROOT:-$HOME/.local/share/Steam}"
PROTON="${PROTON:-$STEAM/steamapps/common/Proton 11.0/proton}"

export STEAM_COMPAT_CLIENT_INSTALL_PATH="$STEAM"
export STEAM_COMPAT_DATA_PATH="$ROOT/.proton-prefix"
export SteamAppId=220200
mkdir -p "$STEAM_COMPAT_DATA_PATH"

cd "$ROOT/KSP"
exec "$PROTON" run ./KSP_x64.exe "$@"
