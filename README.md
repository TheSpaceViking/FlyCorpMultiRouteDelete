# FlyCorp Multi Route Delete

Fly Corp mod that adds an in-game workflow for deleting more than one route at a time while still using the game's normal single-route sale path.

## Status

- Current repository milestone: `v0.4.0` reconstructed routes-tab workflow release
- Game target: Fly Corp on Windows
- Mod loader target: BepInEx 6 IL2CPP

## Features In This Milestone

- Multi-route delete workflow exposed in `Statistics -> Routes`
- Per-row `Select` toggles for choosing routes
- `Delete Selected`, `Delete All`, and `Clear` controls inside the routes tab
- Batch sale execution still driven through Fly Corp's route sale logic
- One-time startup confirmation dialog after the game initializes

## Build

Populate the local dependency folders described in [deps/README.md](/mnt/h/My%20Repo/FlyCorpMod/deps/README.md), then run:

```bash
dotnet build FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj -c Release
```

If you use a local SDK bootstrap inside `deps/dotnet`, this also works:

```bash
./deps/dotnet/dotnet build FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj -c Release
```

## Install

1. Install `BepInEx 6 IL2CPP` into the Fly Corp game folder.
2. Launch the game once so BepInEx can generate IL2CPP interop assemblies.
3. Build the project.
4. Copy `FlyCorpMultiRouteDelete.dll` into `BepInEx/plugins`.
5. Launch Fly Corp from Steam.
6. Open `Statistics -> Routes` and use the route-tab controls.

Example game path:

`H:\SteamLibrary\steamapps\common\Fly Corp`

## Notes

- This repository history is reconstructed. The original modding session was not tracked in git.
- Later commits in this repo add route-tab UX refinements, the bulk refund override, and seam-wrapping route visuals.
