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

    /// <summary>
    /// Returns the complete source signature used by the retained scene. The service owns both the
    /// O(1) stable-frame cache and the slow vanilla texture audit. RuntimeDetour original-method
    /// callbacks are deliberately not part of this boundary.
    /// </summary>
    internal static int ComputeSourceHash(MapPage page, WorldMapGpuScene.FrameState frame)
    {
        if (frame?.Snapshot?.Available != true)
            return 0;

        EditorMapPresentationSnapshot snapshot = frame.Snapshot;
        int generation = WorldMapGpuCache.Generation;
        bool snapshotChanged = !ReferenceEquals(cachedSourceSnapshot, snapshot);
        bool generationChanged = cachedSourceGeneration != generation;
        bool auditDue = Time.frameCount >= nextSourceAuditFrame;

        if (!snapshotChanged && !generationChanged && !auditDue)
            return cachedSourceHash;

        int liveHash = ComputeLiveSourceHash(page, frame);
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
        if (page == null) return false;

        EnsurePanelIndex(page);
        if (panelIndex.TryGetValue(roomIndex, out panel) && panel?.roomRep?.room?.index == roomIndex)
            return true;

        // Same-count replacement is rare, but third-party code can still replace vanilla nodes
        // without participating in DryCycle invalidation. Repair the index through one slow scan.
        if (!TryFindRoomPanelSlow(page, roomIndex, out panel))
            return false;

        panelIndex[roomIndex] = panel;
        return true;
    }

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

    private static int ComputeLiveSourceHash(MapPage page, WorldMapGpuScene.FrameState frame)
    {
        unchecked
        {
            int hash = 17;
            EditorMapRoomSnapshot[] rooms = frame.Snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
            for (int i = 0; i < rooms.Length; i++)
            {
                EditorMapRoomSnapshot room = rooms[i];
                if (room == null) continue;
                hash = hash * 397 ^ room.RoomIndex;
                hash = hash * 397 ^ room.Layer;

                if (TryFindRoomPanel(page, room.RoomIndex, out RoomPanel panel))
                {
                    MapObject.RoomRepresentation rep = panel.roomRep;
                    FAtlasElement element = rep?.mapTex;
                    if (TryResolveRoomTextureAtlas(
                            element,
                            out Texture2D atlas,
                            out Rect sourceUv,
                            out float sourceWidth,
                            out float sourceHeight))
                    {
                        hash = hash * 397 ^ 1;
                        hash = hash * 397 ^ (element.name?.GetHashCode() ?? 0);
                        hash = hash * 397 ^ atlas.GetInstanceID();
                        hash = hash * 397 ^ atlas.width;
                        hash = hash * 397 ^ atlas.height;
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceWidth * 1000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceHeight * 1000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.x * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.y * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.width * 1000000f);
                        hash = hash * 397 ^ Mathf.RoundToInt(sourceUv.height * 1000000f);
                    }
                    else if (rep?.texture != null)
                    {
                        Texture2D direct = rep.texture;
                        hash = hash * 397 ^ 2;
                        hash = hash * 397 ^ direct.GetInstanceID();
                        hash = hash * 397 ^ direct.width;
                        hash = hash * 397 ^ direct.height;
                    }
                    else
                    {
                        hash = hash * 397;
                    }
                }

                if (WorldMapGpuCache.TryGetRoom(room.RoomIndex, out WorldMapGpuCache.RoomBake bake))
                {
                    hash = hash * 397 ^ bake.SourceSignature.GetHashCode();
                    hash = hash * 397 ^ (bake.GeometryReady ? 1 : 0);
                }
            }
            return hash;
        }
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

    private static bool TryFindRoomPanelSlow(MapPage page, int roomIndex, out RoomPanel panel)
    {
        panel = null;
        if (page?.subNodes == null) return false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel candidate || candidate.roomRep?.room?.index != roomIndex)
                continue;
            panel = candidate;
            return true;
        }
        return false;
    }

    private static bool TryResolveRoomTextureAtlas(
        FAtlasElement element,
        out Texture2D texture,
        out Rect uv,
        out float width,
        out float height)
    {
        texture = null;
        uv = new Rect(0f, 0f, 1f, 1f);
        width = 0f;
        height = 0f;

        if (element?.atlas?.texture is not Texture2D atlas || atlas == null)
            return false;

        Rect candidateUv = element.uvRect;
        float uvWidth = Math.Abs(candidateUv.width);
        float uvHeight = Math.Abs(candidateUv.height);
        if (uvWidth <= 0.000001f || uvHeight <= 0.000001f)
            return false;

        float sampledWidth = uvWidth * Math.Max(1, atlas.width);
        float sampledHeight = uvHeight * Math.Max(1, atlas.height);
        if (sampledWidth < 0.5f || sampledHeight < 0.5f)
            return false;

        texture = atlas;
        uv = candidateUv;
        width = Math.Max(1f, element.sourcePixelSize.x > 0.5f ? element.sourcePixelSize.x : sampledWidth);
        height = Math.Max(1f, element.sourcePixelSize.y > 0.5f ? element.sourcePixelSize.y : sampledHeight);
        return true;
    }
}
