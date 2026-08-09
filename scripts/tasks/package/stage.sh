#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"
# shellcheck source=../../lib/artifacts.sh
source "$TASK_DIR/../../lib/artifacts.sh"

assert_non_root_path "$TUFHELPER_LITE_PACKAGE_ROOT"
assert_non_root_path "$TUFHELPER_LITE_PACKAGE_STAGE"
mkdir -p "$TUFHELPER_LITE_PACKAGE_ROOT"
if [ -e "$TUFHELPER_LITE_PACKAGE_STAGE" ]; then
  safe_remove_tree "$TUFHELPER_LITE_PACKAGE_STAGE" "$TUFHELPER_LITE_PACKAGE_ROOT"
fi
mkdir -p "$TUFHELPER_LITE_PACKAGE_STAGE"
copy_mod_artifacts "$TUFHELPER_LITE_PACKAGE_STAGE"
