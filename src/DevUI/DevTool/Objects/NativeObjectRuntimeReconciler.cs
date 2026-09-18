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

        if (target.data is PlacedObject.WaterFlowData)
        {
            target.pos.x = Mathf.Round(target.pos.x / 20f) * 20f;
            target.pos.y = Mathf.Round(target.pos.y / 20f) * 20f;
        }

        if (target.data is PlacedObject.SuperSlopeData superSlope)
        {
            try
            {
                if (room.terrain?.terrainList != null)
                {
                    foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
                    {
                        if (terrain is SuperSlope slope && ReferenceEquals(slope.data, superSlope))
                            slope.thickness = superSlope.bottom;
                    }
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native SuperSlope reconciliation failed: " + error.Message);
            }
        }

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

        if (target.data is PlacedObject.SuperSlopeData superSlope &&
            room.terrain?.terrainList != null)
        {
            for (int i = room.terrain.terrainList.Count - 1; i >= 0; i--)
            {
                if (room.terrain.terrainList[i] is not SuperSlope slope ||
                    !ReferenceEquals(slope.data, superSlope))
                    continue;

                room.terrain.terrainList.RemoveAt(i);
                try { slope.Destroy(); }
                catch { }
                try { room.RemoveObject(slope); }
                catch { }
            }
        }

        if (target.data is GeyserData &&
            room.updateList != null)
        {
            for (int i = room.updateList.Count - 1; i >= 0; i--)
            {
                if (room.updateList[i] is not Geyser geyser ||
                    !ReferenceEquals(geyser.pObj, target))
                    continue;
                try { geyser.Destroy(); }
                catch { }
                try { room.RemoveObject(geyser); }
                catch { }
            }
        }

        if (target.data is MudPit.MudPitData &&
            room.updateList != null)
        {
            for (int i = room.updateList.Count - 1; i >= 0; i--)
            {
                if (room.updateList[i] is not MudPit pit ||
                    !ReferenceEquals(pit.pObj, target))
                    continue;
                try { pit.Destroy(); }
                catch { }
                try { room.RemoveObject(pit); }
                catch { }
            }
        }

        RemoveWaterMembership(room, target);

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
        EnsureWaterMembership(room, target);
        EnsureSimpleRoomRuntime(room, target);

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

        if (target.data is PlacedObject.SuperSlopeData superSlope)
        {
            bool found = false;
            if (room.terrain?.terrainList != null)
            {
                foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
                {
                    if (terrain is SuperSlope slope && ReferenceEquals(slope.data, superSlope))
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
                    room.AddObject(new SuperSlope(room, superSlope));
                }
                catch (Exception error)
                {
                    Plugin.Logger?.LogWarning(
                        "DevTool native SuperSlope runtime creation failed: " + error.Message);
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

    private static void EnsureSimpleRoomRuntime(global::Room room, PlacedObject target)
    {
        if (room.updateList == null) return;

        if (target.data is GeyserData)
        {
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] is Geyser geyser && ReferenceEquals(geyser.pObj, target))
                    return;

            try { room.AddObject(new Geyser(target)); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native Geyser runtime creation failed: " + error.Message);
            }
            return;
        }

        if (target.data is MudPit.MudPitData)
        {
            for (int i = 0; i < room.updateList.Count; i++)
            {
                if (room.updateList[i] is not MudPit pit || !ReferenceEquals(pit.pObj, target))
                    continue;

                int required = pit.SegmentCount + 1;
                if (pit.heights != null && pit.heights.Length == required)
                    return;

                // Resizing a live pit changes its simulation segment count. Recreate only that
                // runtime object; the authored PlacedObject identity remains unchanged.
                try { pit.Destroy(); }
                catch { }
                try { room.RemoveObject(pit); }
                catch { }
                break;
            }

            try { room.AddObject(new MudPit(target)); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native MudPit runtime creation failed: " + error.Message);
            }
        }
    }

    private static void EnsureWaterMembership(global::Room room, PlacedObject target)
    {
        Water water = room.waterObject;
        if (water?.surfaces == null || water.MainSurface == null)
            return;

        if (target.data is WaterCutoffData)
        {
            bool found = false;
            for (int i = 0; i < water.MainSurface.waterCutoffs.Count; i++)
            {
                if (!ReferenceEquals(water.MainSurface.waterCutoffs[i], target)) continue;
                found = true;
                break;
            }
            if (!found)
                water.MainSurface.waterCutoffs.Add(target);
        }

        if (target.data is AirPocketData)
        {
            for (int i = 1; i < water.surfaces.Length; i++)
            {
                if (water.surfaces[i] is Water.AirPocketSurface pocket &&
                    ReferenceEquals(pocket.pObj, target))
                    return;
            }

            Water.Surface[] next = new Water.Surface[water.surfaces.Length + 1];
            Array.Copy(water.surfaces, next, water.surfaces.Length);
            next[next.Length - 1] = new Water.AirPocketSurface(water, target);
            water.surfaces = next;
        }
    }

    private static void RemoveWaterMembership(global::Room room, PlacedObject target)
    {
        Water water = room.waterObject;
        if (water?.surfaces == null || water.MainSurface == null)
            return;

        if (target.data is WaterCutoffData)
        {
            for (int i = water.MainSurface.waterCutoffs.Count - 1; i >= 0; i--)
                if (ReferenceEquals(water.MainSurface.waterCutoffs[i], target))
                    water.MainSurface.waterCutoffs.RemoveAt(i);
        }

        if (target.data is AirPocketData)
        {
            int match = -1;
            for (int i = 1; i < water.surfaces.Length; i++)
            {
                if (water.surfaces[i] is Water.AirPocketSurface pocket &&
                    ReferenceEquals(pocket.pObj, target))
                {
                    match = i;
                    break;
                }
            }
            if (match < 0) return;

            Water.Surface[] next = new Water.Surface[water.surfaces.Length - 1];
            int dst = 0;
            for (int i = 0; i < water.surfaces.Length; i++)
                if (i != match)
                    next[dst++] = water.surfaces[i];
            water.surfaces = next;
        }
    }
}
