# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.10**.

## Versioning

DryCycle uses a simple sequential patch counter for development updates. Every update increments only the last number by 1, and the current development series stays on `0.0.x`.

Examples:

```text
0.0.1 -> 0.0.2
0.0.9 -> 0.0.10
0.0.10 -> 0.0.11
0.0.11 -> 0.0.12
```

The patch number does not roll over at 9 during normal development updates.

## Current feature: hydration

- Hydration is a global **0..5** resource.
- Normal hibernation requires at least **3 hydration** and consumes **3 hydration**.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- DryCycle does not draw a separate hydration bar.
- Hydration is rendered as a **cyan liquid/material fill inside the vanilla food pips**.
- Water is distributed from left to right across the first five food pips. Each pip has exactly three hydration states: **empty, half full, or full**.
- Example: hydration `2.5` renders as `full, full, half, empty, empty`; hydration `4.5` renders as `full, full, full, full, half`.
- Vanilla food graphics remain on top of the hydration material, including quarter-food states, so one circle can independently show food in quarters and water in halves as in the `Thirsty.png` design reference.
- The same embedded rendering is used in gameplay, on the sleep/starve screen, and on the character continue/select page.
- On a normal sleep screen the embedded water amount animates downward by the 3-point hibernation cost while following the vanilla food meter's own visibility/fade.
- While fully submerged, DryCycle keeps Rain World's **vanilla lower-left HUD reveal trigger** active. This makes the karma icon, embedded food/hydration meter, and rain-cycle timer fade in together using the game's normal HUD animation. After surfacing, a short hold lets that same vanilla cluster fade away naturally.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Configured foods and edible creatures restore hydration independently from food.
- Hydration is stored in `SaveState.unrecognizedSaveStrings`; no external save file is used.
- The five-unit format continues to use the `DRYCYCLETHIRSTV2` save key, so earlier five-unit hydration saves remain compatible.

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

`src/HUD/ThirstMeter.cs` hooks the vanilla `HUD.FoodMeter` and renders hydration material inside its existing circles. The water layer is calculated independently for each pip instead of applying one shared liquid height to the whole meter. Sleep and character-select pages configure the existing food meter with their saved hydration value rather than creating additional HUD circles.
