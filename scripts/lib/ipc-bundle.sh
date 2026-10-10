#!/usr/bin/env bash
# Bundled IPC v2. No dependency installation or loaded-runtime replacement.
verify_ipc_bundle() {
  require_file "$ADOFAI_IPC_BUNDLE/SHA256SUMS"
  (cd "$ADOFAI_IPC_BUNDLE" && shasum -a 256 -c SHA256SUMS)
}
copy_ipc_bundle() {
  local destination="$1" name temporary
  for name in AdofaiIpc.Contracts.dll AdofaiIpc.Loader.dll ipc/AdofaiIpc.Runtime.dll ipc/manifest.json; do
    mkdir -p "$destination/$(dirname "$name")"
    temporary="$(mktemp "$destination/$name.XXXXXX")"
    cp "$ADOFAI_IPC_BUNDLE/$name" "$temporary"
    mv -f "$temporary" "$destination/$name"
  done
  mkdir -p "$destination/Notices"
  cp "$ADOFAI_IPC_BUNDLE/LICENSE" "$destination/Notices/AdofaiIpc-LICENSE"
}
