# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.20**.

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
0.0.16 -> 0.0.17
0.0.17 -> 0.0.18
0.0.18 -> 0.0.19
0.0.19 -> 0.0.20
0.0.20 -> 0.0.21
```

The patch number does not roll over at 9 during normal development updates.

## Current feature: hydration

- Hydration is a **0..5** resource for each player.
- A cyan hydration divider is drawn between the first and second hydration pips. It uses the same cyan as a full water pip and follows the vanilla FoodMeter fade/position.
- The number of hydration pips to the left of the cyan divider defines normal hibernation water requirement and cost. The divider is currently after pip 1, so normal hibernation requires at least **1 hydration** and consumes **1 hydration**.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- DryCycle does not draw a separate hydration bar.
- Hydration is rendered as a **cyan liquid/material fill inside the vanilla food pips**.
- Water is distributed from left to right across the first five food pips. At rest, each pip has exactly three hydration states: **empty, half full, or full**.
- Example: hydration `2.5` renders as `full, full, half, empty, empty`; hydration `4.5` renders as `full, full, full, full, half`.
- Vanilla food graphics remain on top of the hydration material, including quarter-food states, so one circle can independently show food in quarters and water in halves as in the `Thirsty.png` design reference.
- **Hydration visibility is independent from the vanilla food-fill animation.** Eating can fade or rebuild the food-fill sprite without making the cyan water disappear.
- **Hydration size follows the vanilla outer food-circle animation.** The inset scales proportionally with the original outer-circle radius, so the cyan material expands and settles with the same pop animation instead of keeping a fixed-pixel border during scaling.
- **Hydration remains available during shortcut/pipe room transitions.** Both the HUD renderer and hydration-state initialization fall back to `player.abstractCreature.world.game` while the realized player's `room` is temporarily `null`.
- Drinking is explicitly disabled while `player.inShortcut` is true, preventing stale underwater/submersion values from continuing hydration gain or wave animation inside a transition pipe.
- Custom hydration meshes are removed when the vanilla `FoodMeter.MeterCircle` clears its sprites, preventing leftover cyan meshes when HUDs or character-select pages are destroyed and recreated.
- While hydration is being replenished, the currently filling pip temporarily uses a **continuous rising liquid level with a moving wave surface**. When the refill animation settles, the display returns to the normal empty/half/full states.
- **Hydration gained from food and meat uses the same rising-water animation as underwater drinking.** The HUD records the pre-eat and post-eat hydration values, starts visually from the old amount, then raises the liquid through each affected pip with the same follow speed and moving wave instead of snapping to the result.
- **Hydrating food can still be eaten when the normal stomach is full.** DryCycle temporarily opens one internal food slot only while vanilla eating code runs, suppresses the normal `AddFood`/`AddQuarterFood` result, and restores the original full food count immediately afterward. The item is consumed normally and only its hydration effect is kept.
- When full-stomach hydrating food is being consumed, a **temporary food pip at 50% normal size** appears to the right of the vanilla food meter. It uses the vanilla food-circle graphics, pops/fills as an overflow-eating indicator, then fades away without changing real food capacity.
- Non-hydrating food at a full stomach still uses Rain World's normal refusal feedback, but the refusal is debounced to one warning burst per continuous attempt instead of repeatedly resetting the shake forever while the pickup button is held.
- Food hydration gains still force the lower-left HUD to appear even when the vanilla food count itself does not change.
- A failed hibernation attempt caused by insufficient hydration also reveals the lower-left HUD so the red rejection flash is visible.
- Vanilla `ObjectEaten` interactions whose nourishment result is `-1` do not grant hydration, matching Rain World's own early-return/invalid-food behavior.
- **Watcher spinning-top/warp saves preserve hydration.** Rain World v1.11.8 calls `SaveState.SessionEnded(survived: true)` for these special transitions while deliberately skipping the normal food sleep drain; DryCycle mirrors that behavior, does not charge the normal 1-point hydration hibernation cost, and suppresses the sleep-screen hydration drain animation for that special save path.
- **Jolly story co-op uses independent hydration for every human player.** P1, P2, P3 and P4 each have their own `0..5` water value. Drinking or eating hydrating food changes only the player who performed that action.
- The vanilla Jolly camera changes `hud.owner` when focus moves between players. DryCycle follows that owner change and immediately swaps the embedded hydration display to the focused player's own water value instead of interpolating between players.
- NPC slugpups remain excluded from DryCycle hydration and keep their vanilla pup food meters unchanged.
- In Jolly co-op, a normal shelter close is rejected if any living player who is ready for normal hibernation has less than 1 hydration. Only the insufficient player loses their ready state and receives the rejection feedback. A successful normal hibernation subtracts 1 hydration from every player's own value independently.
- Jolly starvation sleep remains on Rain World's vanilla starvation path. On a successful starvation cycle, each player's saved hydration is set to 0 independently.
- Co-op hydration is saved per player number inside `SaveState.unrecognizedSaveStrings`. Player 0 keeps the existing `DRYCYCLETHIRSTV2<svB>` entry for solo-save compatibility; additional Jolly players use `DRYCYCLETHIRSTV2P1`, `DRYCYCLETHIRSTV2P2`, and so on.
- The same embedded rendering is used in gameplay, on the sleep/starve screen, and on the character continue/select page. The vanilla sleep/character-select screens only expose one FoodMeter, so those screens continue to display player 0's saved hydration; in gameplay the focused Jolly player is shown.
- On a normal sleep screen the embedded water amount animates downward by the 1-point hibernation cost while following the vanilla food meter's own visibility/fade.
- The vanilla lower-left HUD cluster is forced open while the currently focused player is actively drinking. The karma icon, food/hydration meter, and rain-cycle timer therefore fade in together using Rain World's normal HUD animation, then fade away naturally after drinking stops.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Configured foods and edible creatures restore hydration independently from food.
- Hydration is stored in `SaveState.unrecognizedSaveStrings`; no external save file is used.
- Existing solo five-unit saves using `DRYCYCLETHIRSTV2` remain compatible. Co-op player slots that do not yet have a saved hydration entry start full at 5.

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
src/HUD/HydrationDivider.cs
```

`src/HUD/ThirstMeter.cs` hooks the vanilla `HUD.FoodMeter` and renders hydration material inside its existing circles. Static hydration remains quantized to half-pip states, positive hydration changes animate upward with a moving wave before settling, water visibility remains independent from the vanilla food-fill sprite, radius scaling follows the vanilla outer-circle pop, and custom meshes are removed with the vanilla HUD lifecycle. Food and meat hydration gains explicitly queue their pre-gain value so the reused FoodMeter cannot miss the rise animation even when it was hidden or a Jolly camera focus change happened at the same time. Full-stomach hydration-only eating uses a temporary 50%-scale overflow food pip to the right of the meter while keeping the real vanilla food count unchanged. `src/HUD/HydrationDivider.cs` draws the cyan hibernation divider between the first and second hydration pips using the same dimensions as Rain World's normal non-pup food divider; `HydrationSleepDividerAfterPip` is the single source of truth for both divider placement and normal hydration sleep requirement/cost. Gameplay hydration lookup remains valid while the realized player is temporarily between rooms in a shortcut. In Jolly story co-op, hydration is keyed by `PlayerState.playerNumber`; camera focus changes immediately swap the reused FoodMeter to the focused player's own hydration state. Sleep and character-select pages configure the existing food meter with player 0's saved hydration value rather than creating additional HUD circles.
