# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.24**.

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
0.0.21 -> 0.0.22
0.0.22 -> 0.0.23
0.0.23 -> 0.0.24
0.0.24 -> 0.0.25
```

The patch number does not roll over at 9 during normal development updates.

## SlugBase integration

DryCycle has a hard dependency on **SlugBase** (`slime-cubed.slugbase`) and registers two custom SlugBase `PlayerFeature`s. Custom SlugBase slugcats can override both values directly in their character JSON.

```json
{
  "id": "MySlugcat",
  "name": "My Slugcat",
  "description": "Example",
  "features": {
    "WaterLossRate": 5.0,
    "WaterPips": 2
  }
}
```

The feature names are case-sensitive and are exactly:

- `WaterLossRate`: hydration loss rate in **WV per second**. Default: **5 WV/s**.
- `WaterPips`: whole hydration pips required for normal hibernation. DryCycle also uses this value as the normal hibernation water cost and as the cyan divider position.

Built-in `WaterPips` defaults:

| Slugcat | WaterPips |
| --- | ---: |
| Monk (`Yellow`) | 1 |
| Survivor (`White`) | 2 |
| Hunter (`Red`) | 3 |
| Gourmand | 4 |
| Artificer | 3 |
| Rivulet | 3 |
| Saint | 2 |
| Inv | 6 |

Characters not listed above, including custom SlugBase slugcats that omit `WaterPips`, currently fall back to **2**. All characters that omit `WaterLossRate` fall back to **5 WV/s**. Both features are read on demand, so SlugBase JSON reloads are reflected without storing a second cached character configuration in DryCycle.

> Current hydration capacity is still the established **5 pips / 2000 WV**. Therefore `Inv = 6` is kept exactly as configured and currently cannot satisfy a normal hibernation requirement without a future capacity rule change. DryCycle does not silently clamp `WaterPips` down to 5.

## Current feature: hydration

- Hydration is a **0..5** resource for each player.
- DryCycle exposes hydration internally as **Water Value (WV)** while retaining the existing pip-based save format for compatibility.
- **1 full hydration pip = 400 WV**, **1 half pip = 200 WV**, and the five-pip maximum is **2000 WV**.
- `WaterLossRate` is applied independently to every living story player each gameplay tick. The default `5 WV/s` equals `0.0125` hydration pip per second.
- At **200 WV or lower** (half of one hydration pip or less), the player receives Rain World's normal **Malnourished / starving weakness** gameplay state. DryCycle applies this through the temporary `malnourishedByCreature` channel so recovering above 200 WV does not clear a genuine vanilla starvation-cycle malnourished state.
- The cyan hydration divider position is character-specific and follows that character's `WaterPips` value in gameplay, on the sleep screen, and on the character continue/select page.
- The hydration divider copies Rain World's own survival-divider spacing: every food pip on its right is shifted by an additional **half `CircleDistance`** (15 px on the normal FoodMeter), so the cyan line has the same clear gap on both sides as the vanilla white food divider. The vanilla white divider is offset by the same added hydration gap when it lies to the right, preserving its original spacing too.
- The number of hydration pips to the left of the cyan divider defines normal hibernation water requirement and cost for that character.
- **Normal sleep hydration is consumed strictly from right to left.** The cyan divider only defines how much water sleep costs; it never marks a specific pip to remove first. The sleep HUD reconstructs the pre-sleep amount and continuously lowers the rightmost occupied hydration pip until the configured cost is spent, then moves left only if more water still needs to be consumed.
- Starvation hibernation consumes all remaining hydration and leaves food at 0.
- DryCycle does not draw a separate hydration bar.
- Hydration is rendered as a **cyan liquid/material fill inside the vanilla food pips**.
- Water is distributed from left to right across the first five food pips. At rest, each pip has exactly three hydration states: **empty, half full, or full**.
- Example: hydration `2.5` renders as `full, full, half, empty, empty`; hydration `4.5` renders as `full, full, full, full, half`.
- Vanilla food graphics remain on top of the hydration material, including quarter-food states, so one circle can independently show food in quarters and water in halves as in the `Thirsty.png` design reference.
- **Hydration visibility is independent from the vanilla food-fill animation.** Eating can fade or rebuild the food-fill sprite without making the cyan water disappear.
- **Hydration size follows the vanilla outer food-circle animation.** The inset scales proportionally with the original outer-circle radius, so the cyan material expands and settles with the same pop animation instead of keeping a fixed-pixel border during scaling.
- **Hydration remains available during shortcut/pipe room transitions.** Both the HUD renderer and hydration-state initialization fall back to `player.abstractCreature.world.game` while the realized player's `room` is temporarily `null`.
- Drinking is explicitly disabled while `player.inShortcut` is true, preventing stale underwater/submersion values from continuing hydration gain or wave animation inside a transition pipe. Passive `WaterLossRate` still continues while the living player is travelling through a shortcut.
- Custom hydration meshes are removed when the vanilla `FoodMeter.MeterCircle` clears its sprites, preventing leftover cyan meshes when HUDs or character-select pages are destroyed and recreated.
- While hydration is being replenished, the currently filling pip temporarily uses a **continuous rising liquid level with a moving wave surface**. When the refill animation settles, the display returns to the normal empty/half/full states.
- **Hydration gained from food and meat uses the same rising-water animation as underwater drinking.** The HUD records the pre-eat and post-eat hydration values, starts visually from the old amount, then raises the liquid through each affected pip with the same follow speed and moving wave instead of snapping to the result.
- **Hydrating food can still be eaten when the normal stomach is full.** DryCycle temporarily opens one internal food slot only while vanilla eating code runs, suppresses the normal `AddFood`/`AddQuarterFood` result, and restores the original full food count immediately afterward. The item is consumed normally and only its hydration effect is kept.
- When full-stomach hydrating food is being consumed, a **temporary food pip at 50% normal size** appears to the right of the vanilla food meter. It uses the vanilla food-circle graphics, pops/fills as an overflow-eating indicator, then fades away without changing real food capacity.
- Non-hydrating food at a full stomach still uses Rain World's normal refusal feedback, but the refusal is debounced to one warning burst per continuous attempt instead of repeatedly resetting the shake forever while the pickup button is held.
- Food hydration gains still force the lower-left HUD to appear even when the vanilla food count itself does not change.
- A failed hibernation attempt caused by insufficient hydration also reveals the lower-left HUD so the red rejection flash is visible.
- Vanilla `ObjectEaten` interactions whose nourishment result is `-1` do not grant hydration, matching Rain World's own early-return/invalid-food behavior.
- **Watcher spinning-top/warp saves preserve hydration.** Rain World v1.11.8 calls `SaveState.SessionEnded(survived: true)` for these special transitions while deliberately skipping the normal food sleep drain; DryCycle mirrors that behavior and suppresses the hydration hibernation charge for that special save path.
- **Jolly story co-op uses independent hydration for every human player.** P1, P2, P3 and P4 each have their own water value, their own `WaterLossRate`, and their own `WaterPips` requirement based on the slugcat they are actually playing.
- The vanilla Jolly camera changes `hud.owner` when focus moves between players. DryCycle follows that owner change and immediately swaps the embedded hydration display and cyan divider to the focused player's own hydration state/configuration instead of interpolating between players.
- NPC slugpups remain excluded from DryCycle hydration and keep their vanilla pup food meters unchanged.
- In Jolly co-op, a normal shelter close is rejected if any living player who is ready for normal hibernation has less water than their own `WaterPips` setting. Only the insufficient player loses their ready state and receives the rejection feedback. A successful normal hibernation subtracts each player's own `WaterPips` cost independently.
- Jolly starvation sleep remains on Rain World's vanilla starvation path. On a successful starvation cycle, each player's saved hydration is set to 0 independently.
- Co-op hydration is saved per player number inside `SaveState.unrecognizedSaveStrings`. Player 0 keeps the existing `DRYCYCLETHIRSTV2<svB>` entry for solo-save compatibility; additional Jolly players use `DRYCYCLETHIRSTV2P1`, `DRYCYCLETHIRSTV2P2`, and so on.
- The same embedded rendering is used in gameplay, on the sleep/starve screen, and on the character continue/select page. The vanilla sleep/character-select screens only expose one FoodMeter, so those screens continue to display player 0's saved hydration; in gameplay the focused Jolly player is shown.
- The vanilla lower-left HUD cluster is forced open while the currently focused player is actively drinking. The karma icon, food/hydration meter, and rain-cycle timer therefore fade in together using Rain World's normal HUD animation, then fade away naturally after drinking stops.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**, equivalent to **200 WV per second** before passive water loss is applied.
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

DryCycle now also compiles against `SlugBase.dll`. `src/DryCycle.csproj` tries common local-mod and Steam Workshop SlugBase locations automatically. If SlugBase is installed elsewhere, pass its DLL explicitly:

```powershell
dotnet build .\DryCycle.sln -c Release `
  -p:RainWorldDir="D:/Application/Steam/steamapps/common/Rain World" `
  -p:SlugBaseDll="D:/path/to/SlugBase.dll"
```

The project accepts `RainWorldDir` as an environment variable or MSBuild property. On the current development machine it also automatically detects:

```text
D:/Application/Steam/steamapps/common/Rain World
```

Normal explicit build command when SlugBase is auto-detected:

```powershell
dotnet build .\DryCycle.sln -c Release -p:RainWorldDir="D:/Application/Steam/steamapps/common/Rain World"
```

DryCycle references:

```text
BepInEx/core/BepInEx.dll
BepInEx/utils/PUBLIC-Assembly-CSharp.dll
BepInEx/plugins/HOOKS-Assembly-CSharp.dll
RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll
RainWorld_Data/Managed/UnityEngine.dll
RainWorld_Data/Managed/UnityEngine.CoreModule.dll
SlugBase.dll
```

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
src/Thirst/HydrationWeakness.cs
src/Thirst/SlugBaseHydrationFeatures.cs
src/Thirst/FoodWaterTable.cs
src/HUD/ThirstMeter.cs
src/HUD/HydrationDivider.cs
```

`src/Thirst/SlugBaseHydrationFeatures.cs` registers and resolves `WaterLossRate` and `WaterPips`. `src/Thirst/ThirstHooks.cs` applies passive WV loss and uses per-character `WaterPips` for shelter checks and saved sleep costs. `src/HUD/HydrationDivider.cs` resolves the current character every draw so its cyan divider tracks `WaterPips`, including SlugBase JSON reloads and Jolly camera focus changes. `src/HUD/ThirstMeter.cs` reconstructs the correct per-character pre-sleep water total and drains the current rightmost occupied pip continuously toward the saved post-sleep target. `src/Thirst/HydrationWeakness.cs` converts the current pip-based water amount into WV and applies Rain World's temporary malnourished weakness at 200 WV or below while preserving genuine vanilla starvation malnourishment.
