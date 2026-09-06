# DryCycle AI Observatory V5

Status: implementation in progress.

This document is the implementation baseline for the performance-first AI Flight Recorder architecture agreed for DryCycle. The recorder is track-native, schema-driven, simulation-tick based, and strictly read-only with respect to creature AI. Snapshot/Presentation code is a view over recorder data, not a second AI evaluation path.

## Non-negotiable contracts

- Observatory never re-runs AI decisions or utility methods to populate diagnostics.
- 40 Hz means RainWorldGame simulation ticks, not render frames.
- Recorder hot path performs no string formatting, LINQ, ToArray, reflection for DryCycle-owned species, disk IO, logging, or per-sample heap allocation.
- UI visibility, recorder state, historical cursor, and game pause are independent concepts.
- Presentation and export consume recorder/resolver data; they do not capture live Creature/AI state independently once migration is complete.
- Data loss is never hidden. Buffer overwrite/writer loss is surfaced as diagnostics and timeline gaps.
- Sampling frequency is never silently reduced to hide pressure.

## Core physical storage

Logical trace kinds are state, instant event, counter, span, link and marker. Physical storage stays track-native for cache locality:

- MotionTrack: realized position/velocity at 40 Hz.
- StateTrack: change-driven lifecycle, behavior, target, destination and control state.
- CounterTrack: exact-value change records for counters where continuous 40 Hz storage is unnecessary.
- EventTrack: important discrete events.
- RichTrack: lower-frequency AI/path summaries.
- HeavySnapshotStore: sparse, versioned inspector/utility/perception snapshots.

Storage is preallocated block-backed managed arrays under net48/Mono. Unmanaged storage is not used unless profiling later proves it necessary.

## Tracked entity model

- Selected: one entity, highest detail.
- Pinned: up to three entities, retained capture.
- Retained: history kept but no active sampling.
- Background: entity-browser-only, no long-term history.

Tracked slots retain a stable DebugEntityKey plus a main-thread AbstractCreature handle. The handle is only a fast access handle and never the identity.

## Capture rates

Default Flight Recorder:

- Motion: 40 Hz realized entities.
- Fast AI fingerprint: 40 Hz.
- Rich AI/path: ~10 Hz, phase-staggered across tracked entities.
- Utility: ~5 Hz plus important change triggers.
- Perception: ~5 Hz.
- Heavy inspector: ~2 Hz plus important transitions.
- Events: event-driven.

Abstract entity coordinates are change-driven rather than redundantly written at 40 Hz.

## Recorder lifecycle

Recorder modes:

- Off: no sampling.
- Armed: low-overhead flight recorder for tracked entities.
- Recording: retained session plus writer pipeline.

F7 only controls UI visibility. Closing F7 does not stop an armed/recording trace.

## Simulation tick coordinator

Capture is attached to the RainWorldGame.Update simulation boundary already used by Pause/Step. A normal simulation update produces one recorder tick. Debugger Pause produces no fake gameplay samples. Step produces one real simulation tick and therefore one recorder tick.

## Provider architecture

Providers are split by cost:

- Fast provider: allocation-free 40 Hz data.
- Rich provider: lower-frequency AI/path data.
- Heavy provider: sparse structured inspector data.

DryCycle-owned creatures use direct internal debug-state access, not reflection on the hot path. Vanilla fallback may use compatibility reflection only in low-frequency paths.

## Schema/raw value architecture

Static field metadata is registered once per provider schema. Recorder data stores raw numeric/bool/enum/entity/string-table values rather than formatted strings. Localization and formatting happen at the presentation boundary.

Heavy snapshots use SnapshotId/versioning and change masks so unchanged inspector structures are reused instead of copied every sample.

## Historical resolver

The historical cursor resolves each track with latest-sample-at-or-before-cursor semantics and reports sample age/provenance. Discrete AI state is never interpolated. Position may be interpolated for visualization only when both samples are in the same room and not separated by shortcut/lifecycle transitions; inspector values remain exact samples.

## Presentation boundary

RWImGUI never reads RainWorld/Creature/Room directly. Presentation requests only the visible timeline window and current/historical inspector data. Detached presentation slots are leased/published across threads; if no slot is free, presentation refresh is skipped rather than blocking simulation.

## Timeline

Persistent bottom timeline supports:

- behavior/state spans;
- target/utility/path transitions;
- combat/state/warning/marker events;
- counter graphs;
- zoom/pan;
- live/historical cursor;
- range selection and cached aggregate statistics;
- previous/next event navigation;
- search/filter;
- manual markers.

Only the visible tick range is published to RWImGUI. LOD is built lazily for sealed blocks and preserves first/last/min/max to avoid losing spikes.

## Room/path playback

Room geometry and AIMap/static node data are cached by room. Dynamic path geometry is revisioned and only rebuilt when destination/path revision changes. Historical playback refers to revision IDs rather than rebuilding path data.

## Zero-copy trigger capture

Auto-capture stores start/trigger/end ticks and pins recorder blocks covering the pre-roll window. It does not copy pre-roll frames/events into new lists. Blocks are unpinned after durable session writing/export.

## Session writer

Recording uses a background writer that receives sealed blocks, not per-sample queue objects. Managed blocks are serialized on the worker into an explicit versioned little-endian binary format. NDJSON/CSV are export formats, not the recorder hot format.

Writer pressure never blocks Rain World simulation. Loss is surfaced explicitly if memory/disk pressure makes complete retention impossible.

## Crash recovery

Blocks carry fixed headers with schema version, track type, entity id, tick range, payload size and validation metadata. Incomplete trailing data is ignored during recovery. Sessions remain loadable after a crash and are marked incomplete.

## UI layout

The final workspace contains:

- Recorder/status toolbar
- Entity Browser
- AI & Perception
- Path
- Movement
- Action & State
- Runtime
- Inspector
- persistent Timeline
- Capture/Breakpoint controls

Window movement/resizing remains user-controlled. Full/Compact are presets only.

## Performance targets

Steady-state engineering targets (to be calibrated on the actual Rain World Mono runtime):

- Recorder Off: ~0 overhead, 0 B/tick.
- Armed with zero tracked entities: no world scan, 0 B/tick.
- One Selected: average <0.20 ms, P95 <0.50 ms, 0 B/tick steady-state.
- One Selected + three Pinned: average <0.35 ms, P95 <0.75 ms.
- Burst: P95 target <1.5 ms.

Profile build uses optimization with portable symbols. Debug/Profile/Release keep the same recorder behavior and differ only in compiler optimization/symbol/assert configuration.

## Implementation order

1. Recorder kernel: block tracks, selected/pinned slots, simulation-tick capture.
2. Schema/raw-value and rich/heavy providers.
3. Historical resolver/read API.
4. Presentation migration; remove duplicate live Capture path.
5. Timeline viewport/LOD and UI shell.
6. AI/Utility/Perception/Path/Movement panels.
7. Zero-copy anomaly capture/breakpoints.
8. Block writer/crash recovery/session format.
9. Async export and offline viewer.
10. Optional deep profiling and external analysis exporters.

Each phase must remain buildable and preserve current Observatory functionality while the migration is in progress; implementation does not stop for manual confirmation unless a runtime-only defect requires user evidence.
