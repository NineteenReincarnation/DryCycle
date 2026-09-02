# HeatWave Optical Lattice Direction

Status: **approved design direction for the next HeatWave visual iteration**.

This note exists so the direction is not lost between implementation passes.

## Goal

HeatWave should read as **extreme dry hot air / desert mirage** even in a static frame. The core visual must be spatially coherent air refraction, not a generic fullscreen wobble, fog veil, or stronger single UV offset.

## Core change

Introduce a room/world-anchored **Optical Lattice**: a continuous 2D deformation field that represents hot air as deformable optical space.

Instead of treating Thermal Sheets as just another additive UV offset, use them to deform the lattice itself. The resulting space should support local compression, expansion, shear, and bending.

Conceptual pipeline:

```text
Flow / Heat Bodies / SurfaceField
            |
            v
      Optical Lattice
            |
            +-- Thermal Sheets drive local compression/stretch
            +-- Ground Mirage drives dense near-surface deformation
            +-- Lateral meander breaks mechanical vertical motion
            +-- Relaxation returns calm regions toward rest
            |
            v
    Continuous refractive coordinates
            |
            +-- Fine Normal / Mirage texture adds high-frequency shimmer
            |
            v
        Scene Grab resolve
```

## Lattice scales

Preferred multi-scale structure:

### Macro lattice
- Roughly 24x14 control points per screen-equivalent area.
- Slow, broad deformation.
- Creates large heat bodies, long bends, and low-frequency spatial tilt.

### Thermal lattice
- Roughly 48x28 or 64x36 control points per screen-equivalent area.
- Main HeatWave optical layer.
- Driven by Thermal Sheets and Ground Mirage.
- Produces visible local compression/stretch through poles, chains, architecture silhouettes, etc.

### Fine shader detail
- Keep existing Detail Normal / Mirage texture path.
- Small amplitude, faster shimmer (~subpixel to about 1 px typical).
- Should never become the main deformation source.

Exact grid resolution can be adjusted after profiling; the concept matters more than fixed numbers.

## Forces / deformation drivers

The optical lattice should be driven visually, not by a full fluid simulation.

### Updraft
- Slow upward bias.
- Gives hot air coherent rising motion.

### Thermal Sheets
- Long, thin, irregular horizontal/near-horizontal layers.
- Upper and lower sheet boundaries should bend in opposite directions.
- These boundaries should create visible local pinching and expansion rather than whole-object translation.

### Ground heat
- Strongest immediately above hot terrain from `HeatWaveSurfaceField`.
- Dense, short vertical wavelengths near floors, ledges, slopes, and exposed solid terrain.
- Rapidly falls off with height.
- Suppressed under water.

### Lateral meander
- Small horizontal variation.
- Prevents perfectly vertical or sinusoidal movement.

### Relaxation
- Calm regions slowly return toward an undeformed lattice.
- Avoids permanent drift and makes deformation feel like passing air bodies.

## Optical measurements from the lattice

Use the local deformation Jacobian / finite differences to derive optical state directly from space deformation:

- **compression**: locally reduced area/spacing;
- **expansion**: locally increased area/spacing;
- **shear**: directional skew;
- **bend/gradient**: refraction direction.

Use these quantities for secondary optical response instead of guessing focus from unrelated noise.

Suggested response:

```text
compression -> slight luminance gain, contour concentration, limited silhouette overlap
expansion   -> slight luminance loss, slight directional softening
shear       -> directional refraction / contour skew
```

Keep luminance modulation subtle; do not turn it into water caustics.

## World anchoring

The lattice must remain **room/world anchored**, not screen anchored.

Camera movement must reveal the same hot-air structures at their room positions. Heat should feel present in the level, not stuck to the monitor.

## Thermal Sheet appearance

Thermal Sheets should have several states continuously blended:

```text
calm air
weak heat body
strong thermal sheet
very hot optical lens
dense ground boiling layer
```

Do not make distortion uniform across the whole room.

Target visual features:
- long horizontal / gently sloped hot layers;
- irregular edges and break-up;
- slow broad evolution;
- faster small detail riding on top;
- local pinching/stretching of thin poles, chains, architecture edges;
- static screenshots should already show strong heat refraction.

## Depth / scene-layer direction

Long optical path should read stronger than near foreground where feasible.

Future implementation should investigate using Rain World's level/depth semantics or another stable scene mask so approximately:

```text
far background : strongest
midground      : strong
near foreground: reduced
very near      : light shimmer only
```

This must not require per-room hand tuning.

## Color rules remain unchanged

HeatWave color direction:
- strong dry yellow / sand-yellow atmosphere;
- preserve deep shadows and black silhouettes;
- midtones take most of the warm shift;
- highlights stay hot yellow, not white;
- no white bleaching / fog veil;
- no room-specific color special cases.

## Explicit non-goals

Do **not** solve the next iteration by:
- simply raising distortion max from 14.5 px to a much larger number;
- adding generic scrolling noise;
- making the entire screen sway like water;
- adding gray/white fog;
- making every pixel distort equally;
- reintroducing the removed thermal/plume compute-fluid simulation;
- coupling the effect to player dehydration, dizziness, or physiology for now;
- hard-coding individual rooms such as SU_A53.

## Relationship to current HeatWave

Keep the useful existing layers:
- Rain World `LevelHeat` for terrain-level melt;
- runtime FlowField;
- Base/Detail refractive normals;
- Mirage texture;
- SurfaceField / Ground Mirage;
- Thermal Sheets;
- Heat Band color response;
- directional softening;
- local HeatColumn using built-in HeatDistortion.

The Optical Lattice should become the **low/mid-frequency spatial deformation backbone** underneath those layers rather than replacing all existing work.

## Acceptance criteria

A successful implementation should make a room look extremely hot without needing motion to prove it:

1. Thin straight silhouettes visibly pinch, stretch, skew, and reconnect across hot layers.
2. Ground-adjacent air is visibly denser and more unstable than high air.
3. Large hot-air bodies remain coherent instead of becoming random noise.
4. Fine shimmer exists but does not dominate.
5. The image reads as dry desert heat, not water, gelatin, fog, or a cheap post-process filter.
6. Color remains yellow/hot without bleaching toward white.
7. No per-room special casing.
