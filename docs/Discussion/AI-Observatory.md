# DryCycle AI Observatory V3

Status: **code implementation complete / pending full Rain World runtime validation**.

AI Observatory is a developer-only Dear ImGui inspection system for Rain World creature AI. Its core rule is strict: **observe the real AI; never become a second AI system**.

It is designed to answer four questions:

1. What is this creature doing now?
2. Which layer owns control, and why?
3. What did the AI know, score and plan immediately before a failure?
4. Can that diagnostic state be frozen, compared, captured and exported without changing the creature's decision?

## Controls

- `F7` — open / close Observatory.
- `F6` — Compact / Full DockSpace workspace.
- `Tab` — `LIVE` / `INTERACT` when ImGui is not already consuming keyboard input.
- `Alt + Left Mouse` — select the nearest realized creature under the relevant RoomCamera.
- Entity Browser click — select entity A.
- `Shift + click` — select comparison entity B.
- Pin — keep an entity watched while selecting another entity.
- Toolbar `Pause World` / `Resume World` — pause/resume the whole RainWorldGame simulation.
- Toolbar `Step 1 Tick` — execute one complete RainWorldGame simulation tick while debugger-paused.
- `Ctrl + Shift + F8` — export the whole retained debug session to JSON.

The UI can switch between Chinese and English at runtime. Raw implementation names remain available separately for exact symbol-level debugging.

## Workspace

Full mode is a real Dear ImGui DockSpace with persistent layout storage. The active V3 workspace contains:

- Entity Browser
- Decision Stack
- Inspector
- Timeline
- Events
- Utility
- Perception / Tracker
- Path / Control
- Compare
- Candidates
- Captures / Breakpoints
- Settings

The obsolete V1/V2 window implementations were removed so they cannot break the SDK build later.

Dock layout persistence uses ImGui's memory ini API plus managed file I/O. Native cimgui does not parse the Windows path, which avoids non-ASCII path problems.

## Input isolation

Observatory detours `RWInput.PlayerInputLogic(int,int)` using the RuntimeDetour assembly already shipped with Rain World's BepInEx environment.

Gameplay commands are neutralized when:

- `INTERACT` mode is active;
- ImGui requests keyboard capture;
- ImGui requests mouse capture.

Controller metadata is preserved. LIVE mode does not steal gameplay input merely because F7 is open.

## Entity identity and multiple cameras

`DebugEntityKey` is based on creature template + EntityID spawner/number rather than a realized Creature reference. This lets watched identity survive ordinary realize/unrealize and room transitions while the AbstractCreature still exists.

`AIDebugCameraUtil` centralizes RoomCamera selection:

- selected/pinned overlays use a camera showing their room;
- Alt+click uses the camera under the mouse;
- split-screen world/screen conversion handles upper/lower camera placement;
- frozen historical overlay is drawn only through a camera showing the historical room.

The Entity Browser groups creatures in currently visible camera rooms separately from the rest of the world.

## Timeline and complete history

Trace time uses **`RainWorldGame.clock` at 40 Hz**, not Unity render frames or wall-clock time.

Normal gameplay records approximately one sample every four simulation ticks (~10 Hz). Debugger Pause does not create duplicate samples, and Step 1 Tick can produce a real single-tick diagnostic sample.

Per retained trace the fixed storage is bounded to:

- up to 1024 events;
- up to 600 frame samples;
- up to 12 retained entity traces;
- user-visible history length of 5–60 seconds.

A full-history frame can retain:

- room, position, velocity and local goal;
- mode/controller, target, role and suppression;
- complete `AIDebugSnapshot` sections and decision nodes;
- Utility rows;
- Tracker / perception rows;
- Path state.

Freeze View therefore reads the historical state captured at that simulation tick instead of rebuilding an old page from current AI state.

## Events

Events record decision/state transitions instead of per-frame spam. Categories are Decision, State, Perception, Path, Combat, Social and Warning.

Each event stores raw detail/reason text. Known names and fixed reasons are translated at presentation time, so switching language also re-renders old events. JSON exports preserve raw diagnostics.

## Read-only Utility diagnostics

Observatory **never calls `AIModule.Utility()`** to fill a debug table.

It also never calls `UtilityTracker.SmoothedUtility()`, because Rain World's implementation can call `module.Utility()` when no smoother exists.

For vanilla UtilityComparer AI, Observatory reads only retained state:

- module name;
- weight;
- continuation-bonus configuration;
- current highest UtilityTracker;
- cached weighted/smoothed value only when a real smoother exists.

Unavailable values display as `—` and export as JSON `null`.

DesertBatfly role scores are safe to display because they are values already calculated by its real role evaluator.

## Read-only Perception / Tracker diagnostics

Observatory reads retained `CreatureRepresentation` fields such as:

- visual contact;
- ticks since seen;
- estimated chance of finding;
- priority;
- last-seen coordinate;
- dynamic relationship type/intensity.

It does **not** call `BestGuessForPosition()` from the debugger. `ElaborateCreatureRepresentation.BestGuessForPosition()` may run `FindBestGhost()` and mutate the tracker's cache. Observatory instead uses an already-clean cached `bestGhost` when the real tracker has resolved one; otherwise it reports `lastSeenCoord`.

This preserves the rule that opening the debugger must not change tracker state.

## Path / Control chain

Locomotion is separated into:

- **INTENT** — AbstractCreatureAI destination;
- **PLANNER** — PathFinder, reachability, returnability and stranded state;
- **MOTOR** — species-specific local movement command / physical execution.

The reachability checks used here read existing pathing cells; Observatory does not call `SetDestination` or rebuild the path.

## Compare and Candidate Inspector

Compare displays entity A/B snapshots side-by-side. Raw names can be enabled for exact implementation comparison.

Instrumented AI can publish candidates already produced by the real decision pass. Candidate Inspector displays set/name, validity, score, winner and reason without running another candidate-selection pass.

## World overlays

World overlays are transient Dear ImGui background drawings and do not create persistent Room objects.

Available layers include:

- selected/pinned markers;
- BodyChunks/radii;
- velocity;
- grasps;
- local movement goal;
- generic path destination;
- Tracker marks;
- creature-specific AImap accessibility heatmap and allowed outgoing connections;
- candidate positions;
- DesertBatfly social/combat diagnostics.

## DesertBatfly adapter

The dedicated DesertBatfly source exposes personality, thirst, custom AI mode/target, retreat/memory counters, social role scores and suppression, Sentinel/Opportunist state, flock snapshot, social bond/trauma/grief, movement state and related vanilla FlyAI state.

Special overlays include:

- Sentinel perimeter, threat, confidence and watch state;
- Opportunist return radius, recovery/window/safe-tick state;
- Bully/formal attack `SLOT 1`, `SLOT 2`, `WAIT` labels and AttackSlots violation warning.

## MossySpider adapter

MossySpider has a deliberately simple non-predatory ecology. Its dedicated adapter exposes only its real systems:

- Roaming / Waiting;
- AbstractAI roam target;
- realized MossySpiderPather destination;
- migration control;
- movement direction;
- gait cycle;
- ground support;
- swim factor.

## SpinebackLizard adapter

SpinebackLizard intentionally uses Green Lizard AI as a baseline through DryCycle compatibility hooks. Its adapter makes that ownership explicit and exposes the live LizardAI plus the normal Utility / Tracker / Path diagnostics.

All other creatures fall back to `GenericCreatureDebugSource` rather than receiving invented species semantics.

## Pause / Step

`AIDebugSimulationControl` detours `RainWorldGame.Update()` through Rain World's own RuntimeDetour version.

- debugger Pause remembers the previous game pause state;
- native pause menus retain ownership of their paused update loop;
- Step temporarily unpauses, runs one complete original update, then re-pauses;
- unloading restores the previous pause state and disposes the hook.

The target RuntimeDetour build used during implementation was checked for `Hook(MethodBase, Delegate)` support.

## Trigger Capture, anomalies and breakpoints

Trigger Capture records roughly 10 simulated seconds before the trigger and 5 simulated seconds after it.

Automatic anomaly types currently include:

- InvalidNumber
- VelocitySpike
- StateOscillation
- TargetThrashing
- PossibleStuck
- AttackSlotsViolation

Debugger-induced Pause is excluded from physical stuck/velocity anomaly detection.

Conditional breakpoints can filter by event category, event-name substring and entity. A matching breakpoint can pause the whole world without modifying the creature AI.

## JSON export

Completed trigger captures can be exported individually.

`Ctrl + Shift + F8` exports the complete retained Observatory session under:

`BepInEx/config/DryCycle.AIObservatory.Sessions/`

The session contains:

- format/version and simulation metadata;
- all retained trace identities, including entities no longer realized or visible;
- trace frames;
- complete historical Snapshot / Utility / Perception / Path data when enabled;
- raw events;
- completed captures and their frames/events;
- diagnostic settings metadata.

Non-finite float values are emitted as valid JSON `null`, never `NaN`/`Infinity` tokens.

## Settings and profiling

Persistent settings include language, UI/font scale, opacity, AutoOpen, history length, raw names, data age/source, entity IDs, overlay categories, full-history recording, capture/anomaly switches and breakpoint pause behavior.

The Settings window also exposes Observatory profiling categories for Capture, UI, Overlay, Timeline, Utility, Perception and AImap.

## Performance and non-interference rules

- F7 closed disables detailed trace watching.
- Trace/capture storage is bounded.
- Only selected/pinned entities receive detailed sampling.
- World entity discovery is throttled.
- Normal history sampling is ~10 Hz simulation time, not every render frame.
- Reflection metadata is cached where private species state must be inspected.
- Utility methods are never rerun by the debugger.
- Tracker best-guess computation is never triggered by the debugger.
- Flock access is read-only.
- Debug code must not write AI target, role, destination, path, relationship, utility or save-state data.
- Allowed external effects are limited to debugger controls: player-input isolation, whole-world Pause/Step, and explicit settings/layout/export files.

## Build/runtime wiring

`src/Directory.Build.props` contains the x64 build settings and the `ImGui.NET 1.91.6.1` package reference.

`src/DryCycle.csproj` contains the Rain World/BepInEx/Unity references, including the exact `BepInEx/core/MonoMod.RuntimeDetour.dll` with `Private=false`, and validates those paths before reference resolution.

The ImGui backend is self-contained for Rain World's Built-in Render Pipeline and uses a dedicated overlay Camera with command-buffer/dynamic-mesh rendering.

`scripts/Verify-AIObservatory.ps1` performs source-wiring checks, dependency/deployment checks and then prints the live acceptance checklist.

## Code completion status

The planned V1/V2/V3 managed-code feature set is implemented:

- bilingual inspection workspace;
- DockSpace + persistent layout;
- input isolation;
- complete timeline/history replay;
- read-only Utility / Tracker / Path diagnostics;
- world overlays and AImap;
- Compare / Candidate Inspector;
- whole-world Pause / Step;
- Trigger Capture / anomaly detection;
- conditional breakpoints;
- per-capture JSON export;
- whole-session JSON export;
- DesertBatfly, MossySpider, SpinebackLizard and generic adapters;
- profiling/settings/deployment verification support.

Therefore the **implementation task is code-complete**. This is not the same as runtime acceptance.

## Live validation still required

Before marking the system `fully validated`, run the Release build against the actual Rain World installation and verify at minimum:

1. `scripts/Verify-AIObservatory.ps1` passes source/dependency/deployment checks.
2. Rain World reaches gameplay with F7 closed and no Observatory exception.
3. Compact/Full DockSpace, Chinese/English, saved layout and non-ASCII paths work.
4. LIVE/INTERACT and ImGui keyboard/mouse capture do not leak gameplay input.
5. Pause freezes the whole simulation and Step advances exactly one simulation tick.
6. Timeline does not grow while merely paused and historical pages match old samples.
7. Alt+click and overlays work with normal and multiple/split RoomCamera setups.
8. Utility on vanilla creatures shows unavailable cache values as `—` and does not change AI behavior.
9. Tracker/Perception and Path pages operate without changing retained AI state.
10. AImap overlay does not throw during room/shortcut transitions.
11. DesertBatfly specialized roles, Sentinel/Opportunist state and AttackSlots overlays behave correctly.
12. MossySpider selects its dedicated roaming/migration adapter.
13. SpinebackLizard reports Green-baseline LizardAI ownership and exposes vanilla diagnostics.
14. Trigger Capture produces the expected ~10 s pre / 5 s post simulation window.
15. Artificial Pause does not trigger PossibleStuck.
16. Conditional breakpoint pauses/resumes the whole world safely.
17. Individual capture JSON preserves raw event detail/reason.
18. `Ctrl+Shift+F8` produces valid whole-session JSON containing traces/history/captures.
19. F7-closed overhead is effectively zero and F7-open overhead is acceptable in a populated stress-test room.

Until these live-game checks are performed, the correct status is:

**code complete / pending live Rain World validation**.
