# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.2.3**.

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

The project follows the current Rain World code-mod layout used by modern templates:

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

The project targets **.NET Framework 4.8** and compiles directly against the assemblies from the installed Rain World copy. It does not use generated API stubs.

Set the environment variable `RainWorldDir` to the folder containing `RainWorld.exe`, for example:

```text
C:\Program Files (x86)\Steam\steamapps\common\Rain World
```

Then build from the repository root:

```powershell
dotnet build DryCycle.sln -c Debug
```

or:

```powershell
dotnet build DryCycle.sln -c Release
```

The project references the real game assemblies from:

```text
%RainWorldDir%/BepInEx/core/*.dll
%RainWorldDir%/BepInEx/plugins/*.dll
%RainWorldDir%/BepInEx/utils/*.dll
%RainWorldDir%/RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll
%RainWorldDir%/RainWorld_Data/Managed/Unity*.dll
```

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

The HUD is attached through `RoomCamera.FireUpSinglePlayerHUD(Player)`, which provides the actual player directly and avoids relying on inferred/private HUD ownership fields.
