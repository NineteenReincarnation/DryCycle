# DryCycle development progress

Last updated: 2026-09-19

## Current milestone

DryCycle's DevTool migration has reached a stable page-less native boundary for the **Objects**, **Sound**, and **Triggers** workspaces. `NativeToolScheduler` owns these three workspaces through `NativeToolAnchorPage`; the matching vanilla `DevInterface` pages are materialized only for explicit Vanilla/Legacy presentation or diagnostics.

The most recent Objects pass closed the remaining direct `ObjectsPage.CreateObjRep` representation gaps, including native authoring/runtime ownership for TerrainGrassPatch, SpinningTopSpot, RippleEggDestination, OEsphere, Daemon objects, CosmeticRipple, AetherRainbow and the remaining direct-handle/structured-inspector cases. The Objects validation guard now treats the detached snapshot/command boundary and native runtime reconciliation as contracts.

## Verified repository state

- `main` implementation head before this progress update: `eae5f2faafbf34459abbe59a4681e10e07f0ccad` (`Guard remaining direct Representation native coverage`).
- GitHub Actions on the implementation head are green: `guard`, `build`, `verify-rainworld-binaries`, and `verify-rainworld-decompile` all completed successfully.
- No open GitHub issues are currently tracking unfinished work, so this file is the canonical continuation pointer for unattended development runs.

## Completed DevTool native boundaries

### Objects

- Page-less native workspace ownership.
- Detached position and object-geometry gizmo snapshots/commands.
- Continuous history transactions for drag/edit operations.
- Structured inspectors for vanilla cases that cannot be represented faithfully by generic reflection alone.
- Selected-only compatibility sandbox for unknown third-party representations without full `ObjectsPage.Refresh()` materialization.
- Native runtime reconciliation for representation-created gameplay/visual runtime objects.
- Runtime prepare/refresh/remove/reset lifecycle integrated with edit, history, creation and deletion paths.
- Regression guard in `scripts/validate-devtool-native-objects-runtime.sh` covering the native boundary and migrated special cases.

### Sound

- Page-less native workspace ownership through the shared native scheduler/anchor boundary.
- Native presentation remains independent from ordinary `SoundPage` lifetime except explicit legacy fallback.

### Triggers

- Page-less native workspace ownership through the shared native scheduler/anchor boundary.
- Native presentation remains independent from ordinary `TriggersPage` lifetime except explicit legacy fallback.

## Next development milestone

Continue the DevTool rebuild with the remaining canonical workspaces rather than adding more Objects special cases blindly.

Priority order:

1. **Map** — inventory the current `MapPage` responsibilities, split model/runtime behavior from presentation-only controls, and define a detached native snapshot/command boundary before making it page-less.
2. **Dialog** — perform the same ownership inventory and migrate only after Map's boundary is stable.
3. **Relationships** — migrate last because its editing semantics are more coupled to creature relationship data and should reuse the scheduler/presentation patterns established by Map/Dialog.
4. After each workspace becomes genuinely page-less, extend `NativeToolScheduler.Supports`, `LegacyPageIndex`, exact legacy-page detection, native anchor documentation, and add a dedicated validation guard. Do not add a workspace to `Supports` until its native frontend/backend can operate without relying on the legacy page as a hidden runtime backend.

## Immediate next run

Start with a **Map ownership inventory**. Locate every DryCycle reference to `MapPage`, classify each as presentation, model mutation, runtime side effect, compatibility/diagnostics, or navigation/lifetime ownership, then implement the smallest first extraction that removes a real legacy dependency. Keep Vanilla/Legacy fallback intact while the native Map boundary is incomplete.

## Completion criteria

The DevTool migration is complete when all intended workspaces have native presentation/model boundaries, legacy pages are used only by explicit Vanilla/Legacy/diagnostic compatibility paths, repository guards encode those ownership rules, and all CI/build/decompile verification remains green. Do not declare the project complete merely because the Objects representation inventory is exhausted.
