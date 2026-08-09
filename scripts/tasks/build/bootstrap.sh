#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"

configuration="${1:-Debug}"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" build \
    "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.Bootstrap/TUFHelperLite.Bootstrap.csproj" \
    --configuration "$configuration" \
    -p:OutputPath="$TUFHELPER_LITE_BOOTSTRAP_BUILD_OUTPUT/" \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL"
