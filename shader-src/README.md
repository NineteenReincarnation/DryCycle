# DryCycle weather shader project

This folder is a minimal Unity project source tree for DryCycle's custom weather rendering assets.

## Why it exists

Rain World cannot compile a new Unity `.shader` or `.compute` file at runtime. The assets must be built into an AssetBundle by a Unity Editor, then loaded during `RainWorld.LoadResources` and wrapped in `FShader` where appropriate.

The runtime logs the exact player engine version as `Application.unityVersion` when the bundle is loaded or missing. Use a Unity Editor matching that version whenever possible; AssetBundles are platform/editor-version sensitive.

## Assets

- `Assets/DryCycle/Shaders/DryCycleFogComposite.shader`
  - final GrabPass world composite
  - exponential gameplay extinction / transmittance
  - pseudo-depth from Rain World's `_LevelTex`
  - 12-step volumetric density integration
  - domain-warped 3D noise with Rain World 2D-noise fallback
  - fixed Lantern / LanternMouse fog reveal with terrain occlusion
  - fog in-scattering around the permitted lights
- `Assets/DryCycle/Compute/DryCycleFogFluid.compute`
  - whole-room semi-Lagrangian 2D fluid field
  - obstacle-aware velocity advection
  - divergence / Jacobi pressure projection
  - vorticity confinement
  - density advection, wall pooling and player wake injection
- `Assets/DryCycle/Compute/DryCycleFogNoise.compute`
  - generates the shared 64^3 coherent/cellular fog detail volume once at runtime

## Build in the Unity Editor

Open `shader-src` as a Unity project, then use:

`DryCycle -> Build Weather AssetBundle (Windows x64)`

The resulting runtime bundle is written directly to:

`mod/assets/drycycle/drycycleweather`

## Command-line build

The C# project has an optional MSBuild bridge:

```powershell
dotnet build .\src\DryCycle.csproj -c Release `
  -p:BuildDryCycleAssets=true `
  -p:DryCycleUnityEditor="C:/Path/To/Unity.exe"
```

You can also set the `UNITY_EDITOR` environment variable instead of passing `DryCycleUnityEditor`.

A normal Release build automatically copies an existing `mod/assets/drycycle/drycycleweather` into the active Ancient Site mod at `assets/drycycle/drycycleweather`. If no bundle exists, the build emits a warning and the game uses DryCycle's compatibility fog renderer instead of crashing.

## Runtime safety

Do not move AssetBundle loading into BepInEx `Awake`/`OnEnable`. `DryCycleShaderAssets` only installs the hook there; `AssetBundle.LoadFromFile` and `LoadAsset` execute after Rain World's own `LoadResources` pass.
