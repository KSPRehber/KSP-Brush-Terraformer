#!/usr/bin/env bash
# Builds TerraformTech.dll against the test install's own assemblies and copies it into GameData.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KSP="${KSP_DIR:-$ROOT/KSP}"
MANAGED="$KSP/KSP_x64_Data/Managed"
OUT="$ROOT/build"
DEPLOY="$KSP/GameData/TerraformTech/Plugins"

mkdir -p "$OUT" "$DEPLOY"

refs=(mscorlib System System.Core Assembly-CSharp Assembly-CSharp-firstpass
      UnityEngine UnityEngine.CoreModule UnityEngine.PhysicsModule UnityEngine.IMGUIModule
      UnityEngine.InputLegacyModule UnityEngine.UI)

args=()
for r in "${refs[@]}"; do args+=("-r:$MANAGED/$r.dll"); done

csc -nologo -nostdlib -noconfig -target:library -optimize+ -debug:portable -langversion:7.3 \
    -out:"$OUT/TerraformTech.dll" "${args[@]}" "$ROOT"/src/*.cs

# Copy then rename: Mono memory-maps loaded assemblies, so overwriting the file in place
# corrupts a running game ("Method has zero rva"). A rename leaves the old inode intact.
cp "$ROOT/config/TerraformTech.cfg" "$DEPLOY/../TerraformTech.cfg"
cp "$OUT/TerraformTech.dll" "$DEPLOY/.TerraformTech.dll.tmp"
mv -f "$DEPLOY/.TerraformTech.dll.tmp" "$DEPLOY/TerraformTech.dll"
echo "Deployed to $DEPLOY/TerraformTech.dll"
if pgrep -f KSP_x64.exe > /dev/null; then
    echo "Note: KSP is running; restart it to load the new build."
fi
