# FlyCorp Multi Route Delete

Fly Corp mod that adds an in-game workflow for deleting more than one route at a time while still using the game's normal single-route sale path.

## Status

- Current repository milestone: `v0.2.0` reconstructed baseline
- Game target: Fly Corp on Windows
- Mod loader target: BepInEx 6 IL2CPP

## Features In This Milestone

- Multi-route delete workflow exposed in-game
- Batch sale execution still driven through Fly Corp's route sale logic

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

Example game path:

`H:\SteamLibrary\steamapps\common\Fly Corp`

## Notes

- This repository history is reconstructed. The original modding session was not tracked in git.
- Later commits in this repo add the startup feedback, route-tab UX refinements, bulk refund override, and seam-wrapping route visuals.
