using System;
using System.Runtime.CompilerServices;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Built-in PlacedObject runtime adapters whose vanilla DevUI representations historically created
/// or refreshed gameplay/cosmetic runtime objects as a side effect. These adapters are model/runtime
/// only: no DevInterface types and no editor presentation state.
/// </summary>
internal static class BuiltinObjectRuntimeAdapters
{
    private sealed class LightFixtureState
    {
        internal string TypeValue = string.Empty;
        internal int RandomSeed;
        internal bool Captured;
    }

    private static ConditionalWeakTable<PlacedObject, LightFixtureState> lightFixtureStates = new();

    internal static void ResetRuntimeState() =>
        lightFixtureStates = new ConditionalWeakTable<PlacedObject, LightFixtureState>();

    internal static void Prepare(global::Room room, PlacedObject target)
    {
        if (room == null || target?.data is not PlacedObject.LightFixtureData data)
            return;

        LightFixtureState state = lightFixtureStates.GetValue(target, _ => new LightFixtureState());
        state.TypeValue = data.type?.value ?? string.Empty;
        state.RandomSeed = data.randomSeed;
        state.Captured = true;
    }

    internal static void Refresh(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null || target == null)
            return;

        if (target.type == PlacedObject.Type.LightFixture &&
            target.data is PlacedObject.LightFixtureData)
        {
            EnsureLightFixtureRuntime(room, target);
            return;
        }

        if (target.type == PlacedObject.Type.AdjustableFan)
        {
            if (FindAdjustableFan(room, target) == null)
                room.AddObject(new AdjustableFan(target, room));
            return;
        }

        if (target.type == PlacedObject.Type.HarmfulSteam)
        {
            if (FindHarmfulSteam(room, target) == null)
                room.AddObject(new HarmfulSteam(target, room));
            return;
        }

        if (target.type == PlacedObject.Type.SkyWhalePathfinding)
        {
            if (FindSkyWhaleNode(room, target) == null)
                room.AddObject(new SkyWhalePathfindingNode(target, room));
            return;
        }


        if (target.type == PlacedObject.Type.CustomDecal)
        {
            CustomDecal runtime = FindCustomDecal(room, target);
            if (runtime == null)
            {
                runtime = new CustomDecal(target);
                room.AddObject(runtime);
            }

            if (target.data is PlacedObject.CustomDecalData data &&
                !string.Equals(runtime.usingImage, data.imageName, StringComparison.Ordinal))
                runtime.UpdateAsset();
            runtime.UpdateMesh();
            return;
        }

        if (target.type == PlacedObject.Type.GooDrips)
        {
            GooDripSource runtime = FindGooDrips(room, target);
            if (runtime == null)
            {
                runtime = new GooDripSource(target);
                room.AddObject(runtime);
            }

            runtime.RefreshCeilingTiles();
            return;
        }

        if (target.type == PlacedObject.Type.Rainbow)
        {
            Rainbow runtime = FindRainbow(room, target);
            if (runtime == null)
            {
                runtime = new Rainbow(room, target);
                room.AddObject(runtime);
            }

            runtime.Refresh();
            return;
        }

        if (target.type == PlacedObject.Type.RainbowNoFade)
        {
            RainbowNoFade runtime = FindRainbowNoFade(room, target);
            if (runtime == null)
            {
                runtime = new RainbowNoFade(room, target);
                room.AddObject(runtime);
            }

            runtime.Refresh();
            return;
        }

        if (target.type == PlacedObject.Type.SSLightRod)
        {
            SSLightRod runtime = FindLightRod(room, target);
            if (runtime == null)
            {
                runtime = new SSLightRod(target, room);
                room.AddObject(runtime);
            }

            runtime.UpdateLightAmount();
            return;
        }

        if (target.type == PlacedObject.Type.PlateTree ||
            target.type == PlacedObject.Type.RotPlateTree)
        {
            PlateTree runtime = FindPlateTree(room, target);
            PlateTree.Variant expected =
                target.type == PlacedObject.Type.RotPlateTree
                    ? PlateTree.Variant.Rot
                    : PlateTree.Variant.Plate;

            if (runtime == null || runtime.variant != expected)
            {
                if (runtime != null)
                    DestroyRuntime(room, runtime);

                runtime = new PlateTree(target, room, expected);
                room.AddObject(runtime);
            }

            runtime.Reset();
            return;
        }

        if (target.type == PlacedObject.Type.DeepProcessing)
        {
            DeepProcessing runtime = FindDeepProcessing(room, target);
            if (runtime == null)
            {
                runtime = new DeepProcessing(target);
                room.AddObject(runtime);
            }

            runtime.meshDirty = true;
        }
    }

    internal static void Remove(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null || target == null)
            return;

        lightFixtureStates.Remove(target);


        for (int i = room.updateList.Count - 1; i >= 0; i--)
        {
            UpdatableAndDeletable runtime = room.updateList[i];
            bool matches =
                runtime is LightFixture fixture && ReferenceEquals(fixture.placedObject, target) ||
                runtime is AdjustableFan fan && ReferenceEquals(fan.pObj, target) ||
                runtime is HarmfulSteam steam && ReferenceEquals(steam.placedObject, target) ||
                runtime is SkyWhalePathfindingNode whale && ReferenceEquals(whale.placedObject, target) ||
                runtime is CustomDecal decal && ReferenceEquals(decal.placedObject, target) ||
                runtime is GooDripSource drips && ReferenceEquals(drips.placedObject, target) ||
                runtime is Rainbow rainbow && ReferenceEquals(rainbow.placedObject, target) ||
                runtime is RainbowNoFade noFade && ReferenceEquals(noFade.placedObject, target) ||
                runtime is SSLightRod rod && ReferenceEquals(rod.placedObject, target) ||
                runtime is PlateTree tree && ReferenceEquals(tree.pObj, target) ||
                runtime is DeepProcessing processing && ReferenceEquals(processing.placedObject, target);

            if (!matches)
                continue;

            if (runtime is CustomDecal customDecal)
            {
                try { customDecal.RoomUnloaded(); }
                catch { }
            }

            if (runtime is AdjustableFan adjustableFan && adjustableFan.FanElement != null)
                DestroyRuntime(room, adjustableFan.FanElement);

            DestroyRuntime(room, runtime);
        }
    }

    private static void EnsureLightFixtureRuntime(global::Room room, PlacedObject target)
    {
        PlacedObject.LightFixtureData data = target.data as PlacedObject.LightFixtureData;
        if (data == null) return;

        LightFixtureState state = lightFixtureStates.GetValue(target, _ => new LightFixtureState());
        LightFixture runtime = FindLightFixture(room, target);
        string typeValue = data.type?.value ?? string.Empty;
        bool constructorStateChanged =
            state.Captured &&
            (!string.Equals(state.TypeValue, typeValue, StringComparison.Ordinal) ||
             state.RandomSeed != data.randomSeed);

        if (runtime == null || constructorStateChanged)
        {
            if (runtime != null)
                DestroyRuntime(room, runtime);

            runtime = CreateLightFixture(room, target, data);
            if (runtime != null)
                room.AddObject(runtime);
        }

        state.TypeValue = typeValue;
        state.RandomSeed = data.randomSeed;
        state.Captured = true;
    }

    private static LightFixture CreateLightFixture(
        global::Room room,
        PlacedObject target,
        PlacedObject.LightFixtureData data)
    {
        try
        {
            if (data.type == PlacedObject.LightFixtureData.Type.RedLight)
                return new Redlight(room, target, data);
            if (data.type == PlacedObject.LightFixtureData.Type.HolyFire)
                return new HolyFire(room, target, data);
            if (data.type == PlacedObject.LightFixtureData.Type.ZapCoilLight)
                return new ZapCoilLight(room, target, data);
            if (data.type == PlacedObject.LightFixtureData.Type.DeepProcessing)
                return new DeepProcessingLight(room, target, data);
            if (data.type == PlacedObject.LightFixtureData.Type.SlimeMoldLight)
                return new SlimeMoldLight(room, target, data);
            if (data.type == PlacedObject.LightFixtureData.Type.RedSubmersible)
                return new Redlight(room, target, data, submersible: true);
            if (data.type == PlacedObject.LightFixtureData.Type.GlowWeedLight)
                return new GlowWeedLight(room, target, data);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native LightFixture runtime creation failed: " + error.Message);
        }

        return null;
    }

    private static LightFixture FindLightFixture(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is LightFixture runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static AdjustableFan FindAdjustableFan(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is AdjustableFan runtime &&
                ReferenceEquals(runtime.pObj, target))
                return runtime;
        return null;
    }

    private static HarmfulSteam FindHarmfulSteam(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is HarmfulSteam runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static SkyWhalePathfindingNode FindSkyWhaleNode(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is SkyWhalePathfindingNode runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static CustomDecal FindCustomDecal(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is CustomDecal runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static GooDripSource FindGooDrips(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is GooDripSource runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static Rainbow FindRainbow(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is Rainbow runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static RainbowNoFade FindRainbowNoFade(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is RainbowNoFade runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static SSLightRod FindLightRod(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is SSLightRod runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

    private static PlateTree FindPlateTree(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is PlateTree runtime &&
                ReferenceEquals(runtime.pObj, target))
                return runtime;
        return null;
    }

    private static DeepProcessing FindDeepProcessing(global::Room room, PlacedObject target)
    {
        for (int i = 0; i < room.updateList.Count; i++)
            if (room.updateList[i] is DeepProcessing runtime &&
                ReferenceEquals(runtime.placedObject, target))
                return runtime;
        return null;
    }

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
