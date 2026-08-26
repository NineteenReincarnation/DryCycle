# Local Rain World references

This folder is for local compile-time references only. Do not commit the game DLLs.

If `RainWorldPath` is not provided, place these two files here:

- `PUBLIC-Assembly-CSharp.dll`
- `HOOKS-Assembly-CSharp.dll`

They are normally found in the Rain World installation under:

- `BepInEx/utils/PUBLIC-Assembly-CSharp.dll`
- `BepInEx/plugins/HOOKS-Assembly-CSharp.dll`

The DLL files are ignored by `.gitignore`.
