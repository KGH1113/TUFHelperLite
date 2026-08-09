#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

require_command shasum
require_file "$TUFHELPER_LITE_PACKAGE_ZIP"
checksum="$TUFHELPER_LITE_PACKAGE_ZIP.sha256"
(
  cd "$(dirname "$TUFHELPER_LITE_PACKAGE_ZIP")"
  shasum -a 256 "$(basename "$TUFHELPER_LITE_PACKAGE_ZIP")" > "$(basename "$checksum")"
)
printf 'Checksum written to %s\n' "$checksum"
