# AI Observatory V5 Implementation Progress

This file tracks implementation checkpoints only. The normative architecture is `docs/AI-Observatory-V5.md`.

## Completed

- V5 recorder kernel and block-backed Motion/State tracks.
- Simulation-tick integration with Pause/Step.
- Selected entity binding independent of F7 visibility.
- Allocation-free DesertBatfly fast provider.
- Profile build configuration.

## In progress

- Recorder read API / historical resolver.
- Presentation migration away from duplicate live `AIDebugRegistry.Capture` calls.
- Structured rich/heavy provider path.

## Remaining

- Timeline viewport/LOD and full V5 UI layout.
- Utility/Perception/Path/Movement historical panels.
- Selected + pinned UI and comparison.
- Zero-copy anomaly capture.
- Block writer/session persistence and crash recovery.
- Async export/offline viewer.
- Optional deep profiler.
