# ADOFAI-IPC v2 integration

The `tufhelperlite` feature message major is 1.

The mod bundles ADOFAI-IPC product version 2.0.0, contract major 1 with assembly identity 1.0.0.0, and WebSocket wire major 3. Its feature message major is explicit and checked by the browser SDK. The DLLs, manifest, MIT license, source revision and SHA-256 values are committed under `vendor/adofai-ipc-runtime`; SDK sources are vendored separately.

The mod references only Contracts and Loader. `Info.json` invokes the mod's own entry point. Loader selects one compatible stable runtime from `Mods/<mod>/ipc/manifest.json`; it checks the minimum runtime and required capabilities. Registration disposal cancels that feature's work and removes its subscriptions without stopping the process host. There is no IPC dependency installation, UMM activation, migration bridge or bootstrap trial state.

A package includes `AdofaiIpc.Contracts.dll`, `AdofaiIpc.Loader.dll`, `ipc/AdofaiIpc.Runtime.dll` and `ipc/manifest.json`. Contracts and Loader resolve to one contract assembly identity across mods. Install the full mod package and restart the game when moving from v1. Replacing a bundle does not replace an already loaded process runtime.

The SDK declares the feature major with `connection.namespace(name, major)`. Missing or different majors fail with `feature_protocol_mismatch` before domain traffic. Wire protocol 3 and existing commands, DTO casing, correlation IDs, cancellation and reconnect snapshots are retained.

Standalone .NET and browser tests validate compilation, update/package handling, DTO behavior and reconnect logic. Actual Unity/Mono multi-mod loading and shutdown still require an in-game check before release. This migration does not publish releases or PRs.
