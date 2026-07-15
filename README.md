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
- AdofaiIpc, which is installed automatically when it is missing
- TUFHelperLite installed under the ADOFAI `Mods/TUFHelperLite` directory

## Build

Build and install locally:

```bash
./build.sh
```

Create a package zip:

```bash
./package.sh
```

The package command creates both `build/TUFHelperLite.zip` and
`build/TUFHelperLite.zip.sha256`. Upload both files, without renaming them, to a
stable GitHub release tagged `vX.Y.Z`; `X.Y.Z` must match `Info.json`.

TUFHelperLite's fixed bootstrap checks the latest stable release before loading
the core assembly. It allows up to 20 seconds for release metadata, checksum,
and package downloads, then verifies, stages, and applies a newer core in the
same game launch. Network or verification failures fall back to the installed
version. The first release that introduces `TUFHelperLite.Core.dll` must be
installed manually once so the fixed bootstrap is present.

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
