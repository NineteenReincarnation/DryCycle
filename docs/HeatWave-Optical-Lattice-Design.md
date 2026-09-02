# HeatWave Optical Lattice Direction

Status: **implemented as the current HeatWave optical backbone**.

This note remains the implementation contract for future HeatWave tuning.

## Goal

HeatWave should read as **extreme dry hot air / desert mirage** even in a static frame. The core visual is spatially coherent air refraction, not a generic fullscreen wobble, fog veil, or stronger single UV offset.

## Implemented core

HeatWave now uses a room/world-anchored **Optical Lattice**: a continuous 2D deformation field representing hot air as deformable optical space.

Thermal Sheets and Ground Mirage no longer act only as additive UV offsets. They also drive the lattice itself, producing local compression, expansion, shear, bending and large coherent refractive movement.

Current pipeline:

```text
Flow / Heat Bodies / SurfaceField
            |
            v
      Optical Lattice
            |
            +-- Thermal Sheets drive local compression/stretch
            +-- Ground Mirage drives dense near-surface deformation
            +-- Updraft moves deformation upward through room space
            +-- Lateral meander breaks mechanical vertical motion
            +-- Relaxation returns calm regions toward rest
            |
            v
    Continuous refractive coordinates
            |
            +-- Jacobian -> compression / expansion / shear / bend
            +-- Thermal Sheet boundary refraction
            +-- Fine Normal / Mirage high-frequency shimmer
            |
            v
        Scene Grab resolve
            |
            +-- silhouette compression / layering
            +-- directional softening
            +-- optical focus
            +-- dry-hot yellow grading
```

## Lattice scales

### Macro lattice

Implemented at approximately **64 px control spacing**.

On a 1366x768 screen-equivalent area this is roughly 21x12 control cells, close to the intended 24x14 class.

Purpose:
- slow broad deformation;
- large heat bodies;
- long bends;
- low-frequency spatial tilt;
- coherent motion instead of noise wobble.

### Thermal lattice

Implemented at approximately **28 px control spacing**.

On a 1366x768 screen-equivalent area this is roughly 49x27 control cells, within the intended 48x28 class.

Purpose:
- main visible HeatWave spatial deformation;
- driven strongly by Thermal Sheets and Ground Mirage;
- local pinching and stretching through poles, chains, architecture and creature silhouettes;
- faster evolution than the macro lattice without becoming fine noise.

### Fine shader detail

Existing Base/Detail refractive normals and Mirage texture remain active on top of the lattice.

Fine detail amplitude was deliberately kept below the lattice/sheet deformation so it reads as shimmer rather than the primary shape of the weather.

## Implemented forces / deformation drivers

### Updraft

The lattice node phase moves upward through room coordinates over time. Large heat structures therefore evolve as rising air rather than as a texture attached to the screen.

### Thermal Sheets

Long, thin, irregular horizontal/near-horizontal layers remain present at multiple scales.

The shader samples sheet values above and below the current point and derives:
- first vertical derivative -> opposing upper/lower boundary refraction;
- second vertical derivative -> local pinching / expansion.

These values now also drive the thermal lattice, so a Thermal Sheet deforms the surrounding optical space rather than merely adding another independent offset.

### Ground heat

`HeatWaveSurfaceField` remains the geometry-aware ground guide.

Ground proximity:
- strengthens the thermal lattice;
- increases short-scale boiling motion;
- increases dense Ground Mirage layers;
- increases optical compression and directional blur;
- falls off away from terrain;
- remains suppressed under water through the dry-air mask.

### Lateral meander

Each lattice scale contains smaller horizontal variation driven by room-anchored node phase, flow direction and spatial hash. This prevents perfectly vertical sine-wave movement.

### Relaxation

Lattice node amplitude is gated by coherent heat-body / sheet drive. Calm regions collapse back toward the undeformed lattice rather than accumulating permanent drift.

## Optical measurements from the lattice

The implementation derives a local deformation Jacobian from the interpolated lattice node derivatives.

Current measurements:
- **compression** from determinant below 1;
- **expansion** from determinant above 1;
- **shear** from cross-axis derivatives;
- **bend** from total local deformation gradient.

These values directly drive secondary optics:

```text
compression -> local contour concentration + slight focus gain
expansion   -> stronger directional softening + slight focus loss
shear       -> directional skew / refractive blur direction
bend        -> additional strong-air blur contribution
```

No water-caustic texture is used.

## World anchoring

All lattice cell coordinates are derived from `roomPx`, which itself is derived from Rain World's room/camera transform.

The lattice is therefore **room/world anchored**, not screen anchored. Camera movement reveals the same hot-air structures at their room positions.

## Thermal Sheet appearance

The current shader continuously blends:

```text
calm air
weak heat body
strong thermal sheet
very hot optical lens
dense ground boiling layer
```

Thermal Sheets remain irregular and multi-scale rather than uniform bands.

Target visual features retained by implementation:
- long horizontal / gently sloped hot layers;
- irregular edges and break-up;
- slow broad evolution;
- faster small detail riding on top;
- local pinching/stretching of thin poles, chains and architecture edges;
- static screenshots should already show visible heat refraction.

## Player / creature inclusion

The HeatWave atmosphere remains in Rain World's `GrabShaders` scene stage. This stage is after the main room sprite layers and therefore the scene grab contains player/creature sprites as well as terrain and props.

Directional softening is now explicitly **scene-edge driven inside coherent hot-air layers**. This means the slugcat and other high-contrast creature silhouettes can be refracted and blurred by HeatWave instead of remaining unnaturally razor-sharp while the room behind them bends.

This is environmental optics only. It is **not** tied to player dehydration, dizziness, health, or physiology.

The player is not given a permanent character-only blur halo: creature softening still requires a HeatWave layer / lattice deformation to overlap the silhouette.

## Depth / scene-layer direction

A true stable per-pixel scene-depth mask is still not hard-coded because Rain World's mixed Futile sprite layers and level texture semantics need to remain compatible across base game/DLC rooms and arbitrary modded sprites.

The current implementation instead gains depth-like visual separation from:
- geometry-aware Ground Mirage;
- coherent thermal layers;
- scene-edge response;
- large lattice deformation behind and through silhouettes;
- player/creature inclusion in the same scene grab.

A future genuine optical-depth mask may be added only if it can be derived reliably without room-specific tuning or incorrectly excluding creatures/props.

## Color rules

HeatWave color direction remains:
- strong dry yellow / sand-yellow atmosphere;
- preserve deep shadows and black silhouettes;
- midtones take most of the warm shift;
- highlights stay hot yellow, not white;
- no white bleaching / fog veil;
- no room-specific color special cases.

## Explicit non-goals

Do **not** solve future tuning by:
- simply raising the distortion cap indefinitely;
- adding generic scrolling noise;
- making the entire screen sway like water;
- adding gray/white fog;
- making every pixel distort equally;
- reintroducing the removed thermal/plume compute-fluid simulation;
- coupling the effect to player dehydration, dizziness, or physiology;
- hard-coding individual rooms such as SU_A53.

## Relationship to the rest of HeatWave

The implemented Optical Lattice is the **low/mid-frequency spatial deformation backbone** underneath the existing useful layers:
- Rain World `LevelHeat` for terrain-level melt;
- runtime FlowField;
- Base/Detail refractive normals;
- Mirage texture;
- SurfaceField / Ground Mirage;
- Thermal Sheets;
- Heat Band color response;
- directional softening;
- local HeatColumn using built-in HeatDistortion.

It does not replace those layers; it gives them one coherent deformable optical space.

## Debugging

`Ctrl+Shift+H` now includes an `OPTICAL LATTICE` view.

The lattice debug view exposes approximately:

```text
R = compression
G = expansion
B = shear
```

This allows testing whether weak final visuals are caused by the deformation field itself or by the final scene resolve.

## Acceptance criteria

A successful implementation should make a room look extremely hot without needing motion to prove it:

1. Thin straight silhouettes visibly pinch, stretch, skew, and reconnect across hot layers.
2. Ground-adjacent air is visibly denser and more unstable than high air.
3. Large hot-air bodies remain coherent instead of becoming random noise.
4. Fine shimmer exists but does not dominate.
5. The image reads as dry desert heat, not water, gelatin, fog, or a cheap post-process filter.
6. Color remains yellow/hot without bleaching toward white.
7. Player/creature silhouettes participate in environmental heat refraction and softening.
8. No per-room special casing.
