# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.25**.

## Versioning

DryCycle increments only the final development number:

```text
0.0.23 -> 0.0.24
0.0.24 -> 0.0.25
0.0.25 -> 0.0.26
```

The patch number does not roll over at 9.

## Hydration model

- **1 hydration pip = 400 WV**.
- **Half a pip = 200 WV**.
- Hydration capacity is **not fixed at five pips**.
- A slugcat's maximum hydration is its **maximum food-pip count × 400 WV**.
- Example: a 5-food-pip slugcat has 2000 WV capacity; a 6-food-pip slugcat has 2400 WV capacity; Survivor's 7 food pips give 2800 WV capacity.
- At **200 WV or lower**, the player receives Rain World's malnourished/starving weakness through the temporary `malnourishedByCreature` path.
- Hydration is stored in pip units in `SaveState.unrecognizedSaveStrings`; the WV layer is an internal conversion, so the existing save format remains readable.
- Water fills the same vanilla food circles from left to right. Sleep depletion runs from the currently rightmost occupied water pip toward the left.

## Character hydration configuration

DryCycle has built-in character defaults for two values:

- `WaterLossRate`: passive hydration loss in **WV per second**. Default: **5 WV/s**.
- `WaterPips`: hydration pips required for normal hibernation. The same value is used as the normal hibernation water cost and as the cyan divider position.

Built-in `WaterPips` values:

| Slugcat | WaterPips |
| --- | ---: |
| Monk (`Yellow`) | 1 |
| Survivor (`White`) | 2 |
| Hunter (`Red`) | 3 |
| Gourmand | 4 |
| Artificer | 3 |
| Rivulet | 3 |
| Spearmaster (`Spear`) | 3 |
| Saint | 2 |
| Inv | 6 |
| Watcher | 2 |

Characters not listed above fall back to `WaterPips = 2` and `WaterLossRate = 5`. `Night` is Rain World's hidden legacy Nightcat identifier rather than a normal story campaign, so DryCycle currently leaves it on that fallback instead of treating it as a separate campaign default.

`WaterPips` is **not** a capacity setting. Capacity always follows the character's food meter. A character may therefore have, for example, 12 maximum food/water pips while only requiring 6 pips to hibernate.

## Optional SlugBase compatibility

SlugBase is **not required** to run DryCycle. `modinfo.json` has no SlugBase requirement, the plugin declares no SlugBase dependency metadata, and the project has no compile-time `SlugBase.dll` reference.

When SlugBase is present, DryCycle discovers it during Rain World's mod initialization and registers two optional custom player features through reflection before SlugBase performs its JSON scan:

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

The feature names are case-sensitive:

- `WaterLossRate`
- `WaterPips`

If a SlugBase character omits either feature, DryCycle uses the built-in fallback values above. Because values are resolved on demand, SlugBase JSON reloads can update the active settings without DryCycle maintaining a second character-config cache.

SlugBase's own `food_max`/food-meter behavior remains the source of truth for a custom character's maximum hydration capacity because DryCycle reads the actual food meter / `MaxFoodInStomach` rather than using `WaterPips` as a cap.

## Current HUD behavior

- Hydration is rendered as cyan liquid inside vanilla food pips; there is no second hydration row.
- Static hydration uses empty / half / full states.
- Drinking and food hydration gains use the same continuous rising-water animation and wave surface.
- Water stays visible during vanilla food restore/pop animations and scales with the food-circle outer-radius animation.
- The cyan hibernation divider uses the current character's `WaterPips` and copies the vanilla survival-divider spacing.
- Normal sleep drains water visually from right to left.
- Full-stomach hydrating food can still be eaten without increasing normal food; a temporary 50%-scale overflow food pip appears to the right.
- Non-hydrating food at full stomach keeps the vanilla refusal feedback without repeatedly resetting the shake forever.
- Shortcut transitions preserve hydration HUD state; drinking itself remains disabled while travelling through a shortcut.
- Jolly co-op keeps hydration, passive loss, and `WaterPips` independent per human player.
- NPC slugpups remain excluded from DryCycle hydration.

## Standard Rain World build setup

The project targets **.NET Framework 4.8** and compiles against the installed Rain World assemblies. SlugBase is not needed to compile.

```powershell
dotnet build .\DryCycle.sln -c Release -p:RainWorldDir="D:/Application/Steam/steamapps/common/Rain World"
```

DryCycle references only:

```text
BepInEx/core/BepInEx.dll
BepInEx/utils/PUBLIC-Assembly-CSharp.dll
BepInEx/plugins/HOOKS-Assembly-CSharp.dll
RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll
RainWorld_Data/Managed/UnityEngine.dll
RainWorld_Data/Managed/UnityEngine.CoreModule.dll
```

Output path on the current development machine:

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

Temperature mechanics are not implemented yet.
