# Changelog

This repository did not start inside git during the original modding session. The version milestone commits in this repo are reconstructed from the final working tree, test notes, and release behavior, not recovered from an original commit history.

## v0.2.0

- Established the reconstructed baseline for the multi-route delete mod
- Added the public repository scaffolding and local dependency notes
- Kept batch route sales on Fly Corp's normal route-sale path

## v0.3.0

- Added one-time startup feedback so the mod confirms it loaded after Fly Corp initializes

## v0.4.0

- Moved the user workflow into `Statistics -> Routes`
- Added per-row route selection plus `Delete Selected`, `Delete All`, and `Clear` actions

## v0.4.1

- Broke bulk route deletion into frame batches to reduce long freezes
- Tightened up the route-tab action strip layout

## v0.4.2

- Raised the mod-driven bulk delete refund to `80%`

## v0.5.0

- Added seam-wrapped route visuals and plane wrapping for long map-edge crossings
- Published the repository with public-facing documentation and a technical manual

## v0.5.1

- Added detailed seam-wrap diagnostics to `BepInEx/LogOutput.log`
- Logged map bounds, route endpoints, wrap shifts, path transforms, and generated anchors to diagnose bad spline placement

## v0.5.2

- Fixed seam-wrap spline assignment to use each `PathCreator`'s local coordinate space instead of raw world coordinates
- Kept the temporary seam-wrap diagnostics in place for validation

## v0.5.3

- Fixed seam-wrap spline construction to use the `BezierPath(IEnumerable<Vector3>, bool, PathSpace)` constructor
- Removed the incorrect center-point spline initialization that was producing long unintended arcs

## v0.5.4

- Disabled the experimental seam-wrap route rendering code path
- Kept the route-deletion workflow, batch sales, startup feedback, and `80%` bulk refund override intact
