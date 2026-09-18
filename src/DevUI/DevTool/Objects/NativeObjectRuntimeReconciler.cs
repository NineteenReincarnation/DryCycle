using System;
using DryCycle.DevUI.DevTool.Core;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Applies model/runtime side effects that used to be hidden inside PlacedObjectRepresentation
/// Refresh implementations. Native authoring writes the model directly, so every required gameplay
/// refresh belongs here instead of depending on an invisible DevInterface tree.
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
}
