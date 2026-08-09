#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

require_command zip
require_dir "$TUFHELPER_LITE_PACKAGE_STAGE"
rm -f "$TUFHELPER_LITE_PACKAGE_ZIP"
mkdir -p "$(dirname "$TUFHELPER_LITE_PACKAGE_ZIP")"
(
  cd "$TUFHELPER_LITE_PACKAGE_ROOT"
  zip -r "$TUFHELPER_LITE_PACKAGE_ZIP" TUFHelperLite \
    -x 'TUFHelperLite/Data/*' \
    -x 'TUFHelperLite/*.log'
)
printf 'Packaged to %s\n' "$TUFHELPER_LITE_PACKAGE_ZIP"
