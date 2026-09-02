# DryCycle weather shader project

This folder is a minimal Unity project source tree for DryCycle's custom weather rendering assets.

## Why it exists

Rain World cannot compile a new Unity `.shader` or `.compute` file at runtime. The assets must be built into an AssetBundle by a Unity Editor, then loaded during `RainWorld.LoadResources` and wrapped in `FShader` where appropriate.

The runtime logs the exact player engine version as `Application.unityVersion` when the bundle is loaded or missing. Use a Unity Editor matching that version whenever possible. Unity documents AssetBundles as backward-compatible in many cases but **not forward-compatible**, so a bundle built by an editor newer than Rain World's player can fail even when the shader source itself is correct.

Every build writes a sibling file:

`mod/assets/drycycle/drycycleweather.version.txt`

The runtime compares that editor version with Rain World's player version and logs the result before loading the bundle.

## Assets

### Fog

- `Assets/DryCycle/Shaders/DryCycleFogComposite.shader`
  - anonymous per-camera GrabPass final world composite
  - exponential gameplay extinction / transmittance independent from visual texture
  - pseudo-depth reconstructed from Rain World's `_LevelTex`
  - **24-step** 2.5D volumetric integration
  - macro / mid / fine fog scales with domain warping
  - low-frequency fluid-density gradient bends high-frequency billows
  - spatial + slow temporal ray-march jitter to suppress visible layers
  - fixed Lantern / LanternMouse fog reveal with **24-step terrain occlusion**
  - coloured volumetric in-scattering around the permitted lights
  - player awareness separated from real light: it restores only weak local silhouettes
- `Assets/DryCycle/Compute/DryCycleFogFluid.compute`
  - whole-room semi-Lagrangian 2D fluid field
  - obstacle-aware velocity advection
  - divergence / Jacobi pressure projection
  - vorticity confinement
  - density advection, wall pooling and player wake injection
- `Assets/DryCycle/Compute/DryCycleFogNoise.compute`
  - generates one shared **96^3 ARGBHalf** volumetric noise texture
  - explicitly periodic value-noise FBM and Worley fields, so Repeat sampling has no hard 3D seam
  - Nubis-style coherent body + cellular erosion rather than simple texture multiplication

### HeatWave

HeatWave deliberately follows Rain World's own global/local split instead of treating a giant full-screen `HeatDistortion` as the weather.

- Rain World's built-in `LevelHeat` shader is applied to `RoomCamera.levelGraphic` while scheduled HeatWave is active.
  - this is the **primary** weather deformation
  - terrain receives the recognizable vertical heat-melt / palette response already proven by the base game
  - authored RoomSettings `HeatWave` remains compatible and is combined with the scheduled weather
- `Assets/DryCycle/Shaders/DryCycleHeatWaveAtmosphere.shader`
  - one SceneColor grab and one final resolve; no recursive Far/Mid/Near distortion stack
  - broad rising heat bands and meso-scale whole-air instability
  - stronger vertical compression/stretch than horizontal displacement, avoiding the "underwater" look
  - separate high-frequency shimmer spectrum for edge vibration
  - luminance-aware desert bleaching toward warm/bone white while protecting deep shadows
  - depth-weighted distant contrast loss and very small local softening in strong hot-air bands
  - dedicated debug views for heat bands, air motion, heat color and pseudo-depth
- Mapper-authored `HeatColumn` objects use Rain World's built-in local `HeatDistortion` shader.
  - HeatColumn is an auxiliary local hot-air volume, not the global HeatWave implementation
  - there is no HeatWave thermal-fluid compute solver, plume compute field or Thermal Burst state machine

## Build in the Unity Editor

Open `shader-src` as a Unity project, then use:

`DryCycle -> Build Weather AssetBundle (Windows x64)`

The resulting files are written directly to:

- `mod/assets/drycycle/drycycleweather`
- `mod/assets/drycycle/drycycleweather.version.txt`

## Command-line build

The C# project has an optional MSBuild bridge:

```powershell
dotnet build .\src\DryCycle.csproj -c Release `
  -p:BuildDryCycleAssets=true `
  -p:DryCycleUnityEditor="C:/Path/To/Unity.exe"
```

You can also set the `UNITY_EDITOR` environment variable instead of passing `DryCycleUnityEditor`.

A normal Release build automatically copies an existing bundle and its version sidecar into the active Ancient Site mod at `assets/drycycle/`. If no bundle exists, the build emits a warning. Fog uses its compatibility renderer; HeatWave still retains its built-in `LevelHeat` primary deformation while the custom atmosphere pass is unavailable.

## Runtime safety

Do not move AssetBundle loading into BepInEx `Awake`/`OnEnable`. `DryCycleShaderAssets` only installs the hook there; `AssetBundle.LoadFromFile` and `LoadAsset` execute after Rain World's own `LoadResources` pass.

Fog's custom composite can still run without compute shaders: if compute/3D texture support is unavailable, room fluid advection is disabled and the shader synthesizes pseudo-volume structure from Rain World's built-in `_NoiseTex` / `_NoiseTex2`. HeatWave does not depend on compute shaders.
