# Map layer and Player Map thumbnail integration regressions

Run after building `DryCycle.sln` in Release. This executable loads the **deployed**
DryCycle core and DevTool frontend, together with the installed game's public assembly.
The default installation is `D:/Steam/steamapps/common/Rain World`.

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' build DryCycle.sln -c Release
& 'C:/Program Files/dotnet/dotnet.exe' build tests/MapLayer.Tests/MapLayer.Tests.csproj -c Release
& tests/MapLayer.Tests/bin/Release/net48/MapLayer.Tests.exe
```

For a different installation, pass `-p:RainWorldDir=...` to the test build and the
same directory as the executable's first argument. The Ancient Site mod, B5 room
data and built core/frontend DLLs must exist there.

The test reads the actual `B5_BDF01` record and executes production code for:

- Command processing and native authoring, including no-op and invalid-layer edits.
- Undo/redo, World Map snapshots, retained scene metadata and spatial layer filters.
- Player Map's normal synchronization path, revision invalidation after layer-only
  edits and snapshot reuse on stable frames, with optional projection plugins disabled.
- Replacing a stale RoomPanel without losing edits, while still importing a real
  external mutation on the same panel.
- Capturing and serializing map records without rereading changed panel layers.
- Automatically preparing all locally available B5 room thumbnails while the actual
  World Map background policy rejects visual work at zoom 0.12. No room is selected.
  The test compiles real room-text bakes, supplies one authored curve fixture and
  empty terrain settings for other rooms, then runs the real bounded background
  scanner and Player Map synchronization. It checks readiness, terrain overlay
  reuse, stable snapshots and inactivity when Player Map is hidden.

The game-owned object graph is a fixture. A copy of UnityEngine.CoreModule in the
test output supplies a controlled frame clock and shader-name hashing; other native
calls throw. The layer tests use a stable missing-terrain entry. The thumbnail test
supplies cached room-text bakes and RoomSettings objects to isolate the scheduling
and publication regression from Unity's file resolution and GPU initialization.
No game assembly or author map file is modified. This verifies managed data flow,
room-text compilation and terrain overlay generation, not in-game drawing,
RoomSettings disk loading or the final disk commit.
