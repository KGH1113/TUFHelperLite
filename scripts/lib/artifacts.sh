#!/usr/bin/env bash

if [ "${TUFHELPER_LITE_ARTIFACTS_LOADED:-0}" = "1" ]; then
  return 0
fi
TUFHELPER_LITE_ARTIFACTS_LOADED=1

TUFHELPER_LITE_ARTIFACTS_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=context.sh
source "$TUFHELPER_LITE_ARTIFACTS_LIB_DIR/context.sh"
# shellcheck source=guards.sh
source "$TUFHELPER_LITE_ARTIFACTS_LIB_DIR/guards.sh"

copy_mod_artifacts() {
  local destination="$1"

  require_file "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json"
  require_file "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/AdofaiIpcBootstrap.json"
  require_file "$TUFHELPER_LITE_PROJECT_ROOT/THIRD_PARTY_NOTICES.md"
  require_file "$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.dll"
  require_file "$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/TUFHelperLite.Launcher.dll"
  require_file "$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll"
  require_file "$ADOFAIIPC_BOOTSTRAP_DLL"
  require_file "$ADOFAIIPC_DEPENDENCY_SHIM_DLL"
  require_file "$ADOFAIIPC_MIGRATION_DLL"
  require_file "$ADOFAIIPC_BOOTSTRAP_LOCK"

  # shellcheck disable=SC1090
  source "$ADOFAIIPC_BOOTSTRAP_LOCK"
  [ -n "${ADOFAIIPC_BOOTSTRAP_VERSION:-}" ] || fail "AdofaiIpc bootstrap version is missing from the lock file."

  mkdir -p "$destination"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json" "$destination/"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/AdofaiIpcBootstrap.json" "$destination/"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/THIRD_PARTY_NOTICES.md" "$destination/"
  cp "$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.dll" "$destination/"

  if [ -d "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Assets" ]; then
    cp -R "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Assets" "$destination/"
  fi

  mkdir -p "$destination/Assets/AdofaiIpc"
  cp "$ADOFAIIPC_DEPENDENCY_SHIM_DLL" "$destination/Assets/AdofaiIpc/"
  cp "$ADOFAIIPC_BOOTSTRAP_DLL" "$destination/Assets/AdofaiIpc/"
  cp "$ADOFAIIPC_MIGRATION_DLL" "$destination/Assets/AdofaiIpc/"
  cp "$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/TUFHelperLite.Launcher.dll" \
    "$destination/Assets/AdofaiIpc/"
  cp "$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll" \
    "$destination/Assets/AdofaiIpc/"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/AdofaiIpcBootstrap.json" \
    "$destination/Assets/AdofaiIpc/"

  if [ -f "$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.pdb" ]; then
    cp "$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.pdb" "$destination/"
  fi
}
