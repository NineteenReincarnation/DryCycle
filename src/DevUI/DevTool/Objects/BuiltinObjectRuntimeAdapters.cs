using System;
using System.Runtime.CompilerServices;
using RWCustom;
using UnityEngine;

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

    private sealed class UrbanLifeState
    {
        internal int LayerCount;
        internal bool IsShadow;
        internal bool Captured;
    }

    private sealed class UrbanCandleHolderState
    {
        internal int Seed;
        internal bool Captured;
    }

    private static ConditionalWeakTable<PlacedObject, LightFixtureState> lightFixtureStates = new();
    private static ConditionalWeakTable<PlacedObject, UrbanLifeState> urbanLifeStates = new();
    private static ConditionalWeakTable<PlacedObject, UrbanCandleHolderState> urbanCandleHolderStates = new();

    internal static void ResetRuntimeState()
    {
        lightFixtureStates = new ConditionalWeakTable<PlacedObject, LightFixtureState>();
        urbanLifeStates = new ConditionalWeakTable<PlacedObject, UrbanLifeState>();
        urbanCandleHolderStates = new ConditionalWeakTable<PlacedObject, UrbanCandleHolderState>();
    }

    internal static void Prepare(global::Room room, PlacedObject target)
    {
        if (room == null || target == null)
            return;

        if (target.data is PlacedObject.LightFixtureData lightFixture)
        {
            LightFixtureState state = lightFixtureStates.GetValue(target, _ => new LightFixtureState());
            state.TypeValue = lightFixture.type?.value ?? string.Empty;
            state.RandomSeed = lightFixture.randomSeed;
            state.Captured = true;
        }

        if (ModManager.Watcher && target.data is Watcher.UrbanLife.UrbanLifeData urbanLife)
        {
            UrbanLifeState state = urbanLifeStates.GetValue(target, _ => new UrbanLifeState());
            state.LayerCount = DesiredUrbanLifeLayerCount(urbanLife);
            state.IsShadow = urbanLife.isShadow;
            state.Captured = true;
        }

        if (ModManager.Watcher &&
            target.data is Watcher.UrbanCandleHolder.UrbanCandleHolderData holder)
        {
            UrbanCandleHolderState state =
                urbanCandleHolderStates.GetValue(target, _ => new UrbanCandleHolderState());
            state.Seed = holder.seed;
            state.Captured = true;
        }
    }

    internal static void Refresh(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null || target == null)
            return;

        if (ModManager.Watcher &&
            target.type == Watcher.WatcherEnums.PlacedObjectType.UrbanLife &&
            target.data is Watcher.UrbanLife.UrbanLifeData urbanLife)
        {
            EnsureUrbanLifeRuntime(room, target, urbanLife);
            return;
        }

        if (ModManager.Watcher &&
            target.type == Watcher.WatcherEnums.PlacedObjectType.UrbanLifePath &&
            target.data is Watcher.UrbanLifePath.UrbanLifePathData urbanPath)
        {
            EnsureUrbanLifePathRuntime(room, target, urbanPath);
            return;
        }

        if (ModManager.Watcher &&
            target.type == Watcher.WatcherEnums.PlacedObjectType.UrbanCandleHolder &&
            target.data is Watcher.UrbanCandleHolder.UrbanCandleHolderData holder)
        {
            EnsureUrbanCandleHolderRuntime(room, target, holder);
            return;
        }

        if (ModManager.Watcher &&
            target.type == Watcher.WatcherEnums.PlacedObjectType.FlameJet &&
            target.data is Watcher.FlameJet.FlameJetData flameJet)
        {
            EnsureFlameJetRuntime(room, target, flameJet);
            return;
        }

        if (ModManager.Watcher &&
            target.type == Watcher.WatcherEnums.PlacedObjectType.KarmaFlowerPatch &&
            target.data is Watcher.KarmaFlowerPatch.KarmaFlowerPatchData)
        {
            RefreshKarmaFlowerPatchRuntime(room);
            return;
        }

        if (target.type == PlacedObject.Type.ProjectedStars)
        {
            EnsureProjectedStarsRuntime(room, target);
            return;
        }

        if (target.type == PlacedObject.Type.SuperStructureFuses)
        {
            EnsureSuperStructureFusesRuntime(room, target);
            return;
        }

        if (EnsureCommonLinkedRuntime(room, target))
            return;

        if (target.type == PlacedObject.Type.LightFixture &&
            target.data is PlacedObject.LightFixtureData)
        {
            EnsureLightFixtureRuntime(room, target);
            return;
        }

        if (target.type == PlacedObject.Type.InsectGroup)
        {
            EnsureInsectGroupRuntime(room, target);
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
        urbanLifeStates.Remove(target);
        urbanCandleHolderStates.Remove(target);

        RemoveWatcherUrbanRuntime(room, target);

        if (target.type == PlacedObject.Type.InsectGroup)
            RemoveInsectGroupRuntime(room, target);


        for (int i = room.updateList.Count - 1; i >= 0; i--)
        {
            UpdatableAndDeletable runtime = room.updateList[i];
            bool matches =
                runtime is StarMatrix stars && ReferenceEquals(stars.placedObject, target) ||
                runtime is SuperStructureFuses fuses && ReferenceEquals(fuses.placedObject, target) ||
                runtime is PlayerPushback pushback && ReferenceEquals(pushback.placedObj, target) ||
                runtime is WaterCurrent current && ReferenceEquals(current.pObj, target) ||
                runtime is FluxDrain drain && ReferenceEquals(drain.pObj, target) ||
                runtime is SpinningFan fanRuntime && ReferenceEquals(fanRuntime.pObj, target) ||
                runtime is ReliableIggyDirection iggy && ReferenceEquals(iggy.pObj, target) ||
                runtime is ARKillRect killRect && ReferenceEquals(killRect.po, target) ||
                runtime is SpotLight spot && ReferenceEquals(spot.placedObject, target) ||
                runtime is GravityDisruptor disruptor && ReferenceEquals(disruptor.placedObject, target) ||
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

            if (runtime is SpinningFan spinningFan && spinningFan.FanElement != null)
                DestroyRuntime(room, spinningFan.FanElement);

            DestroyRuntime(room, runtime);
        }
    }

    internal static void RefreshAfterRemoval(global::Room room, PlacedObject target)
    {
        if (room == null || target == null || !ModManager.Watcher)
            return;

        if (target.type == Watcher.WatcherEnums.PlacedObjectType.KarmaFlowerPatch)
            RefreshKarmaFlowerPatchRuntime(room);
    }

    private static void EnsureFlameJetRuntime(
        global::Room room,
        PlacedObject target,
        Watcher.FlameJet.FlameJetData data)
    {
        if (room == null || target == null || data == null)
            return;

        data.pos = target.pos;

        Watcher.FlameJet runtime = data.obj;
        if (runtime == null ||
            !ReferenceEquals(runtime.room, room) ||
            runtime.slatedForDeletetion)
        {
            if (runtime != null && ReferenceEquals(runtime.room, room))
                DestroyRuntime(room, runtime);

            try
            {
                runtime = Watcher.FlameJet.FromPlacedObject(room, target);
                data.obj = runtime;
                room.AddObject(runtime);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native FlameJet runtime creation failed: " + error.Message);
                return;
            }
        }

        data.intensityMin = Mathf.Clamp01(data.intensityMin);
        data.intensityMax = Mathf.Clamp(data.intensityMax, data.intensityMin, 1f);
        data.temperatureMin = Mathf.Clamp01(data.temperatureMin);
        data.temperatureMax = Mathf.Clamp(data.temperatureMax, data.temperatureMin, 1f);
        data.lethality = Mathf.Clamp(data.lethality, 0, 2);

        runtime.setPos = target.pos;
        runtime.setTarget = data.target;
        runtime.intensity = data.intensity;
        runtime.temperature = data.temperature;
        runtime.intensityAnimSpeed = data.intensityAnimSpeed;
        runtime.intensityAnimOffset = data.intensityAnimOffset;
        runtime.temperatureAnimSpeed = data.temperatureAnimSpeed;
        runtime.temperatureAnimOffset = data.temperatureAnimOffset;
        runtime.intensityAnim = data.intensityAnim;
        runtime.temperatureAnim = data.temperatureAnim;
        runtime.activeDuring = data.activeDuring;
        runtime.intensityMin = 0f;
        runtime.intensityMax = 1f;
        runtime.intensityMin = data.intensityMin;
        runtime.intensityMax = data.intensityMax;
        runtime.temperatureMin = 0f;
        runtime.temperatureMax = 1f;
        runtime.temperatureMin = data.temperatureMin;
        runtime.temperatureMax = data.temperatureMax;
        runtime.linkTempToIntens = data.linkTempToIntens;
        runtime.lethality = data.lethality;
        runtime.width = data.width;
        runtime.fireVolumeMax = data.fireVolumeMax;
        runtime.smokeVolumeMax = data.smokeVolumeMax;
    }

    private static void RefreshKarmaFlowerPatchRuntime(global::Room room)
    {
        if (room == null || !ModManager.Watcher)
            return;

        bool anyActive = false;
        if (room.roomSettings?.placedObjects != null)
        {
            for (int i = 0; i < room.roomSettings.placedObjects.Count; i++)
            {
                PlacedObject placed = room.roomSettings.placedObjects[i];
                if (placed?.active == true &&
                    placed.type == Watcher.WatcherEnums.PlacedObjectType.KarmaFlowerPatch)
                {
                    anyActive = true;
                    break;
                }
            }
        }

        Watcher.KarmaFlowerPatch runtime = null;
        if (room.updateList != null)
        {
            for (int i = room.updateList.Count - 1; i >= 0; i--)
            {
                if (room.updateList[i] is not Watcher.KarmaFlowerPatch patch)
                    continue;

                if (runtime == null && anyActive)
                {
                    runtime = patch;
                    continue;
                }

                DestroyRuntime(room, patch);
            }
        }

        if (!anyActive)
            return;

        if (runtime == null)
        {
            try
            {
                runtime = new Watcher.KarmaFlowerPatch();
                room.AddObject(runtime);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native KarmaFlowerPatch runtime creation failed: " + error.Message);
                return;
            }
        }

        try { runtime.PlaceFlowers(); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native KarmaFlowerPatch refresh failed: " + error.Message);
        }
    }

    private static void EnsureUrbanLifeRuntime(
        global::Room room,
        PlacedObject target,
        Watcher.UrbanLife.UrbanLifeData data)
    {
        data.pos = target.pos;

        Watcher.UrbanLife runtime = data.obj;
        UrbanLifeState state = urbanLifeStates.GetValue(target, _ => new UrbanLifeState());
        int desiredLayers = DesiredUrbanLifeLayerCount(data);
        bool rebuild =
            runtime == null ||
            !ReferenceEquals(runtime.room, room) ||
            runtime.slatedForDeletetion ||
            state.Captured && (state.LayerCount != desiredLayers || state.IsShadow != data.isShadow);

        if (rebuild)
        {
            if (runtime != null && ReferenceEquals(runtime.room, room))
                DestroyRuntime(room, runtime);

            try
            {
                runtime = Watcher.UrbanLife.FromPlacedObject(room, target);
                data.obj = runtime;
                room.AddObject(runtime);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native UrbanLife runtime creation failed: " + error.Message);
                return;
            }
        }

        runtime.meshDirty = true;
        state.LayerCount = desiredLayers;
        state.IsShadow = data.isShadow;
        state.Captured = true;
    }

    private static int DesiredUrbanLifeLayerCount(Watcher.UrbanLife.UrbanLifeData data) =>
        (int)Mathf.Max(
            1f,
            Mathf.Clamp01(data?.nLayers ?? 0f) * Watcher.UrbanLife.maxLayers);

    private static void EnsureUrbanLifePathRuntime(
        global::Room room,
        PlacedObject target,
        Watcher.UrbanLifePath.UrbanLifePathData data)
    {
        data.pos = target.pos;

        Watcher.UrbanLifePath runtime = data.obj;
        if (runtime != null &&
            ReferenceEquals(runtime.room, room) &&
            !runtime.slatedForDeletetion)
            return;

        try
        {
            runtime = Watcher.UrbanLifePath.FromPlacedObject(room, target);
            data.obj = runtime;
            room.AddObject(runtime);
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native UrbanLifePath runtime creation failed: " + error.Message);
        }
    }

    private static void EnsureUrbanCandleHolderRuntime(
        global::Room room,
        PlacedObject target,
        Watcher.UrbanCandleHolder.UrbanCandleHolderData data)
    {
        data.pos = target.pos;

        UrbanCandleHolderState state =
            urbanCandleHolderStates.GetValue(target, _ => new UrbanCandleHolderState());
        Watcher.UrbanCandleHolder runtime = data.obj;
        bool rebuild =
            runtime == null ||
            !ReferenceEquals(runtime.room, room) ||
            runtime.slatedForDeletetion ||
            state.Captured && state.Seed != data.seed;

        if (rebuild)
        {
            if (runtime != null && ReferenceEquals(runtime.room, room))
                DestroyRuntime(room, runtime);

            try
            {
                runtime = new Watcher.UrbanCandleHolder(room, data);
                data.obj = runtime;
                room.AddObject(runtime);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native UrbanCandleHolder runtime creation failed: " + error.Message);
                return;
            }
        }

        runtime.pos = target.pos;
        runtime.tilt = data.tilt;
        runtime.depth = data.depth;
        runtime.height = data.height;
        runtime.scale = data.scale;
        runtime.candleWidthScale = data.candleWidth;

        state.Seed = data.seed;
        state.Captured = true;
    }

    private static void RemoveWatcherUrbanRuntime(global::Room room, PlacedObject target)
    {
        if (!ModManager.Watcher || target?.data == null)
            return;

        UpdatableAndDeletable runtime = null;

        if (target.data is Watcher.UrbanLife.UrbanLifeData urbanLife)
        {
            runtime = urbanLife.obj;
            urbanLife.obj = null;
        }
        else if (target.data is Watcher.UrbanLifePath.UrbanLifePathData path)
        {
            runtime = path.obj;
            path.obj = null;
        }
        else if (target.data is Watcher.UrbanCandleHolder.UrbanCandleHolderData holder)
        {
            runtime = holder.obj;
            holder.obj = null;
        }

        if (runtime != null && ReferenceEquals(runtime.room, room))
            DestroyRuntime(room, runtime);
    }

    private static void EnsureProjectedStarsRuntime(global::Room room, PlacedObject target)
    {
        if (target?.data is not PlacedObject.ResizableObjectData data)
            return;

        StarMatrix runtime = null;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is StarMatrix candidate &&
                ReferenceEquals(candidate.placedObject, target))
            {
                runtime = candidate;
                break;
            }
        }

        float desiredRad = data.Rad + 50f;
        if (runtime != null &&
            runtime.pos == target.pos &&
            Mathf.Abs(runtime.rad - desiredRad) <= 0.001f)
            return;

        if (runtime != null)
            DestroyRuntime(room, runtime);

        try { room.AddObject(new StarMatrix(target)); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native ProjectedStars runtime creation failed: " + error.Message);
        }
    }

    private static void EnsureSuperStructureFusesRuntime(global::Room room, PlacedObject target)
    {
        if (target?.data is not PlacedObject.GridRectObjectData data)
            return;

        SuperStructureFuses runtime = null;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is SuperStructureFuses candidate &&
                ReferenceEquals(candidate.placedObject, target))
            {
                runtime = candidate;
                break;
            }
        }

        IntRect desired = data.Rect;
        if (runtime != null && SameRect(runtime.rect, desired))
            return;

        if (runtime != null)
            DestroyRuntime(room, runtime);

        try { room.AddObject(new SuperStructureFuses(target, desired, room)); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native SuperStructureFuses runtime creation failed: " + error.Message);
        }
    }

    private static bool SameRect(IntRect a, IntRect b) =>
        a.left == b.left &&
        a.right == b.right &&
        a.bottom == b.bottom &&
        a.top == b.top;

    private static bool EnsureCommonLinkedRuntime(global::Room room, PlacedObject target)
    {
        if (room?.updateList == null || target?.type == null)
            return false;

        for (int i = 0; i < room.updateList.Count; i++)
        {
            UpdatableAndDeletable runtime = room.updateList[i];
            bool exists =
                target.type == PlacedObject.Type.PlayerPushback &&
                runtime is PlayerPushback pushback &&
                ReferenceEquals(pushback.placedObj, target) ||
                target.type == PlacedObject.Type.WaterCurrent &&
                runtime is WaterCurrent current &&
                ReferenceEquals(current.pObj, target) ||
                target.type == PlacedObject.Type.FluxDrain &&
                runtime is FluxDrain drain &&
                ReferenceEquals(drain.pObj, target) ||
                target.type == PlacedObject.Type.HugeTurbine &&
                runtime is HugeTurbine turbine &&
                ReferenceEquals(turbine.pObj, target) ||
                target.type == PlacedObject.Type.ReliableIggyDirection &&
                runtime is ReliableIggyDirection iggy &&
                ReferenceEquals(iggy.pObj, target) ||
                target.type == PlacedObject.Type.ARKillRect &&
                runtime is ARKillRect killRect &&
                ReferenceEquals(killRect.po, target) ||
                target.type == PlacedObject.Type.SpotLight &&
                runtime is SpotLight spot &&
                ReferenceEquals(spot.placedObject, target) ||
                target.type == PlacedObject.Type.GravityDisruptor &&
                runtime is GravityDisruptor disruptor &&
                ReferenceEquals(disruptor.placedObject, target);

            if (exists)
                return true;
        }

        UpdatableAndDeletable created = null;
        try
        {
            if (target.type == PlacedObject.Type.PlayerPushback)
                created = new PlayerPushback(room, target);
            else if (target.type == PlacedObject.Type.WaterCurrent)
                created = new WaterCurrent(target);
            else if (target.type == PlacedObject.Type.FluxDrain)
                created = new FluxDrain(room, target);
            else if (target.type == PlacedObject.Type.HugeTurbine)
                created = new HugeTurbine(target, room);
            else if (target.type == PlacedObject.Type.ReliableIggyDirection)
                created = new ReliableIggyDirection(target);
            else if (target.type == PlacedObject.Type.ARKillRect)
                created = new ARKillRect(room, target);
            else if (target.type == PlacedObject.Type.SpotLight)
                created = new SpotLight(target);
            else if (target.type == PlacedObject.Type.GravityDisruptor)
                created = new GravityDisruptor(target, room);
            else
                return false;

            room.AddObject(created);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning(
                "DevTool native linked runtime creation failed for '" +
                (target.type?.value ?? string.Empty) + "': " + error.Message);
            if (created is SpinningFan spinningFan && spinningFan.FanElement != null)
                DestroyRuntime(room, spinningFan.FanElement);
            DestroyRuntime(room, created);
            return true;
        }
    }

    private static void EnsureInsectGroupRuntime(global::Room room, PlacedObject target)
    {
        if (room == null || target?.type != PlacedObject.Type.InsectGroup)
            return;

        if (room.insectCoordinator == null)
        {
            room.insectCoordinator = new InsectCoordinator(room);
            room.AddObject(room.insectCoordinator);
        }

        for (int i = 0; i < room.insectCoordinator.swarms.Count; i++)
            if (ReferenceEquals(room.insectCoordinator.swarms[i].placedObject, target))
                return;

        room.insectCoordinator.AddGroup(target);
        InsectCoordinator.Swarm swarm =
            room.insectCoordinator.swarms[room.insectCoordinator.swarms.Count - 1];

        bool viewed = false;
        if (room.game?.cameras != null)
        {
            for (int i = 0; i < room.game.cameras.Length; i++)
            {
                if (room.game.cameras[i]?.room != room) continue;
                viewed = true;
                break;
            }
        }

        if (viewed)
        {
            try { swarm.Initiate(); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning(
                    "DevTool native InsectGroup live initialization failed: " + error.Message);
            }
        }
    }

    private static void RemoveInsectGroupRuntime(global::Room room, PlacedObject target)
    {
        InsectCoordinator coordinator = room?.insectCoordinator;
        if (coordinator?.swarms == null || target == null)
            return;

        for (int i = coordinator.swarms.Count - 1; i >= 0; i--)
        {
            InsectCoordinator.Swarm swarm = coordinator.swarms[i];
            if (!ReferenceEquals(swarm.placedObject, target))
                continue;

            for (int memberIndex = swarm.members.Count - 1; memberIndex >= 0; memberIndex--)
            {
                CosmeticInsect member = swarm.members[memberIndex];
                if (member == null) continue;
                try { member.Destroy(); }
                catch { }
                coordinator.allInsects?.Remove(member);
            }

            swarm.members.Clear();
            coordinator.swarms.RemoveAt(i);
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
