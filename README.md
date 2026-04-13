# FlyCorp Multi Route Delete

`FlyCorp Multi Route Delete` is a BepInEx 6 IL2CPP mod for Fly Corp that adds batch route deletion, a route-management workflow inside `Statistics -> Routes`, an `80%` bulk refund override for mod-driven clears, and seam-wrapped visuals for long trans-Pacific routes.

## Current Release

- Version: `0.5.3`
- Target game: Fly Corp
- Target Unity runtime: `2022.3.62f2`
- Target mod loader: `BepInEx 6 IL2CPP`

## Features

- Batch route deletion inside `Statistics -> Routes`
- Per-row `Select` toggles plus `Delete Selected`, `Delete All`, and `Clear`
- Batched route sale execution to reduce long stalls during large clears
- `80%` refund override for mod-driven bulk deletes
- One-time startup confirmation dialog so the player can verify the mod loaded
- Seam-wrapped route visuals for long left/right-edge crossings such as `Los Angeles - Tokyo`
- Temporary seam-wrap diagnostics written to `BepInEx/LogOutput.log` to debug route placement issues
- Seam-wrap anchors are now converted into each `PathCreator`'s local coordinate space before assignment
- Seam-wrap splines are built from the full anchor list using `BezierPath(IEnumerable<Vector3>, ...)` instead of the center-point constructor

## How It Works

- The route-management UI is injected into Fly Corp's existing `RoutesStats` screen.
- Each route in a batch is still sold by driving Fly Corp's normal route-sale path.
- The mod applies an `80%` override only for mod-driven bulk deletions.
- Long seam-crossing routes get an alternate wrapped spline plus mirrored visual pathing so the route follows the short edge-crossing path on the world map.

## Build Requirements

This repository does not commit BepInEx binaries, Fly Corp interop DLLs, or a local .NET SDK. Populate the local dependency folders described in [deps/README.md](deps/README.md) first.

Build with a system SDK:

```bash
dotnet build FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj -c Release
```

Or with a locally bootstrapped SDK:

```bash
./deps/dotnet/dotnet build FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj -c Release
```

Output DLL:

`FlyCorpMultiRouteDelete/bin/Release/netstandard2.1/FlyCorpMultiRouteDelete.dll`

## Install

1. Install `BepInEx 6 IL2CPP` into the Fly Corp game folder.
2. Launch Fly Corp once so BepInEx generates the IL2CPP interop assemblies.
3. Build this project.
4. Copy `FlyCorpMultiRouteDelete.dll` into `BepInEx/plugins`.
5. Launch Fly Corp from Steam.
6. Confirm the startup dialog appears.
7. Open `Statistics -> Routes` to use the batch controls.

Example game path:

`H:\SteamLibrary\steamapps\common\Fly Corp`

## Repository Layout

- `FlyCorpMultiRouteDelete/`: plugin source and project file
- `docs/TECHNICAL_MANUAL.md`: implementation and maintenance details
- `CHANGELOG.md`: reconstructed milestone history
- `deps/README.md`: local dependency setup notes

## History Note

This repository was created after the original modding session. The milestone commits in this repo are reconstructed from the working mod, behavior notes, and release progression rather than recovered from an original git history.
