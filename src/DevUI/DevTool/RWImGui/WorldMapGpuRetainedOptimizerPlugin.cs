using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Lightweight CPU-side guards for the retained GPU World Map.
///
/// Dirty chunk ownership lives directly inside WorldMapGpuScene. This compatibility layer keeps two
/// cheap lookup optimizations around that scene: source validity follows WorldMapGpuCache.Generation,
/// and RoomPanel lookup is indexed once per MapPage instead of scanning MapPage.subNodes for every
/// room on every drag frame. There is deliberately no second retained renderer here.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapGpuRendererPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapGpuRetainedOptimizerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapGPU.RetainedOptimizer";
    public const string PluginName = "DryCycle DevTool GPU World Map Retained Optimizer";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapGpuRetainedOptimizer.Enable(Logger);
    private void OnDisable() => WorldMapGpuRetainedOptimizer.Disable();
}

internal static class WorldMapGpuRetainedOptimizer
{
    private delegate int OrigComputeRoomSourceHash(MapPage page, WorldMapGpuScene.FrameState frame);
    private delegate int HookComputeRoomSourceHash(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame);

    private delegate bool OrigTryFindRoomPanel(MapPage page, int roomIndex, out RoomPanel panel);
    private delegate bool HookTryFindRoomPanel(
        OrigTryFindRoomPanel orig,
        MapPage page,
        int roomIndex,
        out RoomPanel panel);

    private static readonly HookComputeRoomSourceHash SourceHashHookDelegate = ComputeRoomSourceHashHook;
    private static readonly HookTryFindRoomPanel RoomPanelHookDelegate = TryFindRoomPanelHook;

    private static readonly Dictionary<int, RoomPanel> panelIndex = new();

    private static ManualLogSource log;
    private static IDisposable sourceHashHook;
    private static IDisposable roomPanelHook;
    private static MapPage indexedPage;
    private static int indexedSubNodeCount = -1;
    private static EditorMapPresentationSnapshot cachedSourceSnapshot;
    private static int cachedSourceGeneration = int.MinValue;
    private static int cachedSourceHash;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type sceneType = typeof(WorldMapGpuScene);
            MethodInfo sourceHash = sceneType.GetMethod(
                "ComputeRoomSourceHash",
                flags,
                null,
                new[] { typeof(MapPage), typeof(WorldMapGpuScene.FrameState) },
                null);
            MethodInfo findRoomPanel = sceneType.GetMethod(
                "TryFindRoomPanel",
                flags,
                null,
                new[] { typeof(MapPage), typeof(int), typeof(RoomPanel).MakeByRefType() },
                null);
            if (sourceHash == null || findRoomPanel == null)
                throw new MissingMemberException("World Map retained CPU lookup targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            sourceHashHook = constructor.Invoke(new object[] { sourceHash, SourceHashHookDelegate }) as IDisposable;
            roomPanelHook = constructor.Invoke(new object[] { findRoomPanel, RoomPanelHookDelegate }) as IDisposable;
            enabled = true;
            log?.LogInfo("GPU World Map retained CPU lookup cache enabled.");
        }
        catch (Exception error)
        {
            string message = Unwrap(error).Message;
            Disable();
            logger?.LogWarning("GPU World Map retained CPU lookup cache could not attach: " + message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref roomPanelHook);
        DisposeHook(ref sourceHashHook);
        panelIndex.Clear();
        indexedPage = null;
        indexedSubNodeCount = -1;
        cachedSourceSnapshot = null;
        cachedSourceGeneration = int.MinValue;
        cachedSourceHash = 0;
        enabled = false;
        log = null;
    }

    private static int ComputeRoomSourceHashHook(
        OrigComputeRoomSourceHash orig,
        MapPage page,
        WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || frame?.Snapshot?.Available != true)
            return orig(page, frame);

        EditorMapPresentationSnapshot snapshot = frame.Snapshot;
        int generation = WorldMapGpuCache.Generation;
        if (ReferenceEquals(cachedSourceSnapshot, snapshot) && cachedSourceGeneration == generation)
            return cachedSourceHash;

        // WorldMapGpuCache already owns source validation. Its generation changes when cached room
        // data is invalidated/replaced. MapEditorPresentationHub publishes immutable snapshots and
        // retains the same instance while the map model is stable, so hashing the room identity/layer
        // vector only once per published snapshot removes the per-frame O(roomCount) scan.
        unchecked
        {
            int hash = 17;
            hash = hash * 397 ^ generation;
            EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            hash = hash * 397 ^ rooms.Length;
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;
            }

            cachedSourceSnapshot = snapshot;
            cachedSourceGeneration = generation;
            cachedSourceHash = hash;
            return hash;
        }
    }

    private static bool TryFindRoomPanelHook(
        OrigTryFindRoomPanel orig,
        MapPage page,
        int roomIndex,
        out RoomPanel panel)
    {
        panel = null;
        if (!enabled || page == null)
            return orig(page, roomIndex, out panel);

        EnsurePanelIndex(page);
        if (panelIndex.TryGetValue(roomIndex, out panel) && panel?.roomRep?.room?.index == roomIndex)
            return true;

        // Defensive slow-path for rare page mutations that replace a node without changing count.
        if (!orig(page, roomIndex, out panel) || panel == null) return false;
        panelIndex[roomIndex] = panel;
        return true;
    }

    private static void EnsurePanelIndex(MapPage page)
    {
        int count = page?.subNodes?.Count ?? 0;
        if (ReferenceEquals(indexedPage, page) && indexedSubNodeCount == count) return;

        panelIndex.Clear();
        indexedPage = page;
        indexedSubNodeCount = count;
        if (page?.subNodes == null) return;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room != null)
                panelIndex[panel.roomRep.room.index] = panel;
        }
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
