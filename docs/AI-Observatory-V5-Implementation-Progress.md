# AI Observatory V5 Implementation Progress

This file tracks implementation checkpoints only. The normative architecture is `docs/AI-Observatory-V5.md`.

## Completed

- V5 recorder kernel and block-backed Motion/State tracks.
- Simulation-tick integration with Pause/Step.
- Selected entity binding independent of F7 visibility.
- Allocation-free DesertBatfly fast provider.
- Profile build configuration.
- Allocation-free recorder historical point queries (`latest sample <= cursor tick`).
- Simulation-tick-owned rich compatibility cache; RWImGUI Presentation no longer performs a second live `AIDebugRegistry.Capture` for the Inspector.
- LIVE / HISTORICAL cursor contract and recorder-backed historical resolution for motion, fast state and rich inspector snapshots.
- RWImGUI historical seek controls (`-1s`, `+1s`, `Return Live`) and recorder cursor diagnostics.
- Schema-first raw-value foundation: static field metadata, compact raw value union and reusable fixed-capacity raw snapshot buffer.

## In progress

- Replacing the compatibility `AIDebugSnapshot` rich payload with schema/raw Rich + Heavy providers.
- Timeline viewport/range query API and persistent bottom Timeline UI.
- Presentation allocation reduction / cached entity metadata.

## Remaining

- Full Timeline LOD, event/state/counter tracks, range selection/search/markers.
- Utility/Perception/Path/Movement historical panels.
- Selected + pinned UI and comparison.
- Room playback / historical path and movement visualization.
- Zero-copy anomaly capture and block pinning.
- Block writer/session persistence and crash recovery.
- Async export/offline viewer.
- Optional deep profiler.
