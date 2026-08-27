# DryCycle

Rain World v1.11.8 code mod. Mod ID: `Anno`.

Current version: **0.0.34**.

## Versioning

DryCycle increments only the final development number:

```text
0.0.31 -> 0.0.32
0.0.32 -> 0.0.33
0.0.33 -> 0.0.34
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
- The water display is now **fully continuous** rather than quantized to empty / half / full. A total of `2.37` hydration therefore renders as two full water pips and a third pip filled to **37%**.
- Whenever the currently active water pip is partially filled, its liquid surface keeps a subtle idle wave even when the player is not drinking.
- Drinking and food hydration gains continue to use a stronger wave and smooth rising-water animation before settling back to the idle wave.
- Passive dehydration tracks the real WV value continuously, so while the HUD is visible the current rightmost water surface can be watched slowly sinking instead of snapping between half-pip states.
- Passive dehydration automatically reveals the vanilla lower-left HUD every time another **half hydration pip (200 WV)** has been lost. With the default `WaterLossRate = 5 WV/s`, this occurs once every **40 seconds** while water is being consumed normally.
- Water stays visible during vanilla food restore/pop animations and scales with the food-circle outer-radius animation.
- The cyan hibernation divider uses the current character's `WaterPips` and copies the vanilla survival-divider spacing.
- Normal sleep drains water visually from right to left using the same continuous liquid-level representation.
- Full-stomach hydrating food can still be eaten without increasing normal food; a temporary 50%-scale overflow food pip appears to the right.
- Non-hydrating food at full stomach keeps the vanilla refusal feedback without repeatedly resetting the shake forever.
- Shortcut transitions preserve hydration HUD state; drinking itself remains disabled while travelling through a shortcut.
- Jolly co-op keeps hydration, passive loss, and `WaterPips` independent per human player.
- NPC slugpups remain excluded from DryCycle hydration.

## KingVultureSpear prototype

Version 0.0.30 adds the first prototype of the **KingVultureSpear** extraction system.

- Only a **dead King Vulture** can be harvested.
- The player must have a free hand and stand close to the King Vulture's head/tusk area.
- Holding the pickup/eat input for about **55 frames** starts and completes the pull.
- The two King Vulture tusks are tracked independently. Each side can be extracted once. After a successful extraction, the pickup button must be released before a second tusk can be pulled.
- The closest eligible tusk is selected automatically. A tusk that is already detached/fired far away from the head is not treated as something that can be harvested from the corpse.
- When extraction finishes, the original corpse-side tusk body, detail layer, wire, and laser are hidden. A separate `KingVultureSpear` object is created at the same position and orientation and is immediately grabbed by the player's free hand.
- The detached item copies the source tusk's side, current profile (`zRot`), King Vulture color pair, armor color, and `patternDisplace` value.
- Its renderer recreates the original King Tusk's **15-segment `TriangleMesh` geometry**, including the original tusk bend/profile/radius formulas, and uses Rain World's original **`KingTusk` shader**. The item does not keep the original sprite instances, wire, or laser, so it remains independent from the corpse's `VultureGraphics` lifecycle.
- The usable item currently inherits normal `Spear` gameplay behavior. Special damage, durability, charging, tethering, or other weapon abilities have not been assigned yet.
- `KingVultureSpear` has its own registered `AbstractObjectType`, an `AbstractSpear` subclass, and a custom save parser so the object can be abstractized/realized instead of existing only as a temporary room effect.
- Which tusks have been removed from a particular corpse is currently stored on that King Vulture's `AbstractCreature` for its current abstract/realized lifetime. It is not yet designed as a permanent cross-cycle world-resource state.

Version 0.0.31 adds the missing `Unity.Mathematics.dll` compile reference required by Rain World APIs exposing `Unity.Mathematics.float2` in their public signatures.

Version 0.0.32 addresses the first in-game extraction test feedback:

- An eligible corpse-side tusk now gets vanilla-style pickup feedback when the player enters range: a pickup-range sound plus a visible white pulse/highlight.
- While the pickup button is held, the selected tusk pulses more strongly, receives a more visible tug, and the slugcat's free hand reaches toward the tusk so the pull action is readable before extraction completes.
- Detached `KingVultureSpear` meshes no longer assign a flat white sprite color after setting custom per-vertex colors.
- The two corpse tusks now survive front/behind sprite-slot swaps independently. Both dynamic body/detail slots are restored before each corpse draw, after which only the actually extracted side is hidden.
- A detached KingVultureSpear also uses a per-vertex white blink when it later becomes a normal pickup candidate.

Version 0.0.33 changes the detached tusk renderer to follow the original Rain World v1.11.8 King Tusk rendering path as closely as possible rather than merely approximating its colors:

- Body/detail meshes still use the exact vanilla 15-segment `MakeLongMesh` topology and the exact `TuskBend`, `TuskProfBend`, and `TuskRad` formulas.
- Both detached meshes are now placed in the same **Midground** container used by `KingTusks.Tusk`.
- `ApplyPalette` now restores the vanilla common sprite tint before writing the custom vertex colors. This sprite-level state is part of the original `KingTusk` shader input and was the main remaining reason the detached pattern could differ from the corpse-side tusk.
- Body and detail vertex colors now mirror the original `KingTusks.Tusk.ApplyPalette` / `UpdateTuskColors` formulas directly.
- The detail mesh alpha is still the original `patternDisplace`, exactly as in `KingTusks.Tusk.ApplyPalette`.
- Under MMF, detached-tusk darkness now also applies Rain World's `LightSourceExposure` factor, matching the lighting calculation used by `VultureGraphics` while allowing the light response to follow the detached item.

Version 0.0.34 adds the requested extraction pose and carrying weight penalties:

- While a tusk is being pulled from a dead King Vulture, the slugcat's available hands use the same absolute-target arm presentation as Rain World's vanilla heavy-corpse dragging, with a small increasing body strain toward the tusk. The corpse remains effectively immovable during the extraction.
- A human-controlled slugcat carrying a `KingVultureSpear` in either hand or on `spearOnBack` runs at **75%** normal speed, climbs poles at **74%** normal speed, and corridor-climbs at **78%** normal speed.
- The movement multipliers are applied only around the relevant vanilla movement update and the original `SlugcatStats` values are restored immediately afterward, avoiding persistent stat mutation.
- NPC slugpups do not receive these carrying penalties.

This is still a source-level prototype and should be re-tested in the local Rain World installation.

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
RainWorld_Data/Managed/Unity.Mathematics.dll
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
src/Items/KingVultureSpear/AbstractKingVultureSpear.cs
src/Items/KingVultureSpear/KingVultureSpear.cs
src/Items/KingVultureSpear/KingVultureSpearHooks.cs
src/Items/KingVultureSpear/KingVultureSpearFeedback.cs
src/Items/KingVultureSpear/KingVultureSpearPlayerEffects.cs
```

Temperature mechanics are not implemented yet.
