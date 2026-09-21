# World Map Retained Renderer V2

## Progress convention

Project progress is code-side only. A phase reaches **100%** when its implementation and repository
integration are complete. Real-game verification remains useful evidence, but it does not hold the
phase percentage below 100%.

## Current phase

- **Phase 0 — 100%**: runtime contract probe, RenderTexture probe and timing baseline instrumentation.
- **Phase 1 — 100%**: world-space retained scene, view transform and frontend dirty graph.
- **Phase 2 — 100%**: retained room thumbnail/geometry resources with last-known-good continuity.
- **Phase 3 — next**: world-space connection router and retained route resources.

The legacy renderer still presents the map while V2 responsibilities are migrated subsystem by
subsystem.

## Engineering boundaries

The V2 renderer follows the repository-level `AGENTS.md` rules:

- backend `EditorSession / Revision / Presentation Snapshot / Command` remains authoritative;
- RWImGui V2 owns presentation state only;
- Draw consumes detached snapshots and enqueues commands; it does not mutate authoring data;
- generated meshes, textures, routes and spatial indexes are derived runtime/cache data only;
- failures are logged with the original exception and cleanup result;
- no RWImGUI texture/native API is guessed when it can be verified from the installed assembly.

## Hard visual invariant

### Thumbnail continuity

A room's committed base thumbnail must remain visible across the complete supported zoom range.

Pan/zoom is never allowed to:

- replace a valid thumbnail with a black rectangle;
- release the committed thumbnail before a replacement is ready;
- rebuild a RenderTexture merely because zoom changed;
- show an uninitialised RenderTexture;
- discard the last-known-good room presentation while an asynchronous replacement is pending.

LOD may hide labels, pipes, node numbers and expensive semantic overlays. It may **not** remove the
base room thumbnail.

During Phase 0 the existing immediate renderer still owns the map. Its navigation placeholder is
forced to the visible room Air tone instead of a near-black FrameBg so the migration itself does not
introduce a black-flash regression. V2 will replace this placeholder with a retained thumbnail.

## Phase 0 implementation

### 1. RWImGUI texture-contract inventory

`WorldMapRetainedV2Phase0` reflects the exact loaded:

- `rain-world-imgui-api.dll`
- RWImGUI-provided modified `ImGui.NET.dll`

It records public candidates for:

- methods accepting `UnityEngine.Texture / Texture2D / RenderTexture`;
- native texture-ID image consumers;
- register/unregister/remove/release texture lifetime methods.

The probe never assumes that `Texture.GetNativeTexturePtr()` is an ImGui texture ID.

The standalone helper `scripts/inspect_rwimgui_api.ps1` prints the same contract information against
the user's installed Workshop build.

### 2. Real RenderTexture device probe

On the Unity main thread, after a live Map session exists, Phase 0 creates a small ARGB32
`RenderTexture`, explicitly clears it, verifies `IsCreated()`, obtains the native pointer for
diagnostics only, then releases and destroys it.

This proves the actual Rain World graphics device can host the future off-screen map surface without
yet binding that surface into ImGui.

### 3. Baseline timing

The current World Map canvas records CPU draw time into bounded rolling samples for:

- steady frames;
- pan frames;
- zoom frames;
- room-drag frames;
- combined navigation frames.

The toolbar exposes `p50 / p95 / max` timings. A concise baseline is also logged every 240
navigation samples.

These values are the regression baseline for V2. Later phases must demonstrate that pan/zoom cost
moves toward transform-only work rather than simply hiding detail.

## Phase 1 implementation

### World-space scene

`WorldMapScene` retains room and connection presentation nodes in map-world coordinates. It is a
frontend projection only; backend snapshots remain authoritative.

### View transform

`WorldMapViewTransform` exclusively owns canvas origin, viewport size, pan and zoom conversion.
Changing pan/zoom increments only the scene's view revision. It does not enter `WorldMapDirtySet`.

### Dirty graph

`WorldMapDirtySet` separates:

- room transform changes;
- room metadata changes;
- room port/topology changes;
- removed rooms;
- changed/removed connections;
- topology/full rebuild flags.

These are derived frontend invalidations and are not a second authoring Revision system.

### Stable-frame fast path

`WorldMapSceneSynchronizer` caches presentation-snapshot identity plus a frontend layout revision.
When both are unchanged, synchronization stops after updating the view transform. This makes pure
pan/zoom an O(1) scene-synchronization operation.

Interactive room dragging advances only the frontend layout revision and updates only the actively
dragged room when the backend snapshot itself is unchanged.

### Lifetime

The retained scene is released from the Map page retained-state reset path. Resetting V2 projection
never modifies the editor document, history, authoring revision or persistence state.

## Phase 0 completion criteria

Implementation is complete when the project compiles and the probes are present.

Runtime verification is complete after a real Rain World run confirms:

1. the exact RWImGUI texture candidate signatures in LogOutput;
2. the RenderTexture probe succeeds on the actual graphics device;
3. at least 240 pan/zoom samples have been collected;
4. p50/p95/max values are recorded before V2 replaces the renderer.

If the installed RWImGUI exposes no public Unity-texture adapter, Phase 1/2 must not invent one.
The next step is to inspect the exact installed API/source and introduce a verified adapter boundary.

## Phase 2 implementation

### Immutable room geometry

`RoomGeometryBlob` is the retained CPU representation of a room. It contains world-local vertices,
triangle indices, curve segments and shortcut-node positions. `RoomGeometryBuilder` consumes only
detached `EditorMapRoomVisualSnapshot` data; it does not touch Unity objects, so the build step can be
moved to worker jobs later without changing its contract.

Every blob contains a visible Air base quad. This is the non-black fallback when no valid MapTex
thumbnail has ever been committed.

### Last-known-good thumbnail resource

`RoomThumbnailResource` has separate pending and committed descriptors.

A replacement becomes visible only after its texture, UV and dimensions are valid. A missing or
failed replacement clears only the pending descriptor; the committed descriptor remains untouched.
The resource does not destroy the Futile/vanilla texture because it does not own that texture.

This implements the thumbnail continuity invariant independently of zoom.

### Main-thread source capture

`WorldMapRoomResourceStore` runs from `BridgePlugin.Update`, after the existing source-recovery
pump. Live `MapPage / RoomPanel / MapTex` access remains isolated behind
`WorldMapLegacyRoomSourceService`; Draw never scans the live DevInterface tree.

Room resources are updated from Phase 1 dirty IDs first, then through a bounded readiness audit for
late MapTex/geometry availability. Interaction frames use a smaller capture budget.

### Derived-resource lifetime

Removing a room drops only its V2 derived resource. Region/page reset clears the V2 store and retained
references without touching authoring data or destroying vanilla-owned textures.

## Legacy retirement policy

Legacy code does **not** have to wait until the entire V2 project is finished.

A legacy file/path may be retired as soon as all of the following are true:

1. its responsibility has a single V2 owner;
2. all consumers have migrated;
3. no runtime fallback still depends on it;
4. build verification passes;
5. the relevant runtime behavior has been tested;
6. deleting it cannot discard authoring data or hide a failure.

Conversely, legacy code must remain while it still owns an unported responsibility. Retirement is
therefore evidence-driven per subsystem rather than tied to one final all-or-nothing phase.
