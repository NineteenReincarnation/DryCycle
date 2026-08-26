# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.2.7**.

## Current feature: hydration

- Hydration is a global **0..5** resource.
- Normal hibernation requires at least **3 hydration** and consumes **3 hydration**.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- DryCycle no longer draws a separate hydration bar.
- Hydration is rendered as a **cyan liquid/material fill inside every vanilla food pip**.
- The liquid height is the global hydration percentage: full hydration fills the circle, half hydration fills its lower half, and intermediate values are rendered continuously.
- Vanilla food graphics remain on top of the hydration material, including quarter-food states, so the same food circle simultaneously shows both food and water as in the `Thirsty.png` design reference.
- The same embedded rendering is used in gameplay, on the sleep/starve screen, and on the character continue/select page.
- On a normal sleep screen the embedded water level animates downward by the 3-point hibernation cost while following the vanilla food meter's own visibility/fade.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Configured foods and edible creatures restore hydration independently from food.
- Hydration is stored in `SaveState.unrecognizedSaveStrings`; no external save file is used.
- The five-unit format continues to use the `DRYCYCLETHIRSTV2` save key, so 0.2.5/0.2.6 hydration values remain compatible with 0.2.7.

Temperature mechanics are not implemented yet.

## Standard Rain World build setup

```text
DryCycle/
├─ mod/
│  └─ modinfo.json
├─ lib/
└─ src/
   ├─ DryCycle.csproj
   ├─ Plugin.cs
   ├─ HUD/
   └─ Thirst/
```

The project targets **.NET Framework 4.8** and compiles against the assemblies from the installed Rain World copy. It does not use generated Rain World API stubs.

The project accepts `RainWorldDir` as an environment variable or MSBuild property. On the current development machine it also automatically detects:

```text
D:/Application/Steam/steamapps/common/Rain World
```

Recommended explicit build command:

```powershell
dotnet build .\DryCycle.sln -c Release -p:RainWorldDir="D:/Application/Steam/steamapps/common/Rain World"
```

DryCycle explicitly references only the assemblies it needs:

```text
BepInEx/core/BepInEx.dll
BepInEx/utils/PUBLIC-Assembly-CSharp.dll
BepInEx/plugins/HOOKS-Assembly-CSharp.dll
RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll
RainWorld_Data/Managed/UnityEngine.dll
RainWorld_Data/Managed/UnityEngine.CoreModule.dll
```

This avoids accidentally compiling against unrelated installed mod DLLs.

The resulting plugin is written directly to the current test mod folder:

```text
D:/Application/Steam/steamapps/common/Rain World/RainWorld_Data/StreamingAssets/mods/Ancient Site/newest/plugins/DryCycle.dll
```

## Source layout

```text
src/Plugin.cs
src/Thirst/ThirstHooks.cs
src/Thirst/ThirstStore.cs
src/Thirst/ThirstState.cs
src/Thirst/ThirstConstants.cs
src/Thirst/FoodWaterTable.cs
src/HUD/ThirstMeter.cs
```

`src/HUD/ThirstMeter.cs` now hooks the vanilla `HUD.FoodMeter` and renders hydration material between the food meter's background and food graphics. Sleep and character-select pages configure the existing food meter with their saved hydration value rather than creating any additional HUD circles.
