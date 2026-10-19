<div align="center">

![TUFHelperLite](https://capsule-render.vercel.app/api?type=waving&height=220&color=0:050505,45:101827,100:5eead4&text=TUFHelperLite&fontColor=e6fffb&fontAlignY=38&desc=TUF%20Level%20Download%20And%20Open%20Bridge&descAlignY=58&animation=fadeIn)

[![Runtime](https://img.shields.io/badge/runtime-ADOFAI%20%2F%20Unity-111827?style=for-the-badge&logo=unity&logoColor=white)](https://store.steampowered.com/app/977950/A_Dance_of_Fire_and_Ice/)
[![Mod Loader](https://img.shields.io/badge/mod%20loader-UnityModManager-7c3aed?style=for-the-badge)](https://www.nexusmods.com/site/mods/21)
[![Standalone](https://img.shields.io/badge/framework-none-059669?style=for-the-badge)](#runtime)
[![IPC](https://img.shields.io/badge/ipc-AdofaiIpc-f97316?style=for-the-badge)](#planned-flow)
[![Target](https://img.shields.io/badge/target-netstandard2.1-2563eb?style=for-the-badge)](https://learn.microsoft.com/dotnet/standard/net-standard)

<br />

![Tech stack](https://skillicons.dev/icons?i=cs,dotnet,unity,bash)

**Open TUF forum levels from the browser into ADOFAI**

</div>

<div align="center">
  <a
    href="docs/media/showcase.gif"
    aria-label="TUFHelperLite Shocase GIF"
  >
    <img
      src="docs/media/showcase.gif"
      alt="TUFHelperLite Shocase GIF"
      width="900"
    />
  </a>
  <br />
</div>

## Overview

TUFHelperLite is an ADOFAI mod that exposes a local IPC bridge for
opening TUF forum levels from browser tooling.

The companion web client's **TUFHelperLite Settings** contains temporary download
mode, storage location and **Manage downloads**. Storage can move to a
user-selected empty folder. TUFHelperLite copies and verifies every file before
switching to the new location, resumes interrupted migrations on the next game
launch, and removes the previous folder only after the new copy is active.

The browser side will add a button to pages like:

```text
https://tuforums.com/levels/:id
```

When clicked, the browser helper should download or resolve the level package
and ask the local mod to open it through AdofaiIpc.

## Browser connection

The TUF website registers an IPC app and uses the generated `TufHelperContract`.
The mod implements generated methods and calls the registered app's handlers
for job, library, storage and batch changes. After reconnecting, each app
receives a fresh snapshot. Starting or cancelling a job is never replayed
automatically after a lost connection.

The contract source is `ipc/main.tsp` in this repository. Generated C# is
compiled into the mod; the generated TypeScript package and SDK are pinned in
the frontend's `vendor/` directory. Each repository builds from its own files.

### Updating the contract

```bash
npm --prefix ipc ci
npm --prefix ipc run generate
npm --prefix ipc run check
TUFHELPER_LITE_SKIP_INSTALL=1 ./scripts/run.sh build
```

Generation uses the versioned tool archive in `vendor/adofai-ipc-typespec/`.
Copy the complete `ipc/generated/typescript/` directory to the frontend's
`vendor/tufhelper-ipc/` directory, then run `npm ci`, `npm run sdk:check` and
`npm run test:ipc` in that repository. `source.json`
records the contract hash, source checksum and generator version. Generated
files are not edited manually; `ipc/SHA256SUMS` verifies the pinned inputs and
outputs. Runtime DLLs remain pinned separately in `vendor/adofai-ipc-runtime/`.

## Runtime

Required at runtime:

- A Dance of Fire and Ice
- UnityModManager
- ADOFAI-IPC 2.0.0 DLL bundle included with the mod (contract major 1, wire major 3).
- TUFHelperLite installed under the ADOFAI `Mods/TUFHelperLite` directory

## Build

Build and install locally:

```bash
./scripts/run.sh build
```

Create a package zip:

```bash
./scripts/run.sh package
```

Validate the shell workflow:

```bash
./scripts/run.sh check
```

Local builds verify `vendor/adofai-ipc-runtime/SHA256SUMS`. Contracts and Loader are copied to the mod root; Runtime and manifest are copied to `ipc/`. No common IPC mod or dependency bootstrap is installed. IPC minimum runtime 2.0.0 and required capabilities are checked before feature registration. Disabling the mod disposes its registration and keeps the process host running.

The package command creates both `build/TUFHelperLite.zip` and
`build/TUFHelperLite.zip.sha256`. Upload both files, without renaming them, to a
stable GitHub release tagged `vX.Y.Z`; `X.Y.Z` must match `Info.json`.

TUFHelperLite 0.2.1 uses a fixed launcher and versioned runtimes. The launcher
checks the latest stable GitHub release before loading the core, verifies the
ZIP and checksum, and can activate a newer runtime in the same game launch.
Network or verification failures leave the current runtime untouched.

The IPC v2 transition requires one full manual reinstall and game restart from an older dependency-shim installation. The v2 launcher continues to verify and stage TUFHelperLite updates; candidate packages must contain the entire compatible IPC bundle. Files are replaced by rename, and an IPC runtime already selected by another mod is kept until restart.

### 0.2.1 highlights

- Add temporary downloads that replace the previous level while preserving the saved library.
- Group temporary mode, storage location and download management in TUFHelperLite Settings.
- Delete individual saved levels from Manage downloads without reloading the loaded list.
- Keep automatic disconnect and reconnect handling quiet and preserve dismissed connection notices.

### 0.2.0 highlights

- Bundle ADOFAI-IPC 2.0.0 with contract major 1 and use bidirectional WebSocket commands and events.
- Push job, storage, folder picker, library and batch changes to subscribed browsers.
- Observe only visible downloaded level IDs, keeping large libraries paginated.
- Separate application change/native picker ports from IPC and Unity adapters.

### 0.1.9 highlights

- Update the AdofaiIpc integration and bundled bootstrap artifacts to 0.4.0.
- Require AdofaiIpc 0.4.0 or newer for the dependency bootstrap transition.

### 0.1.8 highlights

- Restore compatibility with ADOFAI's Unity/Mono runtime after the 0.1.7 build
  emitted unsupported interpolated string handler calls.
- Make release builds reproducible and reject Unity-loaded assemblies that
  reference unsupported interpolated string handlers.

### 0.1.7 highlights

- Resolve level metadata before showing the download overlay, so real artwork
  and level details are present when the UI first appears.
- Check installed TUF levels for updates before opening them while preserving
  the existing URL-only open flow and external IPC contract.
- Warn in the level editor when an installed older revision is opened, with a
  timed, hover-pausable toast positioned above the editor controls.

### 0.1.6 highlights

- Check every downloaded level for updates in one bounded background batch,
  while preserving per-level progress and failure details for the web client.
- Reconnect an existing managed download folder after reinstalling the mod by
  selecting it through the same folder picker used for storage migration.
- Mark managed storage with a small identity file so reconnect remains explicit
  and does not adopt an arbitrary non-empty folder.

### 0.1.5 highlights

- Add configurable download storage, with verified copy, atomic cutover, and
  restart recovery when moving existing downloads.
- Expose storage migration and downloaded-level library capabilities to the TUF
  web client through the local IPC bridge.
- Store per-level metadata and provide cursor-paginated downloaded-level pages,
  so the web client can browse a large local library without loading it all at once.

## Download Storage

Downloads use `Mods/TUFHelperLite/Downloads` until the user selects another
empty folder from TUFHelperLite Settings on the TUF website. Folder selection is
performed by the local mod; the browser receives a one-time selection token and
cannot submit an arbitrary filesystem path.

Storage migration runs in the background and temporarily blocks new download
jobs. Keep ADOFAI open and do not edit either directory until it finishes. The
source remains active during copy and verification. After a verified cutover,
cleanup failures leave the new directory active and are retried on the next
launch.

Storage methods are `pickFolder`, `changeStorage`, `retryChange`, and
`cancelChange`. The mod calls the app's `storageChanged` handler as the
operation advances. `folderSelection` is called only on the app session that
opened the picker. Selection tokens are never included in broadcasts or
reconnect snapshots and cannot be used by another session.

### Temporary downloads

Enable **Temp download mode** in TUFHelperLite Settings to download directly into
`Mods/TUFHelperLite/Downloads/temp/`. Each successful download replaces the previous
temporary level; no `tuf-{id}` directory is created inside `temp/`. Failed or
cancelled downloads leave the previous temporary payload intact. Temporary
levels do not enter the saved download library.

The mode persists in `Settings.json`. Its directory stays fixed even if saved
downloads use a custom location. Disabling the mode restores that saved location
and its library. Storage location and Manage downloads are hidden while temporary
mode is enabled. **Flush temp** deletes the contents of `temp/` and retains the
directory; its information tooltip explains this behavior.

Finish or cancel active jobs and level selections before changing the mode or
flushing temporary files. A replacement is downloaded and verified first. If the
old temporary level is open, the mod uses the game's normal exit transition,
including its unsaved-changes prompt, before activating the replacement. Cancel
the download while it waits to retain the old files. Flush temp requires closing
the level first. Files reserved by a pending level selection or queued game-thread
open cannot be replaced.

The generated methods `setTempDownloadMode` and `flushTemp` return the current
settings. The initial snapshot includes `settings`, and `settingsChanged` pushes
later changes to every registered app, including after reconnecting.

## Download Library

The download manager reads real `tuf-{id}` folders through cursor-based IPC.
Pages are ordered by download time and contain at most 50 entries; the default
page size is 20. Existing downloads are enriched lazily and store a small
`.tufhelperlite-level.json` manifest inside their level directory. Library count
and payload size are kept separately in `DownloadLibrarySummary.json`, so no
full level catalog needs to be loaded into memory.

The browser calls `libraryPage` with a cursor, direction and page limit.
`libraryChanged` carries the updated summary and revision. `watchLibrary`
accepts up to 500 currently visible level IDs and returns membership to that
app; later changes use its `membershipChanged` handler. Neither
initial snapshots nor progress events load and broadcast the complete library.
Opaque cursors belong to a library revision and are discarded when the server
reports `download_library_cursor_stale`.

**Manage downloads** can delete individual saved levels through `deleteLevel`.
Deletion accepts a numeric TUF ID, removes only its managed `tuf-{id}` directory,
and updates library summaries and membership notifications. An already removed
level is a harmless no-op. Deletion is blocked during active work, while the level
is open, or when its directory contains filesystem links. Temporary mode blocks
saved-library management methods as well as hiding the controls.

The initial `snapshot` contains health, jobs, storage, batch and summary.
`jobsChanged` and `batchChanged` push later updates. Methods such as
`openFromId` return the started job directly. A non-blocking application change
port coalesces progress mutations; the IPC adapter drains only dirty topics
outside application locks. A separate queue for each app retains the latest
value per topic, sends its initial snapshot first, and discards pending
notifications when that session disconnects.

## Tech Stack

- **C# / .NET SDK**: mod implementation and build tooling.
- **netstandard2.1**: target framework for Unity compatibility.
- **UnityModManager**: ADOFAI mod loading.
- **AdofaiIpc**: local browser-to-mod IPC.
- **UnityFileDialog**: native folder selection on Windows and Linux; macOS uses its native AppleScript picker.
- **Newtonsoft.Json**: IPC payload serialization.
- **Bash / .env**: local build and install configuration.

## Support

If this project was useful, you can support its development.

<a href='https://ko-fi.com/M4M21YTTBG' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>
