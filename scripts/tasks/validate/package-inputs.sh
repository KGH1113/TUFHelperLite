#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

"$TASK_DIR/local-build-inputs.sh"
require_command zip
require_command shasum
require_file "$ADOFAIIPC_INFO_JSON"
require_file "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json"
require_file "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/AdofaiIpcBootstrap.json"
require_file "$TUFHELPER_LITE_PROJECT_ROOT/THIRD_PARTY_NOTICES.md"
