# DryCycle AI Observatory V3

Status: **code implementation complete / pending full Rain World runtime validation**.

The Observatory is a developer-only Dear ImGui runtime inspection system for Rain World creature AI. Its design rule is strict: **observe the real AI; never become a second AI system**.

It is intended to answer four questions:

1. What is this creature doing right now?
2. Which behavior layer currently owns control, and why?
3. What did the AI know / score / plan immediately before something went wrong?
4. Can the exact diagnostic window be frozen, captured, compared and exported without changing the creature's decision?

## Controls

- `F7` — open / close the Observatory.
- `F6` — switch Compact / Full DockSpace workspace.
- `Tab` — switch `LIVE` / `INTERACT` when ImGui is not already consuming keyboard input.
- `Alt + Left Mouse` — select the nearest realized creature under the cursor in the relevant RoomCamera.
- Entity Browser click — select entity A.
- `Shift + click` — select comparison entity B.
- Pin — keep an entity in the watched set while selecting another entity.
- Toolbar `Pause World` / `Resume World` — pause or resume the whole RainWorldGame simulation.
- Toolbar `Step 1 Tick` — while debugger-paused, execute exactly one complete RainWorldGame simulation update.
- `Ctrl + Shift + F8` — export the entire retained Observatory debug session to JSON.

The UI supports Chinese / English switching at runtime. Raw implementation names can be displayed when exact symbols matter.

## Input isolation

The Observatory detours `RWInput.PlayerInputLogic(int,int)` through the RuntimeDetour version already shipped by Rain World's BepInEx environment.

Gameplay input is neutralized when:

- `INTERACT` mode is active;
- ImGui requests keyboard capture;
- ImGui requests mouse capture.

Controller metadata is preserved, so leaving the UI does not require the player to reconnect or reselect the input device.

The debugger does not block gameplay input merely because F7 is open in LIVE mode; it blocks only when the UI is actually consuming the relevant input.

## Full DockSpace workspace

Full mode uses a real Dear ImGui DockSpace with persistent layout storage. The active V3 workspace contains:

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

The old V1/V2 workspace implementations were removed so obsolete code does not remain in the SDK build.

Layout persistence is stored through ImGui's in-memory ini API and written by managed UTF-8 file I/O. Native cimgui therefore never has to parse a potentially Chinese Windows path.

## Entity Browser and identity

`DebugEntityKey` uses creature template + EntityID spawner/number rather than a realized `Creature` object reference. This lets a watched identity survive realize/unrealize and normal room transitions as long as the AbstractCreature still exists.

The Browser separates creatures in currently visible RoomCamera rooms from the rest of the world. Multiple RoomCamera instances are supported.

## Timeline and complete historical state

Trace time is based on **RainWorldGame.clock (40 Hz simulation ticks)**, not Unity render frames or wall-clock time.

Consequences:

- normal gameplay records at one sample every 4 game ticks, approximately 10 Hz;
- debugger Pause does not create duplicate history samples while render frames continue;
- `Step 1 Tick` can produce a real single-tick diagnostic sample;
- capture pre/post windows use simulation time rather than time spent reading the UI.

Current fixed limits per retained trace:

- up to 1024 transition/events;
- up to 600 frame samples;
- up to 12 retained entity traces;
- user-visible history length configurable from 5 to 60 seconds.

Each full-history frame can retain:

- core trace state: room, position, velocity, local goal, mode/controller, target, role/suppression and diagnostic scalar values;
- complete `AIDebugSnapshot` sections and decision nodes;
- Utility rows;
- Tracker / perception rows;
- path/planner state.

Freeze View and the Timeline cursor therefore show the actual recorded diagnostic state for that tick instead of rebuilding old pages from the creature's current AI.

## Events

The event log records state/decision transitions instead of per-frame spam. Categories are:

- Decision
- State
- Perception
- Path
- Combat
- Social
- Warning

Events store `RawDetail` and `RawReason`. The UI renders known event names/reasons in the currently selected language, while JSON export always preserves raw diagnostic text.

DesertBatfly instrumentation includes important boundaries such as:

- RoleEntered
- RoleExit
- RoleEvaluation / RoleEvaluationBlocked
- RoleSustain
- SentinelAlarm
- OpportunistEarlyReturn

## Utility: read retained state only

The Observatory **never calls `AIModule.Utility()` to fill the UI**.

It also does not call `UtilityTracker.SmoothedUtility()`, because Rain World's implementation may call `module.Utility()` when no smoother exists.

For vanilla `UtilityComparer` AI:

- module name is read directly;
- weight and continuation-bonus configuration are read directly;
- current winning `UtilityTracker` is read directly;
- weighted/smoothed values are shown only when the real AI retained them in a non-null smoother;
- unavailable values are shown as `—` / exported as JSON `null`.

For DesertBatfly, Sentinel / Bully / Opportunist scores are values already calculated by the real role evaluator and can therefore be displayed directly.

## Perception / Tracker

When a real AI exposes `Tracker`, the Observatory can show its retained `CreatureRepresentation` data:

- visual contact;
- ticks since seen;
- estimated chance of finding;
- tracker priority;
- last-seen coordinate;
- best-position estimate;
- dynamic relationship type and intensity when available.

Optional world overlay markers can show tracked realized creatures in the selected entity's camera/room.

## Path / Control chain

Locomotion is deliberately separated into three layers:

- **INTENT** — AbstractCreatureAI destination / desired world coordinate;
- **PLANNER** — PathFinder, reachability, returnability and stranded state;
- **MOTOR** — species-specific local movement command / physical execution.

This makes it possible to distinguish:

- wrong destination;
- correct destination but bad path;
- correct path but bad movement command / physics.

## Compare

Entity A and B snapshots are flattened and displayed side-by-side. Raw implementation names can be enabled for exact field comparison.

## Candidate Inspector

Instrumented AI may register candidates without creating another selection pass. The Candidate window shows:

- candidate set;
- candidate name;
- valid / invalid;
- score;
- winner;
- rejection/selection reason.

DesertBatfly exposes role candidates and current motor goal through this path. Candidate data is read from values already calculated by its actual AI.

## World overlay

World overlays are drawn through Dear ImGui background draw lists; they do not create persistent Room objects.

Supported layers include:

- selected / pinned creature markers;
- physics BodyChunks and radii;
- velocity vectors;
- grasps;
- movement/local goal;
- generic path destination;
- Tracker perception marks;
- creature-specific AImap accessibility heatmap and allowed outgoing connections;
- instrumented candidate positions;
- DesertBatfly social/combat diagnostics.

### Multiple RoomCamera support

Camera selection is centralized in `AIDebugCameraUtil`.

- selected/pinned overlays use the camera showing their room;
- Alt+click chooses the RoomCamera under the mouse;
- split-screen world/screen conversion accounts for upper/lower camera placement;
- frozen history is drawn only through a camera showing the historical room.

## DesertBatfly specialized diagnostics

The dedicated DesertBatfly source and overlays expose:

- temperament, nerve, conformity, roost/vengeance/sand-spit affinities;
- thirst and creature cooldown;
- DesertAI mode / target / formal attack ownership;
- retreat, attacker memory, interest, pursuit, unseen and attack-slot state;
- stored role / expressed role / suppression;
- Sentinel, Bully and Opportunist scores;
- commitment / role cooldown / evaluation timing;
- Sentinel confidence and watch state;
- opportunity window, clear-sight safety ticks and Opportunist recovery;
- flock center, average velocity, active count, expressed-role count, panic and roost ratios;
- grab memory, grief, player/predator trauma and social bond;
- position, velocity, escape source, FlyAI localGoal, vanilla behavior and rain/lure state.

World overlays additionally include:

- Sentinel flock perimeter and visible threat;
- Opportunist return radius / recovery status;
- Bully/formal-attack `SLOT 1`, `SLOT 2`, `WAIT` labels and AttackSlots violation warning.

## Other DryCycle creature adapters

### MossySpider

MossySpider deliberately has a minimal non-predatory ecology. Its dedicated adapter therefore exposes what its real AI actually has rather than inventing threat/prey modules:

- Roaming / Waiting behavior;
- AbstractAI roam target;
- realized `MossySpiderPather` destination;
- cross-room migration control;
- movement direction;
- gait cycle;
- ground support;
- swim factor.

### SpinebackLizard

SpinebackLizard intentionally inherits Green Lizard behavior through DryCycle compatibility hooks. Its adapter explicitly labels that ownership while exposing live LizardAI / Utility / Tracker / Path state. This helps distinguish a DryCycle compatibility-hook problem from ordinary vanilla LizardAI behavior.

### Generic fallback

All other creatures fall back to `GenericCreatureDebugSource`, which exposes lifecycle, AbstractAI, RealAI, destination, pathfinder, modules and common realized state without claiming species-specific semantics that are not known.

## Pause / Step

`AIDebugSimulationControl` detours `RainWorldGame.Update()` using Rain World's own `MonoMod.RuntimeDetour` assembly.

- Pause preserves the game's previous pause state.
- Native pause menus keep ownership of their own paused update loop.
- Step temporarily unpauses simulation, runs one complete original `RainWorldGame.Update()`, then restores debugger pause.
- unloading the mod restores the previous pause state and disposes the hook.

The target Rain World/BepInEx RuntimeDetour version used during implementation was also checked for the required `Hook(MethodBase, Delegate)` constructor.

## Trigger Capture and anomaly recorder

Trigger Capture creates a diagnostic black-box window containing approximately:

- 10 seconds before the trigger;
- 5 seconds after the trigger;

measured in Rain World simulation time.

Automatic anomaly categories currently include:

- InvalidNumber
- VelocitySpike
- StateOscillation
- TargetThrashing
- PossibleStuck
- AttackSlotsViolation

Debugger-induced Pause does not count as physical stuck/velocity anomaly time.

Completed captures can be exported individually to raw JSON.

## Conditional Breakpoints

Breakpoint rules may filter by:

- event category;
- event-name substring;
- entity.

When configured, a matching event pauses the whole simulation through the same Observatory pause controller rather than mutating the creature AI.

## Whole Debug Session export

`Ctrl + Shift + F8` writes a complete retained session under:

`BepInEx/config/DryCycle.AIObservatory.Sessions/`

The session file contains:

- format/version and simulation metadata;
- all trace keys still retained by the fixed trace registry, including entities no longer realized/currently visible;
- all retained trace frames;
- complete per-frame historical Snapshot / Utility / Perception / Path data when full-history recording was enabled;
- raw trace events;
- completed Trigger Captures and their frames/events;
- current diagnostic settings metadata.

Non-finite float values are exported as JSON `null`, never invalid `NaN`/`Infinity` tokens.

## Settings

Persistent developer settings include:

- Chinese / English;
- UI scale;
- font scale;
- opacity;
- AutoOpen;
- history length;
- raw implementation names;
- data age/source;
- entity IDs;
- master and per-category overlay toggles;
- full-history recording;
- trigger capture;
- automatic anomaly detection;
- breakpoint-pauses-world.

The Settings page also shows per-category Observatory profiling values.

## Performance / non-interference rules

The Observatory must not become a second AI system.

- F7 closed disables trace watching.
- Trace and capture storage are bounded.
- Only selected/pinned entities receive detailed sampling.
- Entity discovery is throttled.
- Normal trace sampling is roughly 10 Hz simulation time, not every rendered frame.
- Cached reflection metadata is used where private species diagnostics are necessary.
- Flock inspection uses read-only accessors.
- Utility diagnostics never rerun module Utility methods.
- Debug code must not write AI target, role, path, relationship, utility or save-state data.
- Allowed external effects are limited to debugger controls: input isolation, whole-world Pause/Step, UI/settings/layout files and explicit diagnostic exports.

## ImGui / build runtime

`src/Directory.Build.props` references `ImGui.NET 1.91.6.1`, pins the build to x64 and references the `MonoMod.RuntimeDetour.dll` already shipped by Rain World's BepInEx installation with `Private=false`.

The Rain World ImGui backend is self-contained and uses a dedicated overlay Camera plus Built-in Render Pipeline command-buffer / dynamic-mesh rendering. It does not require Unity Editor, SRP, OS viewports or UImGui at runtime.

`src/DryCycle.csproj` validates the actual Rain World/BepInEx/Unity assembly locations before resolving the Release build.

`scripts/Verify-AIObservatory.ps1` is the intended pre-live validation entry point.

## Code completion status

The planned V1/V2/V3 managed-code feature set is now implemented:

- inspection workspace and bilingual UI;
- DockSpace and persistent layout;
- input isolation;
- complete historical snapshots and timeline replay;
- read-only Utility / Tracker / Path diagnostics;
- world overlays and AImap;
- Compare and Candidate Inspector;
- whole-world Pause / Step;
- Trigger Capture / anomaly detection;
- conditional Breakpoints;
- per-capture JSON export;
- whole-session JSON export;
- DesertBatfly, MossySpider, SpinebackLizard and generic adapters;
- profiler/settings/runtime deployment validation support.

This means **the implementation task is code-complete**. It does **not** mean live acceptance has been performed.

## Live validation still required

Before changing status from `implemented / pending live validation` to `fully validated`, run a Release build against the actual Rain World install and verify at minimum:

1. `scripts/Verify-AIObservatory.ps1` passes dependency and deployment checks.
2. NuGet restore resolves ImGui.NET and its net48-compatible dependencies.
3. `DryCycle.dll`, `ImGui.NET.dll`, required managed support assemblies and x64 `cimgui.dll` deploy correctly.
4. Rain World reaches gameplay with F7 closed and no Observatory-side exception.
5. F7 opens Compact mode; F6 enters the DockSpace workspace; F7 closes cleanly.
6. Chinese and English text render correctly, including Chinese glyphs.
7. Saved DockSpace layout survives restart and also works when the Windows path contains non-ASCII characters.
8. LIVE mode does not steal gameplay input when ImGui is not interacting.
9. INTERACT/text/mouse UI capture does not leak gameplay input into the player.
10. Pause World freezes gameplay without corrupting Rain World's native pause menu state.
11. Step 1 Tick advances exactly one gameplay simulation tick.
12. Alt+click selects the correct creature in normal camera and split-screen/multiple-camera conditions.
13. Entity identity survives realize/unrealize, shortcut, den and room transitions without stale-reference exceptions.
14. Pin, Freeze, Timeline and Compare remain valid across ordinary transitions.
15. Timeline stops accumulating duplicate samples while debugger-paused.
16. Utility diagnostics on a vanilla UtilityComparer creature do not alter its behavior and show `—` when no cached smoother exists.
17. Tracker/Perception and Path pages work on suitable vanilla creatures.
18. AImap overlay follows the selected creature's template and does not throw on transition/unloaded coordinates.
19. DesertBatfly shows role evaluation, suppression, Sentinel alarm, Opportunist recovery and AttackSlots overlay correctly.
20. MossySpider uses the dedicated migration/roaming adapter.
21. SpinebackLizard is identified as Green-baseline LizardAI and still exposes vanilla Utility/Tracker/Path data.
22. Trigger Capture contains approximately 10 seconds pre-trigger + 5 seconds post-trigger of simulation history.
23. Artificial debugger Pause does not trigger PossibleStuck.
24. A conditional breakpoint pauses the whole world on the expected event and resumes safely.
25. Individual capture JSON contains raw event detail/reason.
26. `Ctrl+Shift+F8` creates a valid whole-session JSON containing traces, historical state and captures.
27. F7-closed overhead is effectively zero; F7-open overhead remains acceptable in a populated stress-test room.

Until these live-game checks are completed, the correct status remains:

**code complete / pending live Rain World validation**.
