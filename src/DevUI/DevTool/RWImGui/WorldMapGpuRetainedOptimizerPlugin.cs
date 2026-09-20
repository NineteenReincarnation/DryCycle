using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// CPU-side lookup/cache service for the retained GPU World Map.
///
/// This is called explicitly by WorldMapGpuScene. It deliberately does not RuntimeDetour DryCycle
/// methods: self-owned optimization policy belongs at a normal service boundary. MapPage/RoomPanel
/// remain only as the legacy texture-source adapter until room textures are Native-owned as well.
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
        log?.LogInfo("GPU World Map retained CPU lookup cache enabled through direct service calls.");
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

    internal static int ComputeRoomSourceHash(MapPage page, WorldMapGpuScene.FrameState frame)
    {
        if (!enabled || frame?.Snapshot?.Available != true)
            return WorldMapGpuScene.ComputeRoomSourceHashCore(page, frame);

        EditorMapPresentationSnapshot snapshot = frame.Snapshot;
        int generation = WorldMapGpuCache.Generation;
        bool snapshotChanged = !ReferenceEquals(cachedSourceSnapshot, snapshot);
        bool generationChanged = cachedSourceGeneration != generation;
        bool auditDue = Time.frameCount >= nextSourceAuditFrame;

        if (!snapshotChanged && !generationChanged && !auditDue)
            return cachedSourceHash;

        // Stable frames stay O(1). The bounded audit still observes late legacy texture readiness,
        // which currently has no Native revision edge of its own.
        int liveHash = WorldMapGpuScene.ComputeRoomSourceHashCore(page, frame);
        unchecked
        {
            int hash = liveHash * 397 ^ generation;
            cachedSourceSnapshot = snapshot;
            cachedSourceGeneration = generation;
            cachedSourceHash = hash;
            nextSourceAuditFrame = Time.frameCount + SourceAuditIntervalFrames;
            return hash;
        }
    }

    internal static bool TryFindRoomPanel(MapPage page, int roomIndex, out RoomPanel panel)
    {
        panel = null;
        if (!enabled || page == null)
            return WorldMapGpuScene.TryFindRoomPanelCore(page, roomIndex, out panel);

        EnsurePanelIndex(page);
        if (panelIndex.TryGetValue(roomIndex, out panel) && panel?.roomRep?.room?.index == roomIndex)
            return true;

        // Rare same-count replacement: validate through the compatibility slow path and repair the
        // index without making normal drag frames scan MapPage.subNodes.
        if (!WorldMapGpuScene.TryFindRoomPanelCore(page, roomIndex, out panel) || panel == null)
            return false;
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
}
