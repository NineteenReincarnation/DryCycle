using System;
using System.Runtime.CompilerServices;
using DryCycle.DevUI.DevTool.Core;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Owns runtime side effects that vanilla historically hid inside PlacedObjectRepresentation
/// constructors/Refresh methods. Native authoring writes the model directly, so runtime presence,
/// live refresh and removal are reconciled here without materializing a DevInterface tree.
/// </summary>
internal static class NativeObjectRuntimeReconciler
{
    private sealed class LightBinding
    {
        internal LightSource Runtime;
    }

    private static ConditionalWeakTable<PlacedObject, LightBinding> lightBindings = new();

    internal static void ResetRuntimeState()
    {
        lightBindings = new ConditionalWeakTable<PlacedObject, LightBinding>();
        BuiltinObjectRuntimeAdapters.ResetRuntimeState();
        DetachedBuiltinObjectRuntimeAdapters.ResetRuntimeState();
    }

    internal static void PrepareForMutation(EditorSession session, PlacedObject target)
    {
        global::Room room = session?.Room;
        if (room == null || target == null)
            return;

        BuiltinObjectRuntimeAdapters.Prepare(room, target);
        DetachedBuiltinObjectRuntimeAdapters.Prepare(room, target);

        if (target.data is PlacedObject.LightSourceData)
            EnsureLightSourceRuntime(room, target, createIfMissing: true);
    }

    internal static void RefreshAfterMutation(EditorSession session, PlacedObject target) =>
        RefreshAfterMutationCore(session, target, interactivePreview: false);

    internal static void RefreshInteractivePreview(EditorSession session, PlacedObject target) =>
        RefreshAfterMutationCore(session, target, interactivePreview: true);

    private static void RefreshAfterMutationCore(
        EditorSession session,
        PlacedObject target,
        bool interactivePreview)
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
        BuiltinObjectRuntimeAdapters.Refresh(room, target);
        DetachedBuiltinObjectRuntimeAdapters.Refresh(room, target);
        SyncLightSourceRuntime(room, target);

        if (target.data is PlacedObject.FairyParticleData fairyParticle)
        {
            try { fairyParticle.Apply(room); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native FairyParticle live preview failed: " + error.Message);
            }
        }

        if (target.data is PlacedObject.DayNightData dayNight)
        {
            try { dayNight.Apply(room); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native DayNight live preview failed: " + error.Message);
            }
        }

        if (ModManager.Watcher &&
            target.data is Watcher.GrassBlade.TerrainGrassPatchData grassPatch)
        {
            grassPatch.pos = target.pos;
            if (!interactivePreview)
                RespawnTerrainGrassPatch(room, grassPatch);
        }

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

        if (target.type == PlacedObject.Type.TerrainRubble)
        {
            RefreshTerrainRubble(room);
        }

        if (target.data is PlacedObject.RippleStalkData rippleData)
        {
            rippleData.update = true;
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

    internal static void FinalizeInteractiveMutation(EditorSession session, PlacedObject target)
    {
        global::Room room = session?.Room;
        if (room == null || target == null || !ModManager.Watcher)
            return;

        if (target.data is Watcher.GrassBlade.TerrainGrassPatchData grassPatch)
        {
            grassPatch.pos = target.pos;
            RespawnTerrainGrassPatch(room, grassPatch);
        }
    }

    internal static void RefreshAfterRemoval(EditorSession session, PlacedObject target)
    {
        global::Room room = session?.Room;
        if (room == null || target == null)
            return;

        BuiltinObjectRuntimeAdapters.RefreshAfterRemoval(room, target);

        if (target.type == PlacedObject.Type.TerrainRubble)
            RefreshTerrainRubble(room);
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

        if (room.updateList != null)
        {
            for (int i = room.updateList.Count - 1; i >= 0; i--)
            {
                UpdatableAndDeletable runtime = room.updateList[i];
                bool matches =
                    target.type == PlacedObject.Type.LightBeam &&
                    runtime is LightBeam beam &&
                    ReferenceEquals(beam.placedObject, target) ||
                    target.type == PlacedObject.Type.BlackSpot &&
                    runtime is BlackSpot blackSpot &&
                    ReferenceEquals(blackSpot.pObj, target) ||
                    target.type == PlacedObject.Type.WindRect &&
                    runtime is WindRect wind &&
                    ReferenceEquals(wind.placedObj, target);

                if (!matches) continue;
                try { runtime.Destroy(); }
                catch { }
                try { room.RemoveObject(runtime); }
                catch { }
            }
        }

        if (target.data is DevInterface.GeyserData &&
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

        if (ModManager.Watcher &&
            target.data is Watcher.GrassBlade.TerrainGrassPatchData grassPatch)
        {
            RemoveTerrainGrassPatchRuntime(room, grassPatch);
        }

        RemoveWaterMembership(room, target);
        BuiltinObjectRuntimeAdapters.Remove(room, target);
        DetachedBuiltinObjectRuntimeAdapters.Remove(room, target);
        RemoveLightSourceRuntime(room, target);

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

    private static void RespawnTerrainGrassPatch(
        global::Room room,
        Watcher.GrassBlade.TerrainGrassPatchData data)
    {
        if (room == null || data == null)
            return;

        try
        {
            if (data.spawnedGrass == null)
                data.spawnedGrass = new System.Collections.Generic.List<Watcher.GrassBlade>();

            Watcher.Grass.InitGrassInRoom(room);
            Watcher.GrassBlade.SpawnGrassPatch(room, data);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native TerrainGrassPatch regeneration failed: " + error.Message);
        }
    }

    private static void RemoveTerrainGrassPatchRuntime(
        global::Room room,
        Watcher.GrassBlade.TerrainGrassPatchData data)
    {
        if (room == null || data?.spawnedGrass == null)
            return;

        try
        {
            if (room.grass?.grassBlades != null)
            {
                for (int i = data.spawnedGrass.Count - 1; i >= 0; i--)
                    room.grass.grassBlades.Remove(data.spawnedGrass[i]);

                room.grass.needsShuffle = true;
            }

            data.spawnedGrass.Clear();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native TerrainGrassPatch removal failed: " + error.Message);
        }
    }

    private static void RefreshTerrainRubble(global::Room room)
    {
        if (room?.terrain?.terrainList == null)
            return;

        try
        {
            foreach (TerrainManager.ITerrain terrain in room.terrain.terrainList)
            {
                if (terrain is TerrainCurve curve)
                    curve.UpdateRubble();
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool native terrain rubble reconciliation failed: " + error.Message);
        }
    }

    private static void SyncLightSourceRuntime(global::Room room, PlacedObject target)
    {
        if (target?.data is not PlacedObject.LightSourceData data)
            return;

        LightSource light = EnsureLightSourceRuntime(room, target, createIfMissing: true);
        if (light == null)
            return;

        bool enteringNightMode = !light.nightLight && data.nightLight;

        light.setPos = target.pos;
        light.setRad = data.Rad;
        light.setAlpha = data.strength;
        light.fadeWithSun = data.fadeWithSun;
        light.color = Color.white;
        light.colorFromEnvironment = data.colorType == PlacedObject.LightSourceData.ColorType.Environment;
        light.flat = data.flat;
        light.effectColor = Math.Max(-1, (int)data.colorType - 2);
        light.setBlinkProperties(data.blinkType, data.blinkRate);
        light.nightLight = data.nightLight;

        if (enteringNightMode)
            light.nightFade = 0f;
    }

    private static LightSource EnsureLightSourceRuntime(
        global::Room room,
        PlacedObject target,
        bool createIfMissing)
    {
        if (room == null || target?.data is not PlacedObject.LightSourceData)
            return null;

        LightBinding binding = lightBindings.GetValue(target, _ => new LightBinding());
        if (binding.Runtime != null &&
            ReferenceEquals(binding.Runtime.room, room) &&
            !binding.Runtime.slatedForDeletetion)
            return binding.Runtime;

        binding.Runtime = FindExistingLightSource(room, target.pos);
        if (binding.Runtime != null || !createIfMissing)
            return binding.Runtime;

        try
        {
            LightSource created = new(
                target.pos,
                environmentalLight: true,
                Color.white,
                tiedToObject: null);
            room.AddObject(created);
            binding.Runtime = created;
            return created;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native LightSource runtime creation failed: " + error.Message);
            return null;
        }
    }

    private static LightSource FindExistingLightSource(global::Room room, Vector2 position)
    {
        if (room?.lightSources != null)
        {
            for (int i = 0; i < room.lightSources.Count; i++)
            {
                LightSource light = room.lightSources[i];
                if (light != null && !light.slatedForDeletetion && light.Pos == position)
                    return light;
            }
        }

        if (ModManager.MMF && room?.cosmeticLightSources != null)
        {
            for (int i = 0; i < room.cosmeticLightSources.Count; i++)
            {
                LightSource light = room.cosmeticLightSources[i];
                if (light != null && !light.slatedForDeletetion && light.Pos == position)
                    return light;
            }
        }

        return null;
    }

    private static void RemoveLightSourceRuntime(global::Room room, PlacedObject target)
    {
        if (target?.data is not PlacedObject.LightSourceData)
            return;

        LightSource light = EnsureLightSourceRuntime(room, target, createIfMissing: false);
        lightBindings.Remove(target);
        if (light == null)
            return;

        try { light.Destroy(); }
        catch { }
        try { room.RemoveObject(light); }
        catch { }
    }

    private static void EnsureSimpleRoomRuntime(global::Room room, PlacedObject target)
    {
        if (room.updateList == null) return;

        if (target.type == PlacedObject.Type.LightBeam &&
            target.data is LightBeam.LightBeamData)
        {
            EnsureLightBeamRuntime(room, target);
            return;
        }

        if (target.type == PlacedObject.Type.BlackSpot)
        {
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] is BlackSpot blackSpot &&
                    ReferenceEquals(blackSpot.pObj, target))
                    return;

            try { room.AddObject(new BlackSpot(target)); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native BlackSpot runtime creation failed: " + error.Message);
            }
            return;
        }

        if (target.type == PlacedObject.Type.WindRect)
        {
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] is WindRect wind &&
                    ReferenceEquals(wind.placedObj, target))
                    return;

            try { room.AddObject(new WindRect(target)); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool native WindRect runtime creation failed: " + error.Message);
            }
            return;
        }

        if (target.data is DevInterface.GeyserData)
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

    private static LightBeam EnsureLightBeamRuntime(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null ||
            target?.type != PlacedObject.Type.LightBeam ||
            target.data is not LightBeam.LightBeamData data)
            return null;

        LightBeam beam = null;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is LightBeam existing &&
                ReferenceEquals(existing.placedObject, target))
            {
                beam = existing;
                break;
            }
        }

        if (beam == null)
        {
            try
            {
                beam = new LightBeam(target);
                room.AddObject(beam);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native LightBeam runtime creation failed: " + error.Message);
                return null;
            }
        }

        bool enteringNightMode = !beam.nightLight && data.nightLight;
        beam.meshDirty = true;
        beam.SetBlinkProperties(data.blinkType, data.blinkRate);
        beam.nightLight = data.nightLight;
        if (enteringNightMode)
            beam.nightFade = 0f;
        return beam;
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
