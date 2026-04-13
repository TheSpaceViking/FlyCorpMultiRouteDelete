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
  - Main plugin entrypoint, Harmony patches, UI injection, batch-sale pipeline, refund override logic, and seam-wrap behavior.
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
- `PluginVersion`: `0.5.2`

### Harmony Patch Surface

The plugin patches these Fly Corp methods:

- `RouteItem.Fill`
- `RouteItem.OnDisable`
- `RoutesStats.OnEnable`
- `RoutesStats.FillRoutesPanel`
- `PlaneMover.RunTheRoute`
- `PlaneBehavior.Run`
- `RouteInfoUIController.SellRoute(PlaneMover, bool, bool)`

### Runtime Components

- `BatchRunner`
  - Hidden `MonoBehaviour` used to process queued route sales over multiple frames.
- `RouteItemState`
  - Tracks route row UI state and selection behavior.
- `WrappedRouteVisualState`
  - Tracks seam-wrap state for a route, including the mirrored path object.
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

## Seam-Wrapped Route Visuals

### Goal

Fly Corp's world map is a left/right-wrapping projection. Some long routes visually travel the long way across Eurasia instead of crossing the map seam. The mod corrects that for long edge-crossing routes.

### Detection

The mod:

1. collects city coordinates from Fly Corp runtime data
2. estimates world-map bounds
3. compares the X delta between route endpoints
4. treats the route as a seam-crossing route when the direct X delta exceeds half the map width

### Path Rebuild

For seam-crossing routes, the mod:

- computes a wrapped X shift of one map width
- rebuilds the route path as a new `BezierPath`
- applies that wrapped spline to the route path creators used by the route and its planes

### Mirrored Visual Path

To make the path visible on both sides of the seam, the mod clones the visual path object and applies an opposite offset. The mirrored object is set to `Ignore Raycast` so it does not create extra interaction targets.

### Plane Wrapping

Planes on seam-crossing routes are also wrapped:

- if a wrapped route moves left past the minimum X bound, the plane is shifted right by one map width
- if a wrapped route moves right past the maximum X bound, the plane is shifted left by one map width

### Maintenance Loop

The mod periodically refreshes seam-wrap state so newly created routes or removed routes stay in sync.

Current setting:

- `RouteWrapMaintenanceIntervalFrames = 180`

## Seam-Wrap Diagnostics

Versions `0.5.1+` add targeted seam-wrap diagnostics to `BepInEx/LogOutput.log`.

The diagnostics currently log:

- recalculated map bounds and sampled city count
- route start and end coordinates
- computed seam-wrap shift
- route transform details
- each affected `PathCreator` transform
- generated anchor positions in world space
- the same anchor positions expressed relative to the path transform

This is intended to expose whether the wrap spline is being assigned in the wrong coordinate space.

Version `0.5.2` also changes the wrap implementation to assign spline anchors in the `PathCreator`'s local space using `Transform.InverseTransformPoint`.

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
- A route such as `Los Angeles - Tokyo` wraps across the map seam.
- Planes on a wrapped route reappear correctly across the seam.

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
