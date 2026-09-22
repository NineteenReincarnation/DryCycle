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
- **Phase 7 — 100%**: final responsibility consolidation, legacy GPU/routed-overlay retirement and removal of obsolete Map performance compatibility paths.
- **Post-refactor cleanup — 100%**: Phase 0 runtime benchmark/probe instrumentation retired; overlays reuse retained spatial indexes; stable frames reuse the existing off-screen surface; live MapPage/texture/file source work runs only on the Unity main-thread pump and Draw consumes published snapshots; geometry visual caches invalidate from publication generations instead of Unity frame polling; interaction cooldown handoff is thread-safe and Unity-Time-free; fallback rooms use the visible Air tone rather than a near-black FrameBg; Map source/visual compatibility services use the single Bridge lifecycle instead of separate BepInEx plugin shells.
- **Phase 8.1 — 100%**: active view-only pan/zoom reprojects the committed guarded surface through verified AddImage UVs instead of calling Camera.Render; interaction settle performs one exact render, and guard exhaustion falls back visibly rather than stretching/clamping stale pixels.
- **Phase 8.2 — 100%**: viewport, room-drag and linking interaction cooldowns are tracked independently; pure pan/zoom/linking run with zero route-build budget while room drag retains a small incident-route budget, and source/geometry/shortcut work remains frozen for every active interaction class.
- **Phase 8.3 — 100%**: initial room-source capture is guard-band visible-first; rooms needed by the current retained surface are promoted ahead of the region-wide queue without duplicating caches or changing authoring order.

Retained V2 now owns the normal World Map presentation path. The remaining immediate-mode room/direct-link drawing is an explicit compatibility fallback when the verified RenderTexture -> RWImGUI bridge cannot present the V2 surface or when an interaction temporarily moves beyond the committed reprojection guard band.

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

## Phase 0 completion and retirement

Phase 0 was a development probe, not a permanent production subsystem. Its code-side purpose was to
establish the texture-contract boundary and baseline instrumentation while V2 was being built.

After Phase 7, the per-frame stopwatch sampling, toolbar benchmark UI and one-shot RenderTexture
probe are retired from the normal Map runtime. The production `WorldMapTextureBridge` now owns
texture-contract resolution and reports its own explicit failures. The standalone
`scripts/inspect_rwimgui_api.ps1` remains available for manual API inspection without adding work
to every Map frame.

Real-game measurements remain useful evidence but do not gate phase completion under this project's
code-side progress convention.

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

The region-wide initial queue is now **visible-first**. Before room-source capture runs,
`WorldMapRetainedV2Runtime` queries the same guarded world extent used by the off-screen surface and
promotes incomplete rooms in that extent ahead of the ordinary queue. Promotion changes queue order
only: it does not create another resource store, does not duplicate textures, and stale duplicate
queue entries are skipped without consuming the per-frame room budget. A visible room with a
committed thumbnail and an in-flight geometry worker is not repeatedly re-polled merely because it
remains on screen.

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

## Phase 7 implementation

### Final ownership table

| Responsibility | Owner after Phase 7 |
| --- | --- |
| Authoritative map data / undo / persistence | backend `EditorSession`, revisions and command queues |
| Detached room/connection presentation | `MapEditorPresentationHub` |
| Snapshot lookup/indexing | `WorldMapPresentationIndex` |
| Publish/prime throttling | `WorldMapUpdateThrottle` |
| Background interaction budget | `WorldMapBackgroundBudget` |
| Render-thread retained scene | `WorldMapSceneSynchronizer` + render scene |
| Main-thread scene mirror | `WorldMapSceneTransfer` + main scene |
| Pan/zoom handoff | `WorldMapViewTransformMailbox` |
| Room geometry/background builds | `WorldMapRoomResourceStore` + `WorldMapBuildScheduler` |
| Vanilla MapTex compatibility boundary | `WorldMapLegacyRoomSourceService` |
| Room GPU presentation | `WorldMapRetainedRoomRenderer` |
| Orthogonal route algorithm | `WorldMapOrthogonalRouter` |
| World-space route ownership | `WorldMapConnectionResourceStore` |
| Connection GPU presentation | `WorldMapRetainedConnectionRenderer` |
| Room/route hit testing | `WorldMapSpatialIndex` + `WorldMapRouteSpatialIndex` |
| Off-screen composition | `WorldMapRenderTextureSurface` + `WorldMapTextureBridge` |
| Dynamic labels/gizmos/selection/link preview | `WorldMapView` ImGui overlay |

No retired renderer remains an owner of normal-frame map presentation.

### Orthogonal router ownership

The coordinate-system-agnostic A*/orthogonal routing core has moved under `RetainedV2/Connections`
as `WorldMapOrthogonalRouter`. V2 no longer depends on the old screen-space
`WorldConnectionRouter` or on translation caches from the legacy connection overlay.

The router cache now contains world-space V2 routes only and is reset with the retained connection
resource lifetime.

### Legacy connection presentation retired

The old `WorldConnectionOverlay` BepInEx plugin and screen-space route renderer are removed.

When the V2 surface is available, retained route meshes own presentation and
`WorldMapRouteSpatialIndex` owns hover selection. If the surface is unavailable, the existing
simple direct-line ImGui fallback remains local to `WorldMapView`; it does not start a second
router/cache system.

### Legacy GPU World Map retired

The old standalone GPU stack is removed:

- `WorldMapGpuRendererPlugin / WorldMapGpuRuntime`
- `WorldMapGpuScene`
- `WorldMapGpuCache`
- `WorldMapGpuLifecyclePlugin`
- `WorldMapGpuPipeBatchPlugin`
- `WorldMapGpuRegionPreloadPlugin`

These components had become a parked duplicate after the correctness gate disabled their direct
screen camera. V2 owns off-screen rendering, resource lifetime, visibility indexing and background
geometry work, so keeping the old stack would only preserve duplicate state/caches.

`WorldMapPresentationCorrectness` now owns only ImGui canvas clipping and stroke correctness; it no
longer suppresses any standalone GPU camera because no legacy map camera exists.

### Old performance compatibility layer retired

The monolithic `WorldMapPerformancePlugin` is removed. Its still-valid responsibilities are split
into explicit services:

- `WorldMapPresentationIndex` — snapshot and one-frame visual lookup cache;
- `WorldMapUpdateThrottle` — detached snapshot/geometry/shortcut publication cadence;
- `WorldMapBackgroundBudget` — interaction-aware background work budget.

Legacy route translation caches, rounded-polyline caches and the low-zoom overview card are removed.
The overview card previously replaced room detail with a theme `FrameBg` rectangle; removing that
path also enforces the hard invariant that zoom cannot turn a room thumbnail black.

### Compatibility adapters deliberately retained

Two components remain because they still own real source compatibility responsibilities rather than
legacy presentation:

- `WorldMapLegacyRoomSourceService` resolves vanilla `RoomPanel/MapTex` texture descriptors for
  the V2 room resource store. GPU-cache/source-hash responsibilities have been removed from it.
- `WorldMapRasterReadbackFallback` supplies semantic raster data only when the atlas cannot be read
  through the normal CPU path.

`WorldMapLegacyVisualGuard` also remains while vanilla `MapPage` is kept alive as a data source;
its dependency is the frontend bridge, not the retired GPU renderer.

These compatibility components are services, not standalone BepInEx plugins. `BridgePlugin` is the
single lifecycle owner for exact shortcuts, raster fallback and legacy visual suppression. This
removes redundant startup guards/plugin ordering while keeping each service's responsibility
separate.

### Final pan/zoom invariant

After Phase 7, normal retained navigation has one state transition:

```text
pan / zoom
    -> WorldMapViewTransformMailbox
    -> active interaction: UV-reproject committed guarded surface
    -> interaction settle: one exact off-screen camera render
    -> present existing retained room/route resources
```

It does not invoke a legacy GPU cache, legacy routed overlay, screen-space A* rebuild, thumbnail
readback, room mesh rebuild or route mesh rebuild.

Base room thumbnails remain committed independently from zoom and are never replaced by a black LOD
placeholder.

### Final source-thread boundary

The final cleanup removes all live source advancement from `WorldMapView.Draw`.

Unity main thread now owns:

- `MapRoomGeometryPresentationHub.Prime`;
- shortcut texture scanning;
- exact shortcut room-file parsing;
- raster readback fallback;
- MapPage / RoomPanel inspection.

Those services publish detached room/shortcut snapshots. RWImGUI Draw performs lookup only; it no
longer calls `Texture.GetPixels`, `ReadPixels`, `File.ReadAllLines` or walks `MapPage.subNodes`.

The old `WorldMapHotState` DynamicMethod/reflection bridge is retired. Background budgeting reads
the explicitly published V2 zoom value, and its interaction cooldown uses atomic cross-thread state.

`WorldMapPresentationIndex` also publishes an immutable snapshot index atomically. Draw lookups no
longer share mutable room/connection dictionaries with main-thread reset/publication paths.

### Stable-frame surface reuse

The retained surface now renders only when its view, scene, room resources, route resources, layer
mask or link visibility revision changes. A fully stable Map frame reuses the existing RenderTexture
instead of calling `Camera.Render()` again.

### Interaction-time surface reprojection

When the resolved RWImGUI `AddImage` contract exposes UV coordinates, exact renders allocate a
**1.5x guarded surface** centered around the visible viewport. The retained room/route visibility
query uses that same guarded world extent, so the extra texture area contains real retained content
rather than empty padding.

During an active **view-only** pan/zoom, a changed view revision no longer schedules
`Camera.Render()`. Present converts the live viewport transform back into a UV rectangle inside the
last committed guarded surface and draws that sub-rectangle directly. Room meshes, route meshes,
MapTex capture and the Unity camera all stay untouched.

If the live viewport leaves the committed guard band, the surface reports presentation unavailable
for that frame and `WorldMapView` uses its visible immediate compatibility path. It does not clamp
the stale texture edge and does not replace rooms with black placeholders. Once the interaction
cooldown ends, the still-dirty view revision causes one exact guarded render for the settled view.

If the loaded ImGui API has no verified four-`Vector2` AddImage form, UV reprojection and guard-band
allocation stay disabled. The normal exact-surface path and immediate compatibility fallback remain
available instead of guessing an unsupported texture contract.

RenderTexture resize is a two-stage commit. A newly rendered candidate is not authoritative until
the RWImGUI texture bridge presents it successfully. If that presentation fails, the previous
last-known-good surface is restored and the resize is retried after a short cooldown, preventing
black frames and per-frame allocation thrash.

The surface handoff is also non-blocking across the Unity update thread and RWImGUI Present thread.
Only texture-handle/state exchange uses a short lock. `Camera.Render()` and ImGui `AddImage` are
never executed while holding the same lock, so navigation cannot stall because the two threads wait
on an entire render/present operation. RenderTexture release/destruction is deferred back to the
Unity main-thread pump; Present only reports accept/reject feedback.

Present holds a lightweight reader reference after it snapshots the current front/fallback handles.
The Unity pump never waits for that reader: it postpones destruction while a reader exists, and if
the front texture itself is being presented it skips that render attempt and retries on the next
dirty frame. This prevents use-after-release and render/present resource races without putting a
blocking lock back on navigation.

Reset/disable uses a separate presentation-lifecycle gate. That gate is never taken by normal
main-thread rendering; it exists only so shutdown can wait for an already-running Present call to
finish before unregistering the RWImGUI texture and releasing Unity resources.

A failed candidate binding does not mutate/destroy Unity resources from Present. Present draws the
retained last-known-good surface and posts rollback feedback; the next main-thread pump restores the
old front target, disposes the rejected target, refreshes it for the current view, and retries the
requested canvas size after cooldown.

### Interaction freeze

Viewport pan/zoom, room dragging and connection linking now publish separate atomic interaction
cooldowns. Non-urgent snapshot publication, geometry priming, shortcut texture scans,
exact-shortcut file parsing, source recovery and room-resource commits remain paused for **all**
active interaction classes. Retained last-known-good geometry/thumbnails stay authoritative until
the relevant cooldowns expire.

Route construction is stricter: pure viewport navigation and linking receive a **zero** route-build
budget, so queued A*/orthogonal work cannot steal an interaction frame. Room dragging remains the
one exception because incident routes must follow the authored room position; it receives the
existing small interactive route budget. Once all interaction cooldowns expire the normal idle route
budget resumes.

### Post-refactor overlay locality

The remaining ImGui-only authoring overlays now consume the retained room spatial index whenever V2
is active. Room-exit sockets, creature shortcut markers and exit-hover tests operate on visible or
near-pointer room candidates instead of scanning every room in the detached region snapshot.

This keeps the retained architecture's locality guarantee intact even for overlays that intentionally
remain immediate-mode.

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
