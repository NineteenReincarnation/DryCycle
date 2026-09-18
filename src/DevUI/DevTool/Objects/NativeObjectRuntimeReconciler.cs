using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Owns runtime side effects that vanilla historically hid inside PlacedObjectRepresentation
/// constructors/Refresh methods. Native authoring writes the model directly, so runtime presence,
/// live refresh and removal are reconciled here without materializing a DevInterface tree.
/// </summary>
internal static class NativeObjectRuntimeReconciler
{
    internal static void RefreshAfterMutation(EditorSession session, PlacedObject target)
    {
        if (target == null) return;

        try
        {
            target.data?.RefreshLiveVisuals();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool native object live visual refresh failed: " + error.Message);
        }

        global::Room room = session?.Room;
        if (room == null) return;

        EnsureRuntimePresence(room, target);

        if (target.data is PlacedObject.TerrainHandleData)
        {
            try
            {
                if (room.terrain?.terrainList == null) return;
                foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
                {
                    if (terrain is TerrainCurve curve)
                        curve.UpdateHandles();
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native terrain handle reconciliation failed: " + error.Message);
            }
            return;
        }

        if (target.data is PlacedObject.LocalTerrainData localTerrain)
        {
            try
            {
                if (room.terrain?.terrainList == null) return;
                foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
                {
                    if (terrain is LocalTerrainCurve local && ReferenceEquals(local.data, localTerrain))
                        local.RefreshCurve();
                    else if (terrain is CurvedSlope slope && ReferenceEquals(slope.data, localTerrain))
                        slope.RefreshCurve();
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native spline terrain reconciliation failed: " + error.Message);
            }
        }
    }

    internal static void RemoveRuntime(EditorSession session, PlacedObject target)
    {
        global::Room room = session?.Room;
        if (room == null || target == null) return;

        if (target.data is PlacedObject.LocalTerrainData localTerrain &&
            room.terrain?.terrainList != null)
        {
            for (int i = room.terrain.terrainList.Count - 1; i >= 0; i--)
            {
                TerrainManager.ITerrain terrain = room.terrain.terrainList[i];
                bool matches =
                    terrain is LocalTerrainCurve local && ReferenceEquals(local.data, localTerrain) ||
                    terrain is CurvedSlope slope && ReferenceEquals(slope.data, localTerrain);
                if (!matches) continue;

                room.terrain.terrainList.RemoveAt(i);
                if (terrain is UpdatableAndDeletable runtime)
                {
                    try { runtime.Destroy(); }
                    catch { }
                    try { room.RemoveObject(runtime); }
                    catch { }
                }
            }
        }

        if (target.data is PlacedObject.SpawnMigrationStreamData streamData &&
            room.updateList != null)
        {
            for (int i = room.updateList.Count - 1; i >= 0; i--)
            {
                if (room.updateList[i] is not VoidSpawnMigrationStream stream ||
                    !ReferenceEquals(stream.data, streamData))
                    continue;

                try { stream.Destroy(); }
                catch { }
                try { room.RemoveObject(stream); }
                catch { }
            }
        }
    }

    private static void EnsureRuntimePresence(global::Room room, PlacedObject target)
    {
        if (target.data is PlacedObject.LocalTerrainData localTerrain)
        {
            bool found = false;
            if (room.terrain?.terrainList != null)
            {
                foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
                {
                    if (target.type == PlacedObject.Type.LocalTerrain &&
                        terrain is LocalTerrainCurve local &&
                        ReferenceEquals(local.data, localTerrain))
                    {
                        found = true;
                        break;
                    }

                    if (target.type == PlacedObject.Type.CurvedSlope &&
                        terrain is CurvedSlope slope &&
                        ReferenceEquals(slope.data, localTerrain))
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                try
                {
                    if (target.type == PlacedObject.Type.LocalTerrain)
                        room.AddObject(new LocalTerrainCurve(room, localTerrain));
                    else if (target.type == PlacedObject.Type.CurvedSlope)
                        room.AddObject(new CurvedSlope(room, localTerrain));
                }
                catch (Exception error)
                {
                    Plugin.Logger?.LogWarning(
                        "DevTool native spline terrain runtime creation failed: " + error.Message);
                }
            }
        }

        if (target.data is PlacedObject.SpawnMigrationStreamData streamData)
        {
            bool found = false;
            if (room.updateList != null)
            {
                for (int i = 0; i < room.updateList.Count; i++)
                {
                    if (room.updateList[i] is VoidSpawnMigrationStream stream &&
                        ReferenceEquals(stream.data, streamData))
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (!found)
            {
                try
                {
                    room.AddObject(new VoidSpawnMigrationStream(room, streamData));
                }
                catch (Exception error)
                {
                    Plugin.Logger?.LogWarning(
                        "DevTool native spawn-migration runtime creation failed: " + error.Message);
                }
            }
        }
    }
}
