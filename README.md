# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.2.5**.

## Current feature: hydration

- Default/max hydration: **5 pips**, displayed as **3 | 2**.
- The hydration meter is placed above the normal food meter.
- The sleep/starve save screen also receives the hydration meter.
- Normal hibernation requires at least **2 hydration** and consumes **2 hydration**.
- On a normal sleep screen, the two consumed hydration pips animate out before the meter settles on the next-cycle value.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Configured foods and edible creatures restore hydration independently from food.
- Hydration is stored in `SaveState.unrecognizedSaveStrings`; no external save file is used.
- Version 0.2.5 starts a new five-pip hydration save key. Legacy four-pip 0.2.4 test data is discarded once so an existing test save starts at the new full value of 5.

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

The gameplay HUD is attached through `RoomCamera.FireUpSinglePlayerHUD(Player)`. The sleep/starve HUD is attached after `Menu.SleepAndDeathScreen.GetDataFromGame` initializes the vanilla sleep HUD.
