# DryCycle

DryCycle is a Rain World v1.11.8 gameplay-mechanics mod. Mod ID: `Anno`.

Current version: **0.2.0**.

## Implemented: hydration

The first playable implementation adds a second survival resource alongside food:

- Default hydration: **4 maximum / 2 required to hibernate**.
- A cyan hydration meter is drawn above the normal food meter and uses the same pip/gap language.
- Normal hibernation requires the normal food requirement and at least 2 hydration.
- Normal hibernation consumes 2 hydration.
- Starvation hibernation consumes all remaining hydration and explicitly leaves food at 0.
- While fully submerged and consuming lung air, hold the pickup/eat input (Shift on the default keyboard layout) to drink at **0.5 hydration per second**.
- Hydration is persisted inside the Rain World save as an unrecognized save-state field, so no external save file is required.
- Eating configured foods and gnawing configured creatures restores hydration. Carcass hydration is distributed across the creature's available meat bites instead of being granted once per bite at the full value.

### Current hydration table

| Food / creature | Water |
| --- | ---: |
| Batfly | 1 |
| Blue fruit | 1 |
| Bubble fruit | 3 |
| Neuron | 1 |
| Eggbug egg | 2 |
| Hazer | 2 |
| Jellyfish | 1 |
| Mushroom | 0.5 |
| Ordinary lizards | 2 |
| Salamander | 4 |
| Squidcada | 3 |
| Small / adult noodlefly | 2 / 4 |
| Snail | 2 |
| Scavenger / elite / king | 1 |
| Lantern mouse | 1 |
| Jetfish | 3 |
| Tube worm | 1 |
| Small / medium / large centipede | 1 / 2 / 3 |
| Centiwing | 3 |
| Red centipede | 6 |
| Eggbug | 2 |
| Slime mold | 1 |
| Lillypuck | 3 |
| Glow weed | 2 |
| Aquapede | 4 |
| Yeek | 2 |

Temperature mechanics are intentionally not implemented yet; their rules are still being designed.

## Build

Recommended local build with an installed copy of Rain World:

```powershell
dotnet build DryCycle.sln -c Release -p:RainWorldPath="C:\Program Files (x86)\Steam\steamapps\common\Rain World"
```

The project resolves:

- `BepInEx/utils/PUBLIC-Assembly-CSharp.dll`
- `BepInEx/plugins/HOOKS-Assembly-CSharp.dll`
- `RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll`

BepInEx and Unity compile references are taken from the game when available; otherwise the project can fall back to the matching NuGet compile packages. See `lib/README.md` for an offline setup.

A successful Release build creates an installable folder at:

```text
artifacts/DryCycle/
├─ modinfo.json
└─ plugins/
   └─ DryCycle.dll
```

## Source layout

```text
src/DryCycle/Plugin.cs
src/DryCycle/Thirst/ThirstHooks.cs
src/DryCycle/Thirst/ThirstStore.cs
src/DryCycle/Thirst/FoodWaterTable.cs
src/DryCycle/HUD/ThirstMeter.cs
```
