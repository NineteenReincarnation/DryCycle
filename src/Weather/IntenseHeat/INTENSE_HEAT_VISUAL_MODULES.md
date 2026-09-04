# IntenseHeat optional visual modules

The four additions below are deliberately isolated by stable search tags. Search the
entire repository for the exact tag before removing a module; every tagged `BEGIN` block
has a matching `END` block.

## IH-OPT-01_SURFACE_PLUMES

- Purpose: room-anchored convection columns emitted by dry, sunlit terrain.
- Current scope: `DryCycleIntenseHeatAtmosphere.shader` only.
- Removal: delete every block tagged `IH-OPT-01_SURFACE_PLUMES`.

## IH-OPT-02_DRY_AIR

- Purpose: dry bleaching, suspended mineral specks and sparse heat-lifted fibres.
- Current scope: `DryCycleIntenseHeatAtmosphere.shader` only.
- Removal: delete every block tagged `IH-OPT-02_DRY_AIR`.

## IH-OPT-03_SOLAR_MEMORY

- Purpose: direct-sun entry flash, accumulated ocular adaptation and shade afterimage.
- Current scope: `DryCycleIntenseHeatAtmosphere.shader`,
  `IntenseHeatRenderPipeline.cs`, and `IntenseHeatWeatherRuntime.cs`.
- Removal: delete every block tagged `IH-OPT-03_SOLAR_MEMORY`, then remove the three
  solar-memory arguments at the `IntenseHeatRenderFrame` construction site.

## IH-OPT-04_DEPTH_LAYERS

- Purpose: slow dry back-haze, mid-plane thermal shear and sharp foreground grain.
- Current scope: `DryCycleIntenseHeatAtmosphere.shader` only.
- Removal: delete every block tagged `IH-OPT-04_DEPTH_LAYERS`.
