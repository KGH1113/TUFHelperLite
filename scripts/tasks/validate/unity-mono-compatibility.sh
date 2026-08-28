#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

[ "$#" -ge 3 ] || fail "Usage: unity-mono-compatibility.sh CORE_DLL LAUNCHER_DLL UPDATE_ENGINE_DLL"

core_dll="$1"
shift
assemblies=("$core_dll" "$@")

require_command grep
require_command monodis

for assembly in "${assemblies[@]}"; do
  require_file "$assembly"
  if LC_ALL=C grep -aFq 'DefaultInterpolatedStringHandler' "$assembly"; then
    fail "Unity/Mono-incompatible interpolated string handler found in $assembly"
  fi
  if LC_ALL=C grep -aFq 'ToStringAndClear' "$assembly"; then
    fail "Unity/Mono-incompatible interpolated string handler call found in $assembly"
  fi
done

if monodis --assemblyref "$core_dll" | grep -Eq 'Name=0Harmony|Name:[[:space:]]+0Harmony'; then
  fail "TUFHelperLite.Core.dll must not reference 0Harmony"
fi

printf 'Unity/Mono compatibility verified for %s assemblies.\n' "${#assemblies[@]}"
