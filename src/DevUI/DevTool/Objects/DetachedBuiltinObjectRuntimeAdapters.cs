using System;
using System.Runtime.CompilerServices;
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

        UpdatableAndDeletable runtime = Acquire(room, target, createIfMissing: true);
        if (runtime == null)
            return;

        switch (runtime)
        {
            case LightningMachine machine when target.data is PlacedObject.LightningMachineData data:
                machine.pos = target.pos;
                machine.startPoint = data.startPoint;
                machine.endPoint = data.endPoint;
                machine.chance = data.chance;
                machine.permanent = data.permanent;
                machine.radial = data.radial;
                machine.width = data.width;
                machine.intensity = data.intensity;
                machine.lifeTime = data.lifeTime;
                machine.lightningParam = data.lightningParam;
                machine.lightningType = data.lightningType;
                machine.impactType = data.impact;
                machine.volume = data.volume;
                machine.soundType = data.soundType;
                machine.random = data.random;
                machine.light = data.light;
                break;

            case EnergySwirl swirl when target.data is PlacedObject.EnergySwirlData data:
                swirl.setPos = target.pos;
                swirl.setRad = data.Rad;
                swirl.setDepth = data.depth;
                swirl.color = Color.white;
                swirl.colorFromEnviroment =
                    data.colorType == PlacedObject.EnergySwirlData.ColorType.Environment;
                swirl.effectColor = Math.Max(
                    -1,
                    data.colorType.Index - PlacedObject.EnergySwirlData.ColorType.EffectColor1.Index);
                break;

            case SnowSource snow when target.data is PlacedObject.SnowSourceData data:
                snow.pos = target.pos;
                snow.rad = data.Rad;
                snow.intensity = data.intensity;
                snow.noisiness = data.noisiness;
                snow.shape = data.shape;
                break;

            case LocalBlizzard blizzard when target.data is PlacedObject.LocalBlizzardData data:
                blizzard.pos = target.pos;
                blizzard.rad = data.Rad;
                blizzard.intensity = data.intensity;
                blizzard.scale = data.scale;
                blizzard.angle = data.angle;
                break;

            case CellDistortion distortion when target.data is PlacedObject.CellDistortionData data:
                distortion.pos = target.pos;
                distortion.rad = data.Rad;
                distortion.intensity = data.intensity;
                distortion.scale = data.scale;
                distortion.cromaticIntensity = data.chromaticIntensity;
                distortion.timeMult = data.timeMult;
                break;

            case SteamPipe steam when target.data is PlacedObject.SteamPipeData data:
                steam.pos = target.pos;
                steam.direction = Direction(data.handlePos);
                steam.intensity = Mathf.Clamp(data.Rad / 250f, 0f, 1f);
                steam.wallSteamer = target.type == PlacedObject.Type.WallSteamer;
                break;
        }
    }

    internal static void Remove(global::Room room, PlacedObject target)
    {
        if (!Supports(target) || room == null)
            return;

        UpdatableAndDeletable runtime = Acquire(room, target, createIfMissing: false);
        bindings.Remove(target);
        DestroyRuntime(room, runtime);
    }

    private static bool Supports(PlacedObject target) =>
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
            UpdatableAndDeletable runtime = target.type switch
            {
                PlacedObject.Type.LightningMachine when target.data is PlacedObject.LightningMachineData data =>
                    CreateLightning(target, data),
                PlacedObject.Type.EnergySwirl =>
                    new EnergySwirl(target.pos, Color.white, null),
                PlacedObject.Type.SnowSource =>
                    new SnowSource(target.pos),
                PlacedObject.Type.LocalBlizzard =>
                    new LocalBlizzard(target.pos, 100f, 1f, 0.5f),
                PlacedObject.Type.CellDistortion =>
                    new CellDistortion(target.pos, 100f, 1f, 0.5f, 0f, 0f),
                PlacedObject.Type.SteamPipe when target.data is PlacedObject.SteamPipeData data =>
                    new SteamPipe(target.pos, Direction(data.handlePos), Mathf.Clamp(data.Rad / 250f, 0f, 1f), false),
                PlacedObject.Type.WallSteamer when target.data is PlacedObject.SteamPipeData data =>
                    new SteamPipe(target.pos, Direction(data.handlePos), Mathf.Clamp(data.Rad / 250f, 0f, 1f), true),
                _ => null
            };

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
