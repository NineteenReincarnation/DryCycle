# Optional compile-time references

DryCycle's normal build path uses the real Rain World installation through the `RainWorldDir` environment variable.

The project automatically references:

- `Rain World/BepInEx/core/*.dll`
- `Rain World/BepInEx/plugins/*.dll`
- `Rain World/BepInEx/utils/*.dll`
- `Rain World/RainWorld_Data/Managed/Assembly-CSharp-firstpass.dll`
- `Rain World/RainWorld_Data/Managed/Unity*.dll`

This `lib` folder is only for additional stripped reference assemblies that are not already available from the game directories.

Do not put generated stand-in versions of `PUBLIC-Assembly-CSharp.dll` or `HOOKS-Assembly-CSharp.dll` here. DryCycle must compile against the same Rain World assemblies that the game will load at runtime.
