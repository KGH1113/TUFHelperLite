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

The browser side will add a button to pages like:

```text
https://tuforums.com/levels/:id
```

When clicked, the browser helper should download or resolve the level package
and ask the local mod to open it through AdofaiIpc.

## Planned Flow

```text
tuforums.com level page
-> browser userscript or extension injects a button
-> button calls local AdofaiIpc
-> TUFHelperLite downloads or receives the level payload
-> TUFHelperLite opens the level in ADOFAI
```

## Runtime

Required at runtime:

- A Dance of Fire and Ice
- UnityModManager
- AdofaiIpc 0.3.0 or newer. A missing dependency is installed automatically. Disabled,
  outdated, installation-failure, and load-failure states are shown by the shared AdofaiIpc dialog.
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

The package command creates both `build/TUFHelperLite.zip` and
`build/TUFHelperLite.zip.sha256`. Upload both files, without renaming them, to a
stable GitHub release tagged `vX.Y.Z`; `X.Y.Z` must match `Info.json`.

TUFHelperLite 0.1.4 uses a fixed launcher and versioned runtimes. The launcher
checks the latest stable GitHub release before loading the core, verifies the
ZIP and checksum, and can activate a newer runtime in the same game launch.
Network or verification failures leave the current runtime untouched.

The release ZIP intentionally keeps the flat layout accepted by the official
0.1.2 updater. An existing 0.1.2 installation can therefore download 0.1.4
automatically; the 0.1.4 migration bridge then prepares the fixed dependency
shim and launcher and asks for one full game restart. If AdofaiIpc is older than
0.3.0, the shared dialog also asks for a one-time AdofaiIpc reinstall before that
restart. New installations seed the versioned runtime and load normally in the
same launch.

The published 0.1.3 updater cannot complete this transition. Users who manually
installed 0.1.3 must manually reinstall TUFHelperLite 0.1.4 once. Releases after
0.1.4 are handled by the fixed launcher without another manual reinstall. The
core does not own a separate IPC compatibility modal or placeholder namespace.

## Tech Stack

- **C# / .NET SDK**: mod implementation and build tooling.
- **netstandard2.1**: target framework for Unity compatibility.
- **UnityModManager**: ADOFAI mod loading.
- **AdofaiIpc**: local browser-to-mod IPC.
- **Newtonsoft.Json**: IPC payload serialization.
- **Bash / .env**: local build and install configuration.

## Support

If this project was useful, you can support its development.

<a href='https://ko-fi.com/M4M21YTTBG' target='_blank'><img height='36' style='border:0px;height:36px;' src='https://storage.ko-fi.com/cdn/kofi6.png?v=6' border='0' alt='Buy Me a Coffee at ko-fi.com' /></a>
