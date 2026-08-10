#!/usr/bin/env bash
set -euo pipefail

WORKFLOW_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPTS_DIR="$(cd "$WORKFLOW_DIR/.." && pwd)"
TASKS_DIR="$SCRIPTS_DIR/tasks"
# shellcheck source=../lib/context.sh
source "$SCRIPTS_DIR/lib/context.sh"
# shellcheck source=../lib/logging.sh
source "$SCRIPTS_DIR/lib/logging.sh"

run_task "Validate package inputs" "$TASKS_DIR/validate/package-inputs.sh"
run_task "Verify AdofaiIpc dependency" "$TASKS_DIR/verify/adofai-ipc.sh"
run_task "Build bootstrap (Release)" "$TASKS_DIR/build/bootstrap.sh" Release
run_task "Build mod (Release)" "$TASKS_DIR/build/mod.sh" Release
run_task "Run C# tests" "$TASKS_DIR/test/csharp.sh"
run_task "Stage mod package" "$TASKS_DIR/package/stage.sh"
run_task "Create mod archive" "$TASKS_DIR/package/archive.sh"
run_task "Write checksum" "$TASKS_DIR/package/checksum.sh"
run_task "Verify final package compatibility" env \
  TUFHELPER_LITE_PACKAGE_UNDER_TEST="$TUFHELPER_LITE_PACKAGE_ZIP" \
  "$TASKS_DIR/test/csharp.sh"
