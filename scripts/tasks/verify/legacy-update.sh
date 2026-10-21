#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"
# shellcheck source=../../lib/guards.sh
source "$TASK_DIR/../../lib/guards.sh"

# This is an actually published package, not a reconstructed pre-SDK commit.
archive="$TUFHELPER_LITE_PROJECT_ROOT/build/migration-evidence/published-0.1.9/TUFHelperLite.zip"
expected="7a04475a96727787c516ea86ec3c7b5d381e5d442ba6cdacd663664d94a29fc1"
if [ ! -f "$archive" ]; then
  require_command gh
  mkdir -p "$(dirname "$archive")"
  gh release download v0.1.9 --repo KGH1113/TUFHelperLite \
    --pattern TUFHelperLite.zip --dir "$(dirname "$archive")"
fi
actual="$(shasum -a 256 "$archive")"
if [ "${actual%% *}" != "$expected" ]; then
  printf 'Published v0.1.9 package checksum mismatch.\n' >&2
  exit 1
fi
require_command unzip
unzip -q -o "$archive" -d "$(dirname "$archive")/extracted"
