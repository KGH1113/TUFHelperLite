#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

require_command shasum
require_file "$ADOFAIIPC_INFO_JSON"
require_file "$ADOFAIIPC_BOOTSTRAP_DLL"
require_file "$ADOFAIIPC_DEPENDENCY_SHIM_DLL"
require_file "$ADOFAIIPC_MIGRATION_DLL"
require_file "$ADOFAIIPC_BOOTSTRAP_LOCK"

# shellcheck disable=SC1090
source "$ADOFAIIPC_BOOTSTRAP_LOCK"

installed_ipc_version="$(sed -n 's/.*"Version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ADOFAIIPC_INFO_JSON" | head -n 1)"
if [ "$installed_ipc_version" != "$ADOFAIIPC_VERSION" ]; then
  fail "AdofaiIpc version mismatch: expected $ADOFAIIPC_VERSION, found ${installed_ipc_version:-unknown}"
fi

bootstrap_sha256="$(shasum -a 256 "$ADOFAIIPC_BOOTSTRAP_DLL" | awk '{print $1}')"
if [ "$bootstrap_sha256" != "$ADOFAIIPC_BOOTSTRAP_SHA256" ]; then
  fail "AdofaiIpc Bootstrap checksum mismatch: expected $ADOFAIIPC_BOOTSTRAP_SHA256, found $bootstrap_sha256"
fi

shim_sha256="$(shasum -a 256 "$ADOFAIIPC_DEPENDENCY_SHIM_DLL" | awk '{print $1}')"
if [ "$shim_sha256" != "$ADOFAIIPC_DEPENDENCY_SHIM_SHA256" ]; then
  fail "AdofaiIpc dependency shim checksum mismatch: expected $ADOFAIIPC_DEPENDENCY_SHIM_SHA256, found $shim_sha256"
fi

migration_sha256="$(shasum -a 256 "$ADOFAIIPC_MIGRATION_DLL" | awk '{print $1}')"
if [ "$migration_sha256" != "$ADOFAIIPC_MIGRATION_SHA256" ]; then
  fail "AdofaiIpc migration checksum mismatch: expected $ADOFAIIPC_MIGRATION_SHA256, found $migration_sha256"
fi
