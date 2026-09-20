using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Lightweight CPU-side guards for the retained GPU World Map.
///
/// This used to RuntimeDetour WorldMapGpuScene methods owned by DryCycle itself. The scene now calls
/// this service explicitly: self-owned optimization is an ordinary interface boundary, while hooks
/// remain reserved for vanilla/third-party compatibility edges.
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
    private const int SourceAuditIntervalFrames = 15;
    private static readonly Dictionary<int, RoomPanel> panelIndex = new();

    private static ManualLogSource log;
    private static MapPage indexedPage;
    private static int indexedSubNodeCount = -1;
    private static EditorMapPresentationSnapshot cachedSourceSnapshot;
    private static int cachedSourceGeneration = int.MinValue;
    private static int cachedSourceHash;
    private static int nextSourceAuditFrame;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        enabled = true;
        log?.LogInfo("GPU World Map retained CPU lookup cache enabled through direct scene integration.");
    }

    internal static void Disable()
    {
        panelIndex.Clear();
        indexedPage = null;
        indexedSubNodeCount = -1;
        cachedSourceSnapshot = null;
        cachedSourceGeneration = int.MinValue;
        cachedSourceHash = 0;
        nextSourceAuditFrame = 0;
        enabled = false;
        log = null;
    }

    /// <summary>
    /// Returns the retained source hash only while the immutable snapshot/cache generation is stable
    /// and the bounded live-texture audit is not due. The caller computes the authoritative hash on
    /// a miss and feeds it back through ObserveRoomSourceHash.
    /// </summary>
    internal static bool TryGetRoomSourceHash(WorldMapGpuScene.FrameState frame, out int hash)
    {
        hash = 0;
        if (!enabled || frame?.Snapshot?.Available != true)
            return false;

        int generation = WorldMapGpuCache.Generation;
        if (!ReferenceEquals(cachedSourceSnapshot, frame.Snapshot) ||
            cachedSourceGeneration != generation ||
            Time.frameCount >= nextSourceAuditFrame)
            return false;

        hash = cachedSourceHash;
        return true;
    }

    internal static int ObserveRoomSourceHash(WorldMapGpuScene.FrameState frame, int liveHash)
    {
        if (!enabled || frame?.Snapshot?.Available != true)
            return liveHash;

        int generation = WorldMapGpuCache.Generation;
        unchecked
        {
            int hash = liveHash * 397 ^ generation;
            cachedSourceSnapshot = frame.Snapshot;
            cachedSourceGeneration = generation;
            cachedSourceHash = hash;
            nextSourceAuditFrame = Time.frameCount + SourceAuditIntervalFrames;
            return hash;
        }
    }

    internal static bool TryGetRoomPanel(MapPage page, int roomIndex, out RoomPanel panel)
    {
        panel = null;
        if (!enabled || page == null) return false;

        EnsurePanelIndex(page);
        return panelIndex.TryGetValue(roomIndex, out panel) &&
               panel?.roomRep?.room?.index == roomIndex;
    }

    internal static void ObserveRoomPanel(MapPage page, int roomIndex, RoomPanel panel)
    {
        if (!enabled || page == null || panel?.roomRep?.room?.index != roomIndex) return;
        EnsurePanelIndex(page);
        panelIndex[roomIndex] = panel;
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
}
