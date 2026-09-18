using System;

namespace DryCycle.DevUI.DevTool.Objects;

/// <summary>
/// Built-in PlacedObject runtime adapters whose vanilla DevUI representations historically created
/// or refreshed gameplay/cosmetic runtime objects as a side effect. These adapters are model/runtime
/// only: no DevInterface types and no editor presentation state.
/// </summary>
internal static class BuiltinObjectRuntimeAdapters
{
    internal static void Refresh(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null || target == null)
            return;

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

        for (int i = room.updateList.Count - 1; i >= 0; i--)
        {
            UpdatableAndDeletable runtime = room.updateList[i];
            bool matches =
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

            DestroyRuntime(room, runtime);
        }
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
