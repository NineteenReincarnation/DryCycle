using System;
using System.Runtime.CompilerServices;
using MoreSlugcats;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Native runtime ownership for built-in PlacedObjects whose live runtime does not retain the
/// authored PlacedObject reference. A weak PlacedObject -> runtime binding is acquired before model
/// mutation, so moving an object never loses the runtime that vanilla DevUI previously found by
/// position.
/// </summary>
internal static class DetachedBuiltinObjectRuntimeAdapters
{
    private sealed class Binding
    {
        internal UpdatableAndDeletable Runtime;
    }

    private static ConditionalWeakTable<PlacedObject, Binding> bindings = new();

    internal static void ResetRuntimeState() =>
        bindings = new ConditionalWeakTable<PlacedObject, Binding>();

    internal static void Prepare(global::Room room, PlacedObject target)
    {
        if (!Supports(target) || room == null)
            return;

        Acquire(room, target, createIfMissing: true);
    }

    internal static void Refresh(global::Room room, PlacedObject target)
    {
        if (!Supports(target) || room == null)
            return;

        if (target.type == PlacedObject.Type.FluxWaterfall)
        {
            RefreshFluxWaterfall(room, target);
            return;
        }

        UpdatableAndDeletable runtime = Acquire(room, target, createIfMissing: true);
        if (runtime == null)
            return;

        switch (runtime)
        {
            case LightningMachine machine when target.data is PlacedObject.LightningMachineData lightningData:
                machine.pos = target.pos;
                machine.startPoint = lightningData.startPoint;
                machine.endPoint = lightningData.endPoint;
                machine.chance = lightningData.chance;
                machine.permanent = lightningData.permanent;
                machine.radial = lightningData.radial;
                machine.width = lightningData.width;
                machine.intensity = lightningData.intensity;
                machine.lifeTime = lightningData.lifeTime;
                machine.lightningParam = lightningData.lightningParam;
                machine.lightningType = lightningData.lightningType;
                machine.impactType = lightningData.impact;
                machine.volume = lightningData.volume;
                machine.soundType = lightningData.soundType;
                machine.random = lightningData.random;
                machine.light = lightningData.light;
                break;

            case EnergySwirl swirl when target.data is PlacedObject.EnergySwirlData swirlData:
                swirl.setPos = target.pos;
                swirl.setRad = swirlData.Rad;
                swirl.setDepth = swirlData.depth;
                swirl.color = Color.white;
                swirl.colorFromEnviroment =
                    swirlData.colorType == PlacedObject.EnergySwirlData.ColorType.Environment;
                swirl.effectColor = Math.Max(
                    -1,
                    swirlData.colorType.Index - PlacedObject.EnergySwirlData.ColorType.EffectColor1.Index);
                break;

            case SnowSource snow when target.data is PlacedObject.SnowSourceData snowData:
                snow.pos = target.pos;
                snow.rad = snowData.Rad;
                snow.intensity = snowData.intensity;
                snow.noisiness = snowData.noisiness;
                snow.shape = snowData.shape;
                break;

            case LocalBlizzard blizzard when target.data is PlacedObject.LocalBlizzardData blizzardData:
                blizzard.pos = target.pos;
                blizzard.rad = blizzardData.Rad;
                blizzard.intensity = blizzardData.intensity;
                blizzard.scale = blizzardData.scale;
                blizzard.angle = blizzardData.angle;
                break;

            case CellDistortion distortion when target.data is PlacedObject.CellDistortionData distortionData:
                distortion.pos = target.pos;
                distortion.rad = distortionData.Rad;
                distortion.intensity = distortionData.intensity;
                distortion.scale = distortionData.scale;
                distortion.cromaticIntensity = distortionData.chromaticIntensity;
                distortion.timeMult = distortionData.timeMult;
                break;

            case SteamPipe steam when target.data is PlacedObject.SteamPipeData steamData:
                steam.pos = target.pos;
                steam.direction = Direction(steamData.handlePos);
                steam.intensity = Mathf.Clamp(steamData.Rad / 250f, 0f, 1f);
                steam.wallSteamer = target.type == PlacedObject.Type.WallSteamer;
                break;
        }
    }

    internal static void Remove(global::Room room, PlacedObject target)
    {
        if (!Supports(target) || room == null)
            return;

        if (target.type == PlacedObject.Type.FluxWaterfall)
        {
            RemoveFluxWaterfall(room, target);
            bindings.Remove(target);
            return;
        }

        UpdatableAndDeletable runtime = Acquire(room, target, createIfMissing: false);
        bindings.Remove(target);
        DestroyRuntime(room, runtime);
    }

    private static bool Supports(PlacedObject target) =>
        target?.type == PlacedObject.Type.FluxWaterfall ||
        target?.type == PlacedObject.Type.LightningMachine ||
        target?.type == PlacedObject.Type.EnergySwirl ||
        target?.type == PlacedObject.Type.SnowSource ||
        target?.type == PlacedObject.Type.LocalBlizzard ||
        target?.type == PlacedObject.Type.CellDistortion ||
        target?.type == PlacedObject.Type.SteamPipe ||
        target?.type == PlacedObject.Type.WallSteamer;

    private static UpdatableAndDeletable Acquire(
        global::Room room,
        PlacedObject target,
        bool createIfMissing)
    {
        Binding binding = bindings.GetValue(target, _ => new Binding());
        if (binding.Runtime != null &&
            ReferenceEquals(binding.Runtime.room, room) &&
            !binding.Runtime.slatedForDeletetion)
            return binding.Runtime;

        binding.Runtime = FindExisting(room, target);
        if (binding.Runtime != null || !createIfMissing)
            return binding.Runtime;

        binding.Runtime = Create(room, target);
        return binding.Runtime;
    }

    private static UpdatableAndDeletable FindExisting(global::Room room, PlacedObject target)
    {
        if (target.type == PlacedObject.Type.FluxWaterfall &&
            target.data is PlacedObject.WaterFlowData flowData &&
            room.waterFalls != null)
        {
            for (int i = 0; i < room.waterFalls.Length; i++)
                if (room.waterFalls[i] is FluxWaterfall waterfall &&
                    ReferenceEquals(waterfall.data, flowData) &&
                    !waterfall.slatedForDeletetion)
                    return waterfall;
        }

        if (target.type == PlacedObject.Type.LightningMachine && room.lightningMachines != null)
        {
            for (int i = 0; i < room.lightningMachines.Count; i++)
                if (room.lightningMachines[i] != null &&
                    !room.lightningMachines[i].slatedForDeletetion &&
                    room.lightningMachines[i].pos == target.pos)
                    return room.lightningMachines[i];
        }

        if (target.type == PlacedObject.Type.EnergySwirl && room.energySwirls != null)
        {
            for (int i = 0; i < room.energySwirls.Count; i++)
                if (room.energySwirls[i] != null &&
                    !room.energySwirls[i].slatedForDeletetion &&
                    room.energySwirls[i].Pos == target.pos)
                    return room.energySwirls[i];
        }

        if (target.type == PlacedObject.Type.SnowSource && room.snowSources != null)
        {
            for (int i = 0; i < room.snowSources.Count; i++)
                if (room.snowSources[i] != null &&
                    !room.snowSources[i].slatedForDeletetion &&
                    room.snowSources[i].pos == target.pos)
                    return room.snowSources[i];
        }

        if (target.type == PlacedObject.Type.LocalBlizzard && room.localBlizzards != null)
        {
            for (int i = 0; i < room.localBlizzards.Count; i++)
                if (room.localBlizzards[i] != null &&
                    !room.localBlizzards[i].slatedForDeletetion &&
                    room.localBlizzards[i].pos == target.pos)
                    return room.localBlizzards[i];
        }

        if (target.type == PlacedObject.Type.CellDistortion && room.cellDistortions != null)
        {
            for (int i = 0; i < room.cellDistortions.Count; i++)
                if (room.cellDistortions[i] != null &&
                    !room.cellDistortions[i].slatedForDeletetion &&
                    room.cellDistortions[i].pos == target.pos)
                    return room.cellDistortions[i];
        }

        if ((target.type == PlacedObject.Type.SteamPipe ||
             target.type == PlacedObject.Type.WallSteamer) &&
            room.updateList != null)
        {
            bool wallSteamer = target.type == PlacedObject.Type.WallSteamer;
            for (int i = 0; i < room.updateList.Count; i++)
                if (room.updateList[i] is SteamPipe steam &&
                    !steam.slatedForDeletetion &&
                    steam.pos == target.pos &&
                    steam.wallSteamer == wallSteamer)
                    return steam;
        }

        return null;
    }

    private static UpdatableAndDeletable Create(global::Room room, PlacedObject target)
    {
        try
        {
            UpdatableAndDeletable runtime = null;

            if (target.type == PlacedObject.Type.LightningMachine &&
                target.data is PlacedObject.LightningMachineData lightning)
            {
                runtime = CreateLightning(target, lightning);
            }
            else if (target.type == PlacedObject.Type.EnergySwirl)
            {
                runtime = new EnergySwirl(target.pos, Color.white, null);
            }
            else if (target.type == PlacedObject.Type.SnowSource)
            {
                runtime = new SnowSource(target.pos);
            }
            else if (target.type == PlacedObject.Type.LocalBlizzard)
            {
                runtime = new LocalBlizzard(target.pos, 100f, 1f, 0.5f);
            }
            else if (target.type == PlacedObject.Type.CellDistortion)
            {
                runtime = new CellDistortion(target.pos, 100f, 1f, 0.5f, 0f, 0f);
            }
            else if (target.type == PlacedObject.Type.SteamPipe &&
                     target.data is PlacedObject.SteamPipeData steam)
            {
                runtime = new SteamPipe(
                    target.pos,
                    Direction(steam.handlePos),
                    Mathf.Clamp(steam.Rad / 250f, 0f, 1f),
                    false);
            }
            else if (target.type == PlacedObject.Type.WallSteamer &&
                     target.data is PlacedObject.SteamPipeData wallSteam)
            {
                runtime = new SteamPipe(
                    target.pos,
                    Direction(wallSteam.handlePos),
                    Mathf.Clamp(wallSteam.Rad / 250f, 0f, 1f),
                    true);
            }

            if (runtime == null)
                return null;

            room.AddObject(runtime);
            return runtime;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool detached builtin runtime creation failed for '" +
                (target.type?.value ?? string.Empty) + "': " + error.Message);
            return null;
        }
    }

    private static void RefreshFluxWaterfall(global::Room room, PlacedObject target)
    {
        if (target?.data is not PlacedObject.WaterFlowData data)
            return;

        FluxWaterfall runtime = FindExisting(room, target) as FluxWaterfall;
        IntVector2 desiredTile = room.GetTilePosition(target.pos + new Vector2(10f, -10f));
        bool rebuild = runtime == null ||
                       runtime.tilePos.x != desiredTile.x ||
                       runtime.tilePos.y != desiredTile.y ||
                       runtime.width != data.width;

        if (!rebuild)
            return;

        if (runtime != null)
            RemoveFluxWaterfallRuntime(room, runtime);

        try
        {
            runtime = new FluxWaterfall(room, desiredTile, data);
            RegisterFluxWaterfall(room, runtime);
            room.AddObject(runtime);
            bindings.GetValue(target, _ => new Binding()).Runtime = runtime;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native FluxWaterfall runtime rebuild failed: " + error.Message);
        }
    }

    private static void RemoveFluxWaterfall(global::Room room, PlacedObject target)
    {
        FluxWaterfall runtime = FindExisting(room, target) as FluxWaterfall;
        if (runtime != null)
            RemoveFluxWaterfallRuntime(room, runtime);
    }

    private static void RegisterFluxWaterfall(global::Room room, FluxWaterfall runtime)
    {
        int count = room.waterFalls?.Length ?? 0;
        Array.Resize(ref room.waterFalls, count + 1);
        room.waterFalls[count] = runtime;
        if (room.waterObject != null)
            runtime.ConnectToWaterObject(room.waterObject);
    }

    private static void RemoveFluxWaterfallRuntime(global::Room room, FluxWaterfall runtime)
    {
        if (room.waterFalls != null)
        {
            int match = -1;
            for (int i = 0; i < room.waterFalls.Length; i++)
            {
                if (!ReferenceEquals(room.waterFalls[i], runtime)) continue;
                match = i;
                break;
            }

            if (match >= 0)
            {
                WaterFall[] next = new WaterFall[room.waterFalls.Length - 1];
                int dst = 0;
                for (int i = 0; i < room.waterFalls.Length; i++)
                    if (i != match)
                        next[dst++] = room.waterFalls[i];
                room.waterFalls = next;
            }
        }

        if (runtime.topLoop != null)
        {
            runtime.topLoop.volume = 0f;
            try { runtime.topLoop.Update(); }
            catch { }
        }
        if (runtime.bottomLoop != null)
        {
            runtime.bottomLoop.volume = 0f;
            try { runtime.bottomLoop.Update(); }
            catch { }
        }

        DestroyRuntime(room, runtime);
    }

    private static LightningMachine CreateLightning(
        PlacedObject target,
        PlacedObject.LightningMachineData data)
    {
        return new LightningMachine(
            target.pos,
            data.startPoint,
            data.endPoint,
            data.chance,
            data.permanent,
            data.radial,
            data.width,
            data.intensity,
            data.lifeTime)
        {
            lightningParam = data.lightningParam,
            lightningType = data.lightningType,
            impactType = data.impact,
            volume = data.volume,
            soundType = data.soundType,
            random = data.random,
            light = data.light
        };
    }

    private static Vector2 Direction(Vector2 handlePos) =>
        handlePos.sqrMagnitude > 0.0001f ? Custom.DirVec(Vector2.zero, handlePos) : Vector2.up;

    private static void DestroyRuntime(global::Room room, UpdatableAndDeletable runtime)
    {
        if (runtime == null)
            return;

        try { runtime.Destroy(); }
        catch { }
        try { room.RemoveObject(runtime); }
        catch { }
    }
}
