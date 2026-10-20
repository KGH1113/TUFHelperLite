# ADOFAI-IPC v2 integration

The `tufhelperlite` feature message major is 1.

The mod bundles ADOFAI-IPC product version 2.1.0, contract major 1 with assembly identity 1.0.0.0, and WebSocket wire major 3. The DLLs, manifest, MIT license, source revision and SHA-256 values are committed under `vendor/adofai-ipc-runtime`. The browser installs the exact `adofai-ipc@2.1.0` npm package with registry integrity pinned in its lockfile.

The mod references only Contracts and Loader. `Info.json` invokes the mod's own entry point. Loader selects one compatible stable runtime from `Mods/<mod>/ipc/manifest.json`; it checks the minimum runtime and required capabilities. Registration disposal cancels that feature's work and removes its subscriptions without stopping the process host. There is no IPC dependency installation, UMM activation, migration bridge or bootstrap trial state.

A package includes `AdofaiIpc.Contracts.dll`, `AdofaiIpc.Loader.dll`, `ipc/AdofaiIpc.Runtime.dll` and `ipc/manifest.json`. Contracts and Loader resolve to one contract assembly identity across mods. Install the full mod package and restart the game when moving from v1. Replacing a bundle does not replace an already loaded process runtime.

The TypeSpec source in `ipc/main.tsp` generates the C# contract compiled into the mod and the TS package copied to the browser repository. `RegisterMod(TufHelperContract.Instance, ...)` and `app.use(TufHelperContract, handlers)` register matching typed contracts; mismatched contract hashes fail before domain calls. App callbacks target their registered session, and reconnecting apps receive a new snapshot. Job cancellation remains an explicit typed method, and commands are never replayed automatically after connection loss.

The helper downloads remote chart archives into its game-side library. No helper operation currently transfers a file to the browser or uploads one from it, so the existing chart management flow does not need `ipc.File` or `ReceivedFile.download()`.

Standalone .NET and browser tests validate compilation, update/package handling, DTO behavior and reconnect logic. To include the actual-runtime browser integration test, set `TUFHELPER_IPC_FIXTURE` to the built `build/tests/core/TUFHelperLite.Tests.dll` when running the frontend's `npm run test:ipc`. This is an optional test input, not a dependency of either repository's build. Actual Unity/Mono multi-mod loading and shutdown still require an in-game check.
