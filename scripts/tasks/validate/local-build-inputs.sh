#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

require_executable "$DOTNET_EXE"
require_dir "$ADOFAI_MANAGED"
require_file "$UNITY_MOD_MANAGER_DLL"
require_file "$ADOFAI_IPC_BUNDLE/AdofaiIpc.Contracts.dll"
require_file "$ADOFAI_IPC_BUNDLE/AdofaiIpc.Loader.dll"
