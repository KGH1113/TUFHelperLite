#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" run \
    --project "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.Tests/TUFHelperLite.Tests.csproj" \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:AdofaiMods="$ADOFAI_MODS_DIR" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL" \
    -p:HarmonyDll="$HARMONY_DLL"
