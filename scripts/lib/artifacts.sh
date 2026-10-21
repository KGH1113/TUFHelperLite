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
source "${BASH_SOURCE[0]%/*}/ipc-bundle.sh"

copy_mod_artifacts() {
  local destination="$1"

  mkdir -p "$destination"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json" "$destination/"
  cp "$TUFHELPER_LITE_PROJECT_ROOT/THIRD_PARTY_NOTICES.md" "$destination/"
  cp "$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.dll" "$destination/"
  if [ -d "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Assets" ]; then
    cp -R "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Assets" "$destination/"
  fi
  # Keep all new paths beneath Assets: the deployed v0.1.9 updater rejects
  # unknown root files. This same ZIP is also the normal manual installation.
  local payload="$destination/Assets/IpcMigration"
  local legacy="$TUFHELPER_LITE_PROJECT_ROOT/build/migration-evidence/published-0.1.9/extracted/TUFHelperLite"
  mkdir -p "$payload" "$destination/Assets/AdofaiIpc"
  cp "$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/TUFHelperLite.Launcher.dll" "$payload/"
  cp "$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll" "$payload/"
  copy_ipc_bundle "$payload"
  for name in AdofaiIpc.Bootstrap.dll AdofaiIpc.DependencyShim.dll AdofaiIpc.Migration.dll AdofaiIpcBootstrap.json TUFHelperLite.Launcher.dll; do
    cp "$legacy/Assets/AdofaiIpc/$name" "$destination/Assets/AdofaiIpc/"
  done
  cp "$legacy/AdofaiIpcBootstrap.json" "$destination/"
  cp "$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll" "$destination/Assets/AdofaiIpc/"
}
