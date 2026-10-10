#!/usr/bin/env bash
set -euo pipefail
TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$TASK_DIR/../../lib/context.sh"
source "$TASK_DIR/../../lib/guards.sh"
source "$TASK_DIR/../../lib/ipc-bundle.sh"
verify_ipc_bundle
