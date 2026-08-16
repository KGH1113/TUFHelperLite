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
require_file "$HARMONY_DLL"
require_file "$ADOFAIIPC_DLL"
require_file "$ADOFAIIPC_BOOTSTRAP_LOCK"
require_command shasum

# shellcheck disable=SC1090
source "$ADOFAIIPC_BOOTSTRAP_LOCK"

cache_locked_artifact() {
  local destination="$1"
  local expected_sha256="$2"
  shift 2

  if [ -f "$destination" ] && \
    [ "$(shasum -a 256 "$destination" | awk '{print $1}')" = "$expected_sha256" ]; then
    return 0
  fi

  local candidate
  for candidate in "$@"; do
    if [ -f "$candidate" ] && \
      [ "$(shasum -a 256 "$candidate" | awk '{print $1}')" = "$expected_sha256" ]; then
      mkdir -p "$(dirname "$destination")"
      cp "$candidate" "$destination"
      return 0
    fi
  done

  fail "Could not find an AdofaiIpc artifact matching checksum $expected_sha256 for $destination"
}

if [ "$ADOFAIIPC_BOOTSTRAP_DLL" = "$ADOFAIIPC_BOOTSTRAP_CACHE_DLL" ]; then
  cache_locked_artifact "$ADOFAIIPC_BOOTSTRAP_CACHE_DLL" "$ADOFAIIPC_BOOTSTRAP_SHA256" \
    "$ADOFAIIPC_CANONICAL_DIR/AdofaiIpc.Bootstrap.dll" \
    "$ADOFAIIPC_INSTALLED_ASSET_DIR/AdofaiIpc.Bootstrap.dll"
fi

if [ "$ADOFAIIPC_DEPENDENCY_SHIM_DLL" = "$ADOFAIIPC_DEPENDENCY_SHIM_CACHE_DLL" ]; then
  cache_locked_artifact "$ADOFAIIPC_DEPENDENCY_SHIM_CACHE_DLL" "$ADOFAIIPC_DEPENDENCY_SHIM_SHA256" \
    "$ADOFAIIPC_CANONICAL_DIR/AdofaiIpc.DependencyShim.dll" \
    "$ADOFAIIPC_INSTALLED_ASSET_DIR/AdofaiIpc.DependencyShim.dll"
fi

if [ "$ADOFAIIPC_MIGRATION_DLL" = "$ADOFAIIPC_MIGRATION_CACHE_DLL" ]; then
  cache_locked_artifact "$ADOFAIIPC_MIGRATION_CACHE_DLL" "$ADOFAIIPC_MIGRATION_SHA256" \
    "$ADOFAIIPC_CANONICAL_DIR/AdofaiIpc.Migration.dll" \
    "$ADOFAIIPC_INSTALLED_ASSET_DIR/AdofaiIpc.Migration.dll"
fi

require_file "$ADOFAIIPC_BOOTSTRAP_DLL"
require_file "$ADOFAIIPC_DEPENDENCY_SHIM_DLL"
require_file "$ADOFAIIPC_MIGRATION_DLL"
