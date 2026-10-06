#!/usr/bin/env bash
set -euo pipefail

WORKFLOW_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS_DIR="$(cd "$WORKFLOW_DIR/.." && pwd)"
TASKS_DIR="$SCRIPTS_DIR/tasks"
# shellcheck source=../lib/context.sh
source "$SCRIPTS_DIR/lib/context.sh"
# shellcheck source=../lib/logging.sh
source "$SCRIPTS_DIR/lib/logging.sh"

run_task "Validate local build inputs" "$TASKS_DIR/validate/local-build-inputs.sh"
run_task "Build bootstrap (Debug)" "$TASKS_DIR/build/bootstrap.sh" Debug
run_task "Build mod (Debug)" "$TASKS_DIR/build/mod.sh" Debug
run_task "Run C# tests" "$TASKS_DIR/test/csharp.sh"
run_task "Verify local bootstrap activation" python3 "$TASKS_DIR/test/bootstrap-install.py"

if [ "${TUFHELPER_LITE_SKIP_INSTALL:-0}" = "1" ]; then
  log_skip "Install mod (TUFHELPER_LITE_SKIP_INSTALL=1)"
else
  run_task "Install mod" "$TASKS_DIR/install/mod.sh"
fi
