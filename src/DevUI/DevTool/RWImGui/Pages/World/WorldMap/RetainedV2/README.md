# World Map Retained Renderer V2

## Progress convention

Project progress is code-side only. A phase reaches **100%** when its implementation and repository
integration are complete. Real-game verification remains useful evidence, but it does not hold the
phase percentage below 100%.

## Current phase

- **Phase 0 — 100%**: runtime contract probe, RenderTexture probe and timing baseline instrumentation.
- **Phase 1 — 100%**: world-space retained scene, view transform and frontend dirty graph.
- **Phase 2 — 100%**: retained room thumbnail/geometry resources with last-known-good continuity.
- **Phase 3 — 100%**: world-space connection routing and retained route resources.
- **Phase 4 — 100%**: off-screen RenderTexture surface, runtime texture bridge and retained room GPU presentation.
- **Phase 5 — 100%**: spatial index, background room-geometry scheduler and visible/local GPU upload scheduling.
- **Phase 6 — 100%**: retained connection GPU presentation, world-space route interaction, render/main scene handoff and legacy connection hot-path retirement.
- **Phase 7 — next**: final legacy retirement, responsibility audit and removal of obsolete Map performance compatibility paths.

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

## Phase 3 implementation

### World-space routing

`WorldMapWorldSpaceRouter` resolves room bounds and exit positions from retained Phase 1/2 data.
Its inputs contain no canvas origin, pan or zoom. The orthogonal routing core is reused temporarily
because the algorithm itself is coordinate-system agnostic; V2 IDs are isolated with a `v2:`
prefix. Once the legacy screen-space caller retires, the shared algorithm core can move physically
under RetainedV2 without changing V2 route semantics.

### Connection dependencies

`ConnectionDependencyIndex` maps room IDs to connection IDs. Moving a room or changing its ports
invalidates only incident routes. Pure pan/zoom does not enqueue any route work.

### Retained routes

`ConnectionRouteResource` stores the committed world-space polyline, endpoint directions, direction
semantics and a route revision. `WorldMapConnectionResourceStore` batches only dirty route IDs,
keeps deterministic parallel-lane offsets for multiple links between the same room pair, and removes
stale route resources when topology changes.

### Geometry-driven invalidation

When Phase 2 publishes a new room geometry blob, the room resource store emits the affected room ID.
Phase 3 consumes that ID and invalidates only routes incident to that room, covering changed room
dimensions/port geometry without scanning or rebuilding the entire route set.

## Phase 4 implementation

### Off-screen surface

`WorldMapRenderTextureSurface` owns an orthographic Unity camera whose target is an off-screen
`RenderTexture`. The camera never renders directly to the game screen.

RenderTexture allocation depends only on canvas pixel size. Pan/zoom changes only camera transform,
so zoom never recreates the surface.

When the canvas size changes, a new target is created, explicitly cleared and rendered before it
replaces the presented target. The previous target remains alive until the new target has actually
been accepted by the ImGui texture bridge. A failed replacement therefore leaves the last-known-good
surface intact.

### Runtime texture bridge

`WorldMapTextureBridge` resolves the exact loaded RWImGUI contract through reflection. It requires:

1. a public method that accepts `Texture` / `RenderTexture` and returns a native ImGui-compatible
   texture ID;
2. an `ImDrawListPtr.AddImage`-compatible method consuming that ID.

It never treats `GetNativeTexturePtr()` as an ImGui texture ID by assumption. If the loaded RWImGUI
build exposes no verified adapter, V2 presentation remains unavailable and the existing immediate
renderer continues to draw the map.

### Retained room GPU objects

`WorldMapRetainedRoomRenderer` owns persistent Unity room objects and meshes.

- room movement updates only the room GameObject transform;
- pan/zoom rebuilds no room mesh;
- committed thumbnails render as UV quads from the vanilla/Futile texture source;
- custom semantic terrain is a separate retained color overlay;
- rooms with no committed thumbnail render their retained neutral/semantic geometry instead of a
  black placeholder;
- layer filtering toggles retained room objects without rebuilding geometry.

### Migration presentation

`WorldMapView` attempts to present the V2 off-screen room surface inside the existing clipped ImGui
canvas. If presentation succeeds, legacy immediate room geometry is skipped while labels, ports,
interaction and the still-unmigrated connection presentation remain active above the retained room
surface.

If the runtime texture bridge is unavailable, the existing room renderer remains the explicit
fallback; no silent partial surface is treated as success.

## Phase 5 implementation

### Incremental world-space spatial index

`WorldMapSpatialIndex` maintains room AABBs in a fixed world-space spatial hash. Room transforms,
layer changes, removals and geometry-dimension changes update only affected entries. Pan/zoom never
rebuilds the index.

Viewport and point queries visit only intersecting cells. The retained room renderer therefore
receives a visible-room list rather than scanning every room on each frame.

The V2 point query is also used by `WorldMapView` for room hover before the legacy GPU/linear
fallback path.

### Background room geometry build scheduler

`WorldMapBuildScheduler` moves `RoomGeometryBuilder` work off the Unity main thread.

Jobs consume detached `EditorMapRoomVisualSnapshot` data only. They never touch:

- Unity textures;
- Mesh/Material/GameObject;
- MapPage/RoomPanel;
- authoring/persistence state.

Results return through a concurrent completion queue. Each request carries a scheduler generation
and visual-source stamp; stale results from an old region/session or superseded room revision are
discarded before commit. Original worker exceptions are logged.

### Main-thread capture and bounded commit

`WorldMapRoomResourceStore` still captures live MapTex descriptors on the main thread through the
legacy compatibility boundary, but expensive pure geometry construction is scheduled to workers.
The main thread commits only a bounded number of completed immutable blobs per frame.

### Visible/local GPU synchronization

`WorldMapRetainedRoomRenderer` no longer scans every retained scene room. The spatial index supplies
only currently visible room IDs. Meshes rebuild only when a room geometry/thumbnail generation
changes; room movement updates only its transform. Rooms leaving the viewport are disabled without
destroying their retained meshes.

If a visible room resource has not completed its first background build, the renderer creates a
neutral visible fallback mesh, preserving the no-black/no-missing-room invariant.

### Stable frontend position projection

`WorldMapView.SynchronizePositions` now keys off the detached room-array identity. Stable pan/zoom
frames no longer rescan every room merely to rewrite unchanged local positions.

## Phase 6 implementation

### Retained connection GPU presentation

`WorldMapRetainedConnectionRenderer` turns Phase 3 world-space route resources into persistent
Unity meshes on the same off-screen surface as room thumbnails.

Each route owns a retained mesh keyed by route revision. Route changes rebuild only that route;
pan/zoom only changes the camera. Direction arrows are baked into the retained route mesh and
bidirectional links retain two opposite in-line arrows. Ambiguous routes retain dashed presentation.

Crossing bridges live in a separate retained mesh keyed by the connection-resource revision. They
are rebuilt only after route data changes, never because the viewport moved.

### Route spatial interaction

`WorldMapRouteSpatialIndex` stores retained route bounds/segments in world-space cells.

Connection hover now converts the mouse position to map-world coordinates and queries only nearby
route cells. Once the complete V2 route set is committed, legacy screen-space route hit-testing and
`WorldConnectionOverlay` drawing are removed from the active frame path.

Selection, delete semantics and endpoint authoring commands remain the existing backend command
paths. V2 replaces presentation/hit-testing only.

### Render/main-thread scene ownership

RWImGUI Draw and Unity Update no longer share one mutable `WorldMapScene`.

The render thread owns its scene projection. On actual scene dirtiness it captures an immutable
`WorldMapSceneDelta` and publishes that delta through `WorldMapSceneTransfer`. Unity main thread
applies deltas to its own retained scene mirror before touching textures, meshes, spatial indexes or
route resources.

Pan/zoom uses `WorldMapViewTransformMailbox`, a tiny lock-free tuple handoff. It does not copy room
or connection dictionaries and cannot dirty retained geometry.

### Explicit migration fallback

V2 connection presentation becomes authoritative only after every connection in the main-thread
scene has a committed world-space route. Until then, the existing connection presentation remains
visible. This avoids partial maps during incremental route construction without treating an
incomplete V2 route set as success.

Once V2 reports a complete route set, the legacy connection renderer/router is no longer called for
that frame. This is the first runtime retirement of that legacy hot path; physical file removal is
reserved for Phase 7 after all remaining consumers are audited.

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
