#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"

configuration="${1:-Debug}"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" build \
    "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.Launcher/TUFHelperLite.Launcher.csproj" \
    --configuration "$configuration" \
    -m:1 -nodeReuse:false \
    -p:NuGetAudit=false \
    -p:OutputPath="$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/" \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" build \
    "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.UpdateEngine/TUFHelperLite.UpdateEngine.csproj" \
    --configuration "$configuration" \
    -m:1 -nodeReuse:false \
    -p:NuGetAudit=false \
    -p:OutputPath="$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/" \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL"
