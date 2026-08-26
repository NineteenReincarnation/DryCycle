# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.15**.

## Versioning

DryCycle uses a simple sequential patch counter for development updates. Every update increments only the last number by 1, and the current development series stays on `0.0.x`.

Examples:

```text
0.0.1 -> 0.0.2
0.0.9 -> 0.0.10
0.0.10 -> 0.0.11
0.0.11 -> 0.0.12
0.0.12 -> 0.0.13
0.0.13 -> 0.0.14
0.0.14 -> 0.0.15
0.0.15 -> 0.0.16
```

The patch number does not roll over at 9 during normal development updates.

## Current feature: hydration

- Hydration is a global **0..5** resource.
- Normal hibernation requires at least **3 hydration** and consumes **3 hydration**.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- DryCycle does not draw a separate hydration bar.
- Hydration is rendered as a **cyan liquid/material fill inside the vanilla food pips**.
- Water is distributed from left to right across the first five food pips. At rest, each pip has exactly three hydration states: **empty, half full, or full**.
- Example: hydration `2.5` renders as `full, full, half, empty, empty`; hydration `4.5` renders as `full, full, full, full, half`.
- Vanilla food graphics remain on top of the hydration material, including quarter-food states, so one circle can independently show food in quarters and water in halves as in the `Thirsty.png` design reference.
- **Hydration visibility is independent from the vanilla food-fill animation.** Eating can fade or rebuild the food-fill sprite without making the cyan water disappear.
- **Hydration size follows the vanilla outer food-circle animation.** The inset now scales proportionally with the original outer-circle radius, so the cyan material expands and settles with the same pop animation instead of keeping a fixed-pixel border during scaling.
- **Hydration remains available during shortcut/pipe room transitions.** Both the HUD renderer and hydration-state initialization fall back to `player.abstractCreature.world.game` while the realized player's `room` is temporarily `null`.
- Drinking is explicitly disabled while `player.inShortcut` is true, preventing stale underwater/submersion values from continuing hydration gain or wave animation inside a transition pipe.
- Custom hydration meshes are removed when the vanilla `FoodMeter.MeterCircle` clears its sprites, preventing leftover cyan meshes when HUDs or character-select pages are destroyed and recreated.
- While hydration is being replenished, the currently filling pip temporarily uses a **continuous rising liquid level with a moving wave surface**. When the refill animation settles, the display returns to the normal empty/half/full states.
- Large hydration gains from food also use the rising-water animation instead of appearing instantly.
- One-shot hydration gains reveal the vanilla lower-left HUD even if the vanilla food count did not change, such as eating hydrating food while the stomach is already full.
- A failed hibernation attempt caused by insufficient hydration also reveals the lower-left HUD so the red rejection flash is visible.
- Vanilla `ObjectEaten` interactions whose nourishment result is `-1` no longer grant hydration, matching Rain World's own early-return/invalid-food behavior.
- **Watcher spinning-top/warp saves preserve hydration.** Rain World v1.11.8 calls `SaveState.SessionEnded(survived: true)` for these special transitions while deliberately skipping the normal food sleep drain; DryCycle now mirrors that behavior, does not charge the 3-point hydration hibernation cost, and suppresses the sleep-screen hydration drain animation for that special save path.
- The same embedded rendering is used in gameplay, on the sleep/starve screen, and on the character continue/select page.
- On a normal sleep screen the embedded water amount animates downward by the 3-point hibernation cost while following the vanilla food meter's own visibility/fade.
- The vanilla lower-left HUD cluster is forced open while the player is actively drinking. The karma icon, food/hydration meter, and rain-cycle timer therefore fade in together using Rain World's normal HUD animation, then fade away naturally after drinking stops.
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

`src/HUD/ThirstMeter.cs` hooks the vanilla `HUD.FoodMeter` and renders hydration material inside its existing circles. Static hydration remains quantized to half-pip states, positive hydration changes animate upward with a moving wave before settling, water visibility remains independent from the vanilla food-fill sprite, radius scaling follows the vanilla outer-circle pop, and custom meshes are removed with the vanilla HUD lifecycle. Gameplay hydration lookup remains valid while the realized player is temporarily between rooms in a shortcut. Sleep and character-select pages configure the existing food meter with their saved hydration value rather than creating additional HUD circles.
