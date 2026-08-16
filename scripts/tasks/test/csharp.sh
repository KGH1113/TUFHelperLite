#!/usr/bin/env bash
set -euo pipefail

TASK_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=../../lib/context.sh
source "$TASK_DIR/../../lib/context.sh"

core_test_output="$TUFHELPER_LITE_PROJECT_ROOT/build/tests/core"
update_test_output="$TUFHELPER_LITE_PROJECT_ROOT/build/tests/update"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" build \
    "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.Tests/TUFHelperLite.Tests.csproj" \
    --configuration Debug \
    -m:1 -nodeReuse:false \
    -p:OutputPath="$core_test_output/" \
    -p:NuGetAudit=false \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:AdofaiMods="$ADOFAI_MODS_DIR" \
    -p:AdofaiIpcDll="$ADOFAIIPC_DLL" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL" \
    -p:HarmonyDll="$HARMONY_DLL"

DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" "$core_test_output/TUFHelperLite.Tests.dll"

TUFHELPER_LITE_CORE_DLL="$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.dll" \
TUFHELPER_LITE_LAUNCHER_DLL="$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/TUFHelperLite.Launcher.dll" \
TUFHELPER_LITE_UPDATE_ENGINE_DLL="$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll" \
TUFHELPER_LITE_INFO_JSON="$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json" \
ADOFAIIPC_BOOTSTRAP_DLL="$ADOFAIIPC_BOOTSTRAP_DLL" \
ADOFAIIPC_DEPENDENCY_SHIM_DLL="$ADOFAIIPC_DEPENDENCY_SHIM_DLL" \
ADOFAIIPC_MIGRATION_DLL="$ADOFAIIPC_MIGRATION_DLL" \
DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" build \
    "$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite.UpdateTests/TUFHelperLite.UpdateTests.csproj" \
    --configuration Debug \
    -m:1 -nodeReuse:false \
    -p:OutputPath="$update_test_output/" \
    -p:NuGetAudit=false \
    -p:AdofaiManaged="$ADOFAI_MANAGED" \
    -p:UnityModManagerDll="$UNITY_MOD_MANAGER_DLL"

TUFHELPER_LITE_CORE_DLL="$TUFHELPER_LITE_BUILD_OUTPUT/TUFHelperLite.Core.dll" \
TUFHELPER_LITE_LAUNCHER_DLL="$TUFHELPER_LITE_LAUNCHER_BUILD_OUTPUT/TUFHelperLite.Launcher.dll" \
TUFHELPER_LITE_UPDATE_ENGINE_DLL="$TUFHELPER_LITE_UPDATE_ENGINE_BUILD_OUTPUT/TUFHelperLite.UpdateEngine.dll" \
TUFHELPER_LITE_INFO_JSON="$TUFHELPER_LITE_PROJECT_ROOT/TUFHelperLite/Info.json" \
ADOFAIIPC_BOOTSTRAP_DLL="$ADOFAIIPC_BOOTSTRAP_DLL" \
ADOFAIIPC_DEPENDENCY_SHIM_DLL="$ADOFAIIPC_DEPENDENCY_SHIM_DLL" \
ADOFAIIPC_MIGRATION_DLL="$ADOFAIIPC_MIGRATION_DLL" \
DOTNET_ROOT="$DOTNET_ROOT" DOTNET_ROOT_ARM64="$DOTNET_ROOT_ARM64" \
  "$DOTNET_EXE" "$update_test_output/TUFHelperLite.UpdateTests.dll"
