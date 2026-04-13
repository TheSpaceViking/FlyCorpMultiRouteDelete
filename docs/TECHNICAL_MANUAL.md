# Technical Manual

## Scope

This document covers the architecture, local build requirements, runtime behavior, and maintenance workflow for the `FlyCorp Multi Route Delete` mod.

## Compatibility

- Game: Fly Corp
- Runtime: Unity `2022.3.62f2`
- Loader: `BepInEx 6 IL2CPP`
- Plugin assembly: `FlyCorpMultiRouteDelete.dll`

## Repository History

The original modding work was not tracked in git. This repository reconstructs milestone commits from the final working tree and release notes. The commit history is therefore representative of the feature progression, but not a byte-for-byte recovery of the original local edits.

## Source Layout

- `FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj`
  - Project file with references to BepInEx core assemblies and generated Fly Corp interop DLLs.
- `FlyCorpMultiRouteDelete/Plugin.cs`
  - Main plugin entrypoint, Harmony patches, UI injection, batch-sale pipeline, and refund override logic.
- `deps/README.md`
  - Documents how to populate local BepInEx and Fly Corp interop dependencies.

## External Dependencies

The project intentionally does not commit the following:

- BepInEx runtime binaries
- Fly Corp interop DLLs generated from the user's own installation
- A private .NET SDK bootstrap

The project expects local copies of:

- BepInEx core DLLs under `deps/bepinex/BepInEx/core/`
- Fly Corp interop DLLs under `deps/flycorp-interop/`

Important interop assemblies:

- `Assembly-CSharp.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.UI.dll`
- `Unity.TextMeshPro.dll`
- `PathCreator.dll`
- `Il2Cppmscorlib.dll`
- `Il2CppSystem.dll`
- `Il2CppSystem.Core.dll`
- `UniTask.dll`

## Plugin Architecture

### Entry Point

`Plugin` inherits from `BasePlugin` and is registered by:

- `PluginGuid`: `com.spaceviking.flycorp.multi-route-delete`
- `PluginName`: `FlyCorp Multi Route Delete`
- `PluginVersion`: `0.5.4`

### Harmony Patch Surface

The plugin patches these Fly Corp methods:

- `RouteItem.Fill`
- `RouteItem.OnDisable`
- `RoutesStats.OnEnable`
- `RoutesStats.FillRoutesPanel`
- `RouteInfoUIController.SellRoute(PlaneMover, bool, bool)`

### Runtime Components

- `BatchRunner`
  - Hidden `MonoBehaviour` used to process queued route sales over multiple frames.
- `RouteItemState`
  - Tracks route row UI state and selection behavior.
- `RefundMemberBinding`
  - Reflection helper that locates and overrides the game's refund fields without hard-coding a single member path.

## Route Tab UI Injection

The user workflow is built inside `Statistics -> Routes`.

Injected controls:

- `Select` toggle on each route row
- `Delete Selected`
- `Delete All`
- `Clear`
- selection status label

Design constraints:

- Row clicks must keep vanilla behavior
- Selection state must be visible
- Buttons must feel like part of the shipped UI
- The mod must not depend on an external command window after installation

## Batch Delete Pipeline

### Selection

Selected routes are tracked by route ID inside a `HashSet<string>`.

### Execution

When the user requests a bulk delete:

1. The mod resolves a target route list.
2. The list is processed by `BatchRunner`.
3. A small number of routes are sold each frame.
4. Each sale still passes through Fly Corp's normal route-sale logic.

Current setting:

- `BatchSaleRoutesPerFrame = 8`

This was introduced to reduce long frame stalls during large `Delete All` operations.

## Refund Override

Vanilla route sales are preserved, but mod-driven bulk deletes override the refund target to `80%`.

Implementation notes:

- The mod locates candidate refund members using reflection.
- It reads the vanilla refund that Fly Corp already computed.
- It multiplies that value by `1.6`, which turns a vanilla `50%` delete refund into `80%`.
- The override is applied only while the mod is driving a batch operation.

## Startup Feedback

The mod shows a one-time confirmation dialog after initialization.

Purpose:

- confirms that BepInEx loaded the plugin
- gives the player the active controls
- avoids requiring a terminal window or build session to remain open

## Seam-Wrap Status

Experimental seam-wrap route rendering is disabled in `v0.5.4`.

Reason:

- the route-deletion workflow is stable and shippable
- the seam-wrap path rendering still needed more investigation than was justified for the current goal

The seam-wrap implementation remains in source history for future revisit, but it is not active in the current build.

## Build Workflow

1. Populate `deps/` as documented in `deps/README.md`.
2. Run `dotnet build FlyCorpMultiRouteDelete/FlyCorpMultiRouteDelete.csproj -c Release`.
3. Copy the built DLL into the game's `BepInEx/plugins` folder.
4. Launch Fly Corp from Steam.

## Manual Test Checklist

- Plugin loads and shows the startup confirmation dialog.
- `Statistics -> Routes` renders exactly one control strip.
- Route row selection is visible and does not break vanilla row click behavior.
- `Delete Selected` removes only selected routes.
- `Delete All` removes every route while staying reasonably responsive.
- Batch deletes refund `80%`.

## Release Notes

Published milestone sequence in this repo:

- `v0.2.0`: reconstructed baseline
- `v0.3.0`: startup feedback
- `v0.4.0`: routes-tab workflow
- `v0.4.1`: batched deletion
- `v0.4.2`: `80%` refund override
- `v0.5.0`: seam-wrapped route visuals
- `v0.5.1`: seam-wrap diagnostics
- `v0.5.2`: local-space seam-wrap spline fix
- `v0.5.3`: proper BezierPath anchor-list constructor
- `v0.5.4`: seam-wrap disabled, stable route-deletion-only build
