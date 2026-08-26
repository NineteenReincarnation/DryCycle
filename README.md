# DryCycle

DryCycle is a Rain World gameplay-mechanics mod project targeting Rain World **v1.11.8**.

The project is currently in the design/scaffolding phase. The planned core systems are:

- Thirst / hydration
- Ambient temperature and player body-temperature interactions

No gameplay hooks for those systems are enabled yet; implementation will be added after the mechanics are fully specified.

## Build prerequisites

This project intentionally does **not** commit Rain World or BepInEx game assemblies. Set the `RainWorldPath` MSBuild property to your Rain World installation folder when building.

Example on Windows:

```powershell
dotnet build DryCycle.sln -c Release -p:RainWorldPath="C:\Program Files (x86)\Steam\steamapps\common\Rain World"
```

Expected references are taken from the game's `BepInEx` and `RainWorld_Data/Managed` folders.

## Repository layout

```text
DryCycle.sln
src/DryCycle/       C# plugin project
mod/                Rain World mod package files
```

## Current design snapshot

Default hydration capacity is planned as **4 / 2**: four maximum water pips, two required to hibernate. Food and hydration must both meet their requirements for normal hibernation; starvation hibernation consumes all remaining food and hydration. Hydration can come from configured foods/creatures and from drinking while fully submerged.
