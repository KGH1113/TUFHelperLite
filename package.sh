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
ADOFAIIPC_INFO_JSON="${ADOFAIIPC_INFO_JSON:-$ADOFAI_MODS_DIR/AdofaiIpc/Info.json}"
ADOFAIIPC_BOOTSTRAP_LOCK="$PROJECT/TUFHelperLite/AdofaiIpcBootstrap.lock"

project_path() {
  case "$1" in
    /*) printf '%s\n' "$1" ;;
    *) printf '%s/%s\n' "$PROJECT" "$1" ;;
  esac
}

OUT="$(project_path "${TUFHELPER_LITE_BUILD_DIR:-build/TUFHelperLite}")"
PACKAGE_ROOT="$(project_path "${TUFHELPER_LITE_PACKAGE_ROOT:-build/package}")"
STAGE="$PACKAGE_ROOT/TUFHelperLite"
ZIP_PATH="$(project_path "${TUFHELPER_LITE_PACKAGE_ZIP:-build/TUFHelperLite.zip}")"

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

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_command zip
require_command shasum
require_file "$DOTNET_EXE"
require_dir "$ADOFAI_MANAGED"
require_file "$UNITY_MOD_MANAGER_DLL"
require_file "$HARMONY_DLL"
require_file "$ADOFAIIPC_DLL"
require_file "$ADOFAIIPC_BOOTSTRAP_DLL"
require_file "$ADOFAIIPC_INFO_JSON"
require_file "$ADOFAIIPC_BOOTSTRAP_LOCK"

# shellcheck disable=SC1090
source "$ADOFAIIPC_BOOTSTRAP_LOCK"

installed_ipc_version="$(sed -n 's/.*"Version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ADOFAIIPC_INFO_JSON" | head -n 1)"
if [ "$installed_ipc_version" != "$ADOFAIIPC_VERSION" ]; then
  echo "AdofaiIpc version mismatch: expected $ADOFAIIPC_VERSION, found ${installed_ipc_version:-unknown}" >&2
  exit 1
fi

bootstrap_sha256="$(shasum -a 256 "$ADOFAIIPC_BOOTSTRAP_DLL" | awk '{print $1}')"
if [ "$bootstrap_sha256" != "$ADOFAIIPC_BOOTSTRAP_SHA256" ]; then
  echo "AdofaiIpc Bootstrap checksum mismatch." >&2
  echo "Expected: $ADOFAIIPC_BOOTSTRAP_SHA256" >&2
  echo "Actual:   $bootstrap_sha256" >&2
  exit 1
fi

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

rm -rf "$STAGE"
mkdir -p "$STAGE"

cp "$PROJECT/TUFHelperLite/Info.json" "$STAGE/"
cp "$PROJECT/TUFHelperLite/AdofaiIpcBootstrap.json" "$STAGE/"
cp "$PROJECT/THIRD_PARTY_NOTICES.md" "$STAGE/"
cp "$OUT/TUFHelperLite.Bootstrap.dll" "$STAGE/TUFHelperLite.dll"
cp "$OUT/TUFHelperLite.Core.dll" "$STAGE/"
cp "$ADOFAIIPC_BOOTSTRAP_DLL" "$STAGE/"

if [ -d "$PROJECT/TUFHelperLite/Assets" ]; then
  cp -R "$PROJECT/TUFHelperLite/Assets" "$STAGE/"
fi

if [ -f "$OUT/TUFHelperLite.Core.pdb" ]; then
  cp "$OUT/TUFHelperLite.Core.pdb" "$STAGE/"
fi

rm -f "$ZIP_PATH"
mkdir -p "$(dirname "$ZIP_PATH")"
(
  cd "$PACKAGE_ROOT"
  zip -r "$ZIP_PATH" TUFHelperLite \
    -x 'TUFHelperLite/Data/*' \
    -x 'TUFHelperLite/*.log'
)

archive_sha256="$(shasum -a 256 "$ZIP_PATH" | awk '{print $1}')"
printf '%s  %s\n' "$archive_sha256" "$(basename "$ZIP_PATH")" > "$ZIP_PATH.sha256"

echo "Packaged to $ZIP_PATH"
echo "Checksum written to $ZIP_PATH.sha256"
