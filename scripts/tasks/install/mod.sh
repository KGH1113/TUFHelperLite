#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"
# shellcheck source=../../lib/artifacts.sh
source "$TASK_DIR/../../lib/artifacts.sh"

require_command python3
"$TASK_DIR/../verify/adofai-ipc.sh"
assert_non_root_path "$TUFHELPER_LITE_INSTALL_PATH"
mkdir -p "$TUFHELPER_LITE_INSTALL_PATH"

for obsolete_dir in assembly_cache; do
  if [ -e "$TUFHELPER_LITE_INSTALL_PATH/$obsolete_dir" ]; then
    safe_remove_tree "$TUFHELPER_LITE_INSTALL_PATH/$obsolete_dir" "$TUFHELPER_LITE_INSTALL_PATH"
  fi
done

rm -f "$TUFHELPER_LITE_INSTALL_PATH/JAModInfo.json" \
  "$TUFHELPER_LITE_INSTALL_PATH/JAMod.Bootstrap.dll" \
  "$TUFHELPER_LITE_INSTALL_PATH/AdofaiIpc.Bootstrap.dll"
rm -f "$TUFHELPER_LITE_INSTALL_PATH"/JAMod.Bootstrap.dll.*.cache

if [ -e "$TUFHELPER_LITE_INSTALL_PATH/Assets" ]; then
  safe_remove_tree "$TUFHELPER_LITE_INSTALL_PATH/Assets" "$TUFHELPER_LITE_INSTALL_PATH"
fi

# The launcher executes the payload from Runtime/versions, not from the flat
# install root. A local reinstall must invalidate that generated runtime so the
# launcher seeds it again from the freshly copied development artifacts.
if [ -e "$TUFHELPER_LITE_INSTALL_PATH/Runtime" ]; then
  safe_remove_tree "$TUFHELPER_LITE_INSTALL_PATH/Runtime" "$TUFHELPER_LITE_INSTALL_PATH"
fi

copy_mod_artifacts "$TUFHELPER_LITE_INSTALL_PATH"
# The shim follows DependencyBootstrap/state.json, even when Assets contains a
# newer bundled DLL. Select the verified local candidate and retain old versions.
# shellcheck disable=SC1090
source "$ADOFAIIPC_BOOTSTRAP_LOCK"
python3 "$TUFHELPER_LITE_PROJECT_ROOT/scripts/lib/bootstrap_install.py" \
  "$TUFHELPER_LITE_INSTALL_PATH" "$ADOFAIIPC_BOOTSTRAP_DLL" \
  "$ADOFAIIPC_BOOTSTRAP_VERSION" "$ADOFAIIPC_BOOTSTRAP_SHA256"
printf 'Installed to %s\n' "$TUFHELPER_LITE_INSTALL_PATH"
