#!/usr/bin/env bash

if [ "${TUFHELPER_LITE_GUARDS_LOADED:-0}" = "1" ]; then
  return 0
fi
TUFHELPER_LITE_GUARDS_LOADED=1

fail() {
  printf 'Error: %s\n' "$*" >&2
  exit 1
}

require_file() {
  [ -f "$1" ] || fail "Missing required file: $1"
}

require_dir() {
  [ -d "$1" ] || fail "Missing required directory: $1"
}

require_executable() {
  [ -x "$1" ] || fail "Missing required executable: $1"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "Missing required command: $1"
}

assert_non_root_path() {
  local path="${1%/}"
  [ -n "$path" ] || fail "Refusing to operate on an empty path."
  [ "$path" != "/" ] || fail "Refusing to operate on the filesystem root."
}

assert_child_path() {
  local target="${1%/}"
  local parent="${2%/}"

  assert_non_root_path "$target"
  assert_non_root_path "$parent"
  [ "$target" != "$parent" ] || fail "Refusing to operate on parent path itself: $target"

  case "$target" in
    "$parent"/*) ;;
    *) fail "Refusing to operate outside $parent: $target" ;;
  esac
}

safe_remove_tree() {
  local target="${1%/}"
  local parent="${2%/}"
  assert_child_path "$target" "$parent"
  rm -rf "$target"
}
