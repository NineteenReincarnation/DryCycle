# Actual Unity / RWImGUI rendering checks

Build the solution to deploy the current DryCycle assemblies, then run from the repository root:

```powershell
.\scripts\Test-MapRendering.ps1
```

The test uses the installed Unity 2020.3 editor, its actual D3D11 device and the installed
RWImGUI 1.12 native backend. No render, texture, camera or ImGui input calls are mocked.
It checks retained-map isolation from an all-layer gameplay camera, resizing, failure
cleanup, raster orientation, clipping and texture lifetime after eviction. It also
renders the real B5 region without selecting rooms and drives the production connection
editor through mouse press/drag/release and Delete input.

The generated Unity project, logs and PNGs stay in
`../Build/DryCycle/validation/world-map-render-isolation`. The B5 source files are read
with explicit fixture precedence; this isolated editor test does not replace an in-game
check of active-mod precedence, UI scale, player state or the game's Present hook.
