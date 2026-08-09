#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"

checked=0
while IFS= read -r script; do
  bash -n "$script"
  checked=$((checked + 1))
done < <(find "$TUFHELPER_LITE_PROJECT_ROOT/scripts" -type f -name '*.sh' | sort)

if command -v shellcheck >/dev/null 2>&1; then
  while IFS= read -r script; do
    shellcheck -x "$script"
  done < <(find "$TUFHELPER_LITE_PROJECT_ROOT/scripts" -type f -name '*.sh' | sort)
else
  printf 'shellcheck is not installed; syntax validation only.\n'
fi

printf 'Validated %s shell scripts.\n' "$checked"
