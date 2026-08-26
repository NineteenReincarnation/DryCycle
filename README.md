# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.2.4**.

## Current feature: hydration

- Default hydration: **4 maximum / 2 required for normal hibernation**.
- Hydration HUD is placed above the normal food meter.
- Normal hibernation is blocked when hydration is below 2.
- Normal hibernation consumes 2 hydration.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Hydration is stored in `SaveState.unrecognizedSaveStrings`; no external save file is used.
- Configured foods and edible creatures restore hydration.

Temperature mechanics are not implemented yet.

## Standard Rain World build setup

```text
DryCycle/
├─ mod/
│  ├─ modinfo.json
│  └─ newest/
│     └─ plugins/        # DryCycle.dll is built here
├─ lib/                  # optional extra stripped reference DLLs
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

The resulting plugin is written directly to:

```text
mod/newest/plugins/DryCycle.dll
```

For testing, copy the repository's `mod` folder to:

```text
Rain World/RainWorld_Data/StreamingAssets/mods/DryCycle
```

and enable **DryCycle** in Remix.

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

The HUD is attached through `RoomCamera.FireUpSinglePlayerHUD(Player)`, which provides the actual player directly.
