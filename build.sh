#!/usr/bin/env bash
set -euo pipefail

PROJECT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ -f "$PROJECT/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  source "$PROJECT/.env"
  set +a
fi

ADOFAI_DIR="${ADOFAI_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/A Dance of Fire and Ice}"
ADOFAI_MODS_DIR="${ADOFAI_MODS_DIR:-$ADOFAI_DIR/Mods}"
ADOFAI_MANAGED="${ADOFAI_MANAGED:-$ADOFAI_DIR/ADanceOfFireAndIce.app/Contents/Resources/Data/Managed}"

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
DOTNET_ROOT_ARM64="${DOTNET_ROOT_ARM64:-$DOTNET_ROOT}"
DOTNET_EXE="${DOTNET_EXE:-$DOTNET_ROOT/dotnet}"

UNITY_MOD_MANAGER_DLL="${UNITY_MOD_MANAGER_DLL:-$ADOFAI_MANAGED/UnityModManager/UnityModManager.dll}"
HARMONY_DLL="${HARMONY_DLL:-$ADOFAI_MANAGED/UnityModManager/0Harmony.dll}"
ADOFAIIPC_DLL="${ADOFAIIPC_DLL:-$ADOFAI_MODS_DIR/AdofaiIpc/AdofaiIpc.dll}"
ADOFAIIPC_BOOTSTRAP_DLL="${ADOFAIIPC_BOOTSTRAP_DLL:-$ADOFAI_MODS_DIR/AdofaiIpc/AdofaiIpc.Bootstrap.dll}"

project_path() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s/%s\n' "$PROJECT" "$1" ;;
  esac
}

OUT="$(project_path "${TUFHELPER_LITE_BUILD_DIR:-build/TUFHelperLite}")"
DEST="$(project_path "${TUFHELPER_LITE_INSTALL_DIR:-$ADOFAI_MODS_DIR/TUFHelperLite}")"

require_file() {
  if [ ! -f "$1" ]; then
    echo "Missing required file: $1" >&2
    exit 1
  fi
}

require_dir() {
  if [ ! -d "$1" ]; then
    echo "Missing required directory: $1" >&2
    exit 1
  fi
}

require_file "$DOTNET_EXE"
require_dir "$ADOFAI_MANAGED"
require_file "$UNITY_MOD_MANAGER_DLL"
require_file "$HARMONY_DLL"
require_file "$ADOFAIIPC_DLL"
require_file "$ADOFAIIPC_BOOTSTRAP_DLL"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
"$DOTNET_EXE" build "$PROJECT/TUFHelperLite/TUFHelperLite.csproj" \
  -p:OutputPath="$OUT/" \
  -p:AdofaiManaged="$ADOFAI_MANAGED" \
  -p:AdofaiMods="$ADOFAI_MODS_DIR" \
  -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL" \
  -p:HarmonyDll="$HARMONY_DLL"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
"$DOTNET_EXE" build "$PROJECT/TUFHelperLite.Bootstrap/TUFHelperLite.Bootstrap.csproj" \
  -p:OutputPath="$OUT/" \
  -p:AdofaiManaged="$ADOFAI_MANAGED" \
  -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL"

mkdir -p "$DEST"
rm -rf "$DEST/assembly_cache"
cp "$PROJECT/TUFHelperLite/Info.json" "$DEST/"
cp "$PROJECT/TUFHelperLite/AdofaiIpcBootstrap.json" "$DEST/"
cp "$PROJECT/THIRD_PARTY_NOTICES.md" "$DEST/"
rm -f "$DEST/JAModInfo.json" "$DEST/JAMod.Bootstrap.dll"
rm -f "$DEST"/JAMod.Bootstrap.dll.*.cache
cp "$OUT/TUFHelperLite.Bootstrap.dll" "$DEST/TUFHelperLite.dll"
cp "$OUT/TUFHelperLite.Core.dll" "$DEST/"
cp "$ADOFAIIPC_BOOTSTRAP_DLL" "$DEST/"

if [ -d "$PROJECT/TUFHelperLite/Assets" ]; then
  rm -rf "$DEST/Assets"
  cp -R "$PROJECT/TUFHelperLite/Assets" "$DEST/"
fi

if [ -f "$OUT/TUFHelperLite.Core.pdb" ]; then
  cp "$OUT/TUFHelperLite.Core.pdb" "$DEST/"
fi

echo "Installed to $DEST"
