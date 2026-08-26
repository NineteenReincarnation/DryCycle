# Compile-time references

DryCycle does not redistribute Rain World game assemblies.

For a local build, either set the MSBuild property `RainWorldPath` to the Rain World installation directory, or place these files in this `lib` directory:

Required Rain World references:

- `PUBLIC-Assembly-CSharp.dll`
- `HOOKS-Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`

Optional offline references (used when present instead of NuGet packages):

- `BepInEx.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`

When `RainWorldPath` is set, DryCycle automatically resolves the required game assemblies from the normal Rain World/BepInEx directories.
