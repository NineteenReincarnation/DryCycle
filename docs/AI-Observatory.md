# DryCycle AI Observatory

Status: **implemented in managed code / pending full Rain World runtime validation**.

The Observatory is a developer-only runtime AI inspection layer for DryCycle. It is designed to answer three questions without replacing the creature's AI:

1. **What is this creature doing right now?**
2. **Which layer currently owns its behavior, and why?**
3. **What changed immediately before the behavior became wrong?**

## Controls

- `F7` — open / close Observatory.
- `F6` — switch Compact / Full mode.
- `Alt + Left Mouse` — select the nearest realized creature under the cursor in the current RoomCamera.
- Entity Browser click — select entity A.
- `Shift + click` — select comparison entity B.
- Pin — keep an entity in the watched set while selecting another entity.

The panel can switch between Chinese and English at runtime. Raw field names can be displayed when exact implementation symbols matter.

## Full-mode pages

### Overview

Displays the current **Decision Stack**, **Control Owner**, Why / Why Not transitions and the species inspector. DesertBatfly has a dedicated adapter; creatures without a dedicated adapter fall back to the generic vanilla AI source.

### Timeline

A fixed-size ring buffer records selected/pinned entities. Each frame sample contains room, position, velocity, local goal, mode/controller, target, social role/suppression and diagnostic utility values. Clicking the strip moves the timeline cursor and enables Freeze View.

Current limits per trace:

- 512 events
- 1200 frame samples
- one frame sample every 4 Unity frames
- maximum 12 retained entity traces

These are fixed-size buffers; history does not grow for the lifetime of a session.

### Events

Displays state and decision transitions rather than a per-frame spam log. Categories include Decision, State, Perception, Path, Combat, Social and Warning. DesertBatfly social-role code writes precise events at important decision boundaries such as:

- RoleEntered
- RoleExit
- RoleEvaluation / RoleEvaluationBlocked
- RoleSustain
- SentinelAlarm
- OpportunistEarlyReturn

Events carry a detail string and, where available, an explicit reason.

### Utility

For vanilla AI using `UtilityComparer`, the page reads the live `UtilityTracker` objects:

- module raw `Utility()`
- non-weighted smoothed value
- weight
- weighted smoothed value used by `UtilityComparer` to choose the winner
- continuation bonus configuration
- current highest utility tracker

For DesertBatfly, Sentinel / Bully / Opportunist role scores are exposed as custom utilities together with the current soft-budget entry threshold and Why Not explanation.

### Perception / Tracker

For vanilla AI with a `Tracker`, each `CreatureRepresentation` can show:

- visual contact
- ticks since seen
- estimated chance of finding
- tracker priority
- last-seen coordinate
- `BestGuessForPosition()`
- dynamic relationship type and intensity when available

An optional world overlay marks tracked realized creatures in the current room.

### Path / Control Chain

Separates AI locomotion into three layers:

- **INTENT** — abstract destination / desired world coordinate
- **PLANNER** — PathFinder, reachability, ability to return from the destination and stranded state
- **MOTOR** — the species-specific local movement command / physical execution

For DesertBatfly, Motor includes `FlyAI.localGoal`, custom `DesertBatflyAI.Mode`, vanilla `FlyAI.behavior` and current velocity.

This separation is intentional: it makes it possible to distinguish a wrong destination from a wrong path and a correct path from a broken movement command.

### Compare

Entity A and B snapshots are flattened using raw implementation names. Differing values are displayed side-by-side; enabling Raw Names also keeps equal fields visible for exhaustive comparison.

## World overlay

The overlay is drawn through Dear ImGui's background draw list, not through persistent room objects. It can display:

- selected/pinned creature marker
- selected DesertBatfly target
- `localGoal`
- DesertBatfly flock center
- generic PathFinder destination when it is in the current room
- optional Tracker visibility marks / lines

Read-only overlay paths use `DesertSwarmRoom.TryGet`; opening the debugger must not create a DesertSwarmRoom colony merely because a room is inspected.

## DesertBatfly decision diagnostics

The dedicated source exposes, among other values:

- temperament, nerve, conformity, roost/vengeance/sand-spit affinities
- thirst and creature cooldown
- DesertAI mode / target / attack ownership
- retreat, attacker memory, interest, pursuit, unseen and attack-slot state
- stored role / expressed role / suppression
- Sentinel, Bully and Opportunist scores
- commitment / role cooldown / next evaluation
- Sentinel alert confidence
- opportunity window / Opportunist recovery state
- flock center, average velocity, active count, expressed role count, panic and roost ratios
- grab memory, grief, player/predator trauma and social bond
- position, velocity, escape source, `FlyAI.localGoal`, vanilla behavior and rain/lure state

The role Why Not path mirrors the real selection rules: soft-budget entry threshold, minimum dominance lead, role cooldown, formal attack ownership and existing-target restriction.

## Performance rules

The Observatory must not become a second AI system.

- When F7 is closed, trace watching is disabled.
- Trace storage is fixed-size.
- Only selected/pinned entities receive detailed tracing.
- Entity discovery is throttled rather than scanning the world for every ImGui draw call.
- Full snapshots are sampled at roughly 10 Hz instead of rebuilding every rendered frame.
- DesertBatfly private fields are read with cached `FieldInfo` only for the selected bat while the Observatory is visible.
- Flock inspection is read-only and must never call a state-creating accessor.
- Debug code must not write AI target, role, path, relationship, utility or save-state data.

## ImGui runtime

`src/Directory.Build.props` references `ImGui.NET 1.91.6.1` and pins the DryCycle managed build to x64. ImGui.NET's net40 build target is expected to copy the matching Windows x64 `cimgui.dll` beside `DryCycle.dll`.

The Rain World backend is intentionally self-contained. It uses a dedicated depth-clearing overlay camera and a Built-in Render Pipeline `CommandBuffer`/dynamic `Mesh` renderer. It does not require Unity Editor, SRP, OS viewports, or the UImGui Unity package at runtime.

## Validation still required

Managed implementation is not equivalent to live-game acceptance. Before marking the Observatory fully accepted, test a Release build against the actual Rain World install and verify:

1. NuGet restore resolves ImGui.NET and its net48-compatible dependencies.
2. `DryCycle.dll`, `ImGui.NET.dll`, dependency assemblies required by the package, and x64 `cimgui.dll` are deployed where Mono can resolve them.
3. Rain World reaches gameplay with F7 closed and no Observatory-side exception.
4. F7 opens Compact mode; F6 switches Full mode; F7 closes cleanly.
5. Chinese glyph atlas renders without missing glyphs on the target Windows installation.
6. UI mesh renders after RoomCamera without clearing the room image or being depth-occluded.
7. Alt+click selection matches the creature under the Rain World cursor.
8. Entity identity survives realize/unrealize, shortcut and den transitions.
9. Pin, Freeze, Timeline and Compare survive ordinary room transitions without stale-reference exceptions.
10. Utility and Tracker pages work on at least one vanilla creature that uses those modules.
11. DesertBatfly shows RoleEvaluation, suppression, SentinelAlarm and Opportunist recovery transitions during controlled tests.
12. F7 closed has no measurable gameplay regression; F7 open remains within the agreed developer-debug overhead target on a populated test room.

Until those runtime checks are completed, the correct status is **implemented / pending live Rain World validation**, not "fully validated".
