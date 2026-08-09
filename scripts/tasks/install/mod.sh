#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"
# shellcheck source=../../lib/artifacts.sh
source "$TASK_DIR/../../lib/artifacts.sh"

assert_non_root_path "$TUFHELPER_LITE_INSTALL_PATH"
mkdir -p "$TUFHELPER_LITE_INSTALL_PATH"

for obsolete_dir in assembly_cache DependencyBootstrap; do
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

copy_mod_artifacts "$TUFHELPER_LITE_INSTALL_PATH"
printf 'Installed to %s\n' "$TUFHELPER_LITE_INSTALL_PATH"
