using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Explicit compatibility boundary between the Native retained World Map and vanilla MapPage room
/// textures. Native scene code may call this service directly; MapPage/RoomPanel ownership must not
/// leak beyond this adapter. Stable frames are O(1), with a bounded audit for late vanilla texture
/// readiness that currently has no Native revision edge.
/// </summary>
internal static class WorldMapLegacyRoomSourceService
{
    private const int SourceAuditIntervalFrames = 15;
    private static readonly Dictionary<int, RoomPanel> panelIndex = new();

    private static MapPage indexedPage;
    private static int indexedSubNodeCount = -1;
    private static EditorMapPresentationSnapshot cachedSourceSnapshot;
    private static int cachedSourceGeneration = int.MinValue;
    private static int cachedSourceHash;
    private static int nextSourceAuditFrame;

    internal static int ComputeSourceHash(
        MapPage page,
        WorldMapGpuScene.FrameState frame,
        Func<MapPage, WorldMapGpuScene.FrameState, int> computeLiveHash)
    {
        if (frame?.Snapshot?.Available != true || computeLiveHash == null)
            return computeLiveHash?.Invoke(page, frame) ?? 0;

        EditorMapPresentationSnapshot snapshot = frame.Snapshot;
        int generation = WorldMapGpuCache.Generation;
        bool snapshotChanged = !ReferenceEquals(cachedSourceSnapshot, snapshot);
        bool generationChanged = cachedSourceGeneration != generation;
        bool auditDue = Time.frameCount >= nextSourceAuditFrame;

        if (!snapshotChanged && !generationChanged && !auditDue)
            return cachedSourceHash;

        int liveHash = computeLiveHash(page, frame);
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

    internal static bool TryFindRoomPanel(
        MapPage page,
        int roomIndex,
        TryFindRoomPanelFallback fallback,
        out RoomPanel panel)
    {
        panel = null;
        if (page == null)
            return fallback != null && fallback(page, roomIndex, out panel);

        EnsurePanelIndex(page);
        if (panelIndex.TryGetValue(roomIndex, out panel) && panel?.roomRep?.room?.index == roomIndex)
            return true;

        // Same-count replacement is rare, but third-party code can still replace vanilla nodes
        // without participating in DryCycle invalidation. Repair the index through the slow path.
        if (fallback == null || !fallback(page, roomIndex, out panel) || panel == null)
            return false;

        panelIndex[roomIndex] = panel;
        return true;
    }

    internal delegate bool TryFindRoomPanelFallback(MapPage page, int roomIndex, out RoomPanel panel);

    internal static void Reset()
    {
        panelIndex.Clear();
        indexedPage = null;
        indexedSubNodeCount = -1;
        cachedSourceSnapshot = null;
        cachedSourceGeneration = int.MinValue;
        cachedSourceHash = 0;
        nextSourceAuditFrame = 0;
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
