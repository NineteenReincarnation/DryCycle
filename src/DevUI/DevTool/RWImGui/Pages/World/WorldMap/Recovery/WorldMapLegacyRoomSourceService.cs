using System;
using System.Collections.Generic;
using DevInterface;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Compatibility boundary for the one vanilla responsibility V2 still needs: resolving a RoomPanel
/// MapTex/texture into a stable texture+UV descriptor.
///
/// This adapter owns no rendering, GPU cache or source-hash policy. Retained V2 consumes the
/// descriptor and keeps its own last-known-good resource lifetime.
/// </summary>
internal static class WorldMapLegacyRoomSourceService
{
    internal readonly struct RoomTextureSource
    {
        internal RoomTextureSource(
            Texture2D texture,
            Rect uv,
            float width,
            float height,
            int signature)
        {
            Texture = texture;
            Uv = uv;
            Width = width;
            Height = height;
            Signature = signature;
        }

        internal Texture2D Texture { get; }
        internal Rect Uv { get; }
        internal float Width { get; }
        internal float Height { get; }
        internal int Signature { get; }
    }

    private static readonly Dictionary<int, RoomPanel> PanelIndex = new();

    private static MapPage indexedPage;
    private static int indexedSubNodeCount = -1;

    internal static bool TryGetRoomTexture(
        MapPage page,
        int roomIndex,
        out RoomTextureSource source)
    {
        source = default;
        if (!TryFindRoomPanel(page, roomIndex, out RoomPanel panel) ||
            panel.roomRep == null)
            return false;

        MapObject.RoomRepresentation rep = panel.roomRep;
        FAtlasElement element = rep.mapTex;
        if (TryResolveRoomTextureAtlas(
                element,
                out Texture2D atlas,
                out Rect uv,
                out float width,
                out float height))
        {
            unchecked
            {
                int signature = 17;
                signature = signature * 397 ^ 1;
                signature = signature * 397 ^ (element.name?.GetHashCode() ?? 0);
                signature = signature * 397 ^ atlas.GetInstanceID();
                signature = signature * 397 ^ atlas.width;
                signature = signature * 397 ^ atlas.height;
                signature = signature * 397 ^ Mathf.RoundToInt(width * 1000f);
                signature = signature * 397 ^ Mathf.RoundToInt(height * 1000f);
                signature = signature * 397 ^ Mathf.RoundToInt(uv.x * 1000000f);
                signature = signature * 397 ^ Mathf.RoundToInt(uv.y * 1000000f);
                signature = signature * 397 ^ Mathf.RoundToInt(uv.width * 1000000f);
                signature = signature * 397 ^ Mathf.RoundToInt(uv.height * 1000000f);

                source = new RoomTextureSource(
                    atlas,
                    uv,
                    width,
                    height,
                    signature);
                return true;
            }
        }

        Texture2D direct = rep.texture;
        if (direct == null) return false;

        unchecked
        {
            int signature = 17;
            signature = signature * 397 ^ 2;
            signature = signature * 397 ^ direct.GetInstanceID();
            signature = signature * 397 ^ direct.width;
            signature = signature * 397 ^ direct.height;
            source = new RoomTextureSource(
                direct,
                new Rect(0f, 0f, 1f, 1f),
                Math.Max(1f, direct.width),
                Math.Max(1f, direct.height),
                signature);
            return true;
        }
    }

    internal static bool TryFindRoomPanel(
        MapPage page,
        int roomIndex,
        out RoomPanel panel)
    {
        panel = null;
        if (page == null) return false;

        EnsurePanelIndex(page);
        if (PanelIndex.TryGetValue(roomIndex, out panel) &&
            panel?.roomRep?.room?.index == roomIndex)
            return true;

        // Third-party code can replace nodes without participating in DryCycle invalidation.
        // Repair through one explicit slow scan only on a cache miss.
        if (!TryFindRoomPanelSlow(page, roomIndex, out panel))
            return false;

        PanelIndex[roomIndex] = panel;
        return true;
    }

    internal static void Reset()
    {
        PanelIndex.Clear();
        indexedPage = null;
        indexedSubNodeCount = -1;
    }

    private static void EnsurePanelIndex(MapPage page)
    {
        int count = page?.subNodes?.Count ?? 0;
        if (ReferenceEquals(indexedPage, page) &&
            indexedSubNodeCount == count)
            return;

        PanelIndex.Clear();
        indexedPage = page;
        indexedSubNodeCount = count;
        if (page?.subNodes == null) return;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel &&
                panel.roomRep?.room != null)
                PanelIndex[panel.roomRep.room.index] = panel;
        }
    }

    private static bool TryFindRoomPanelSlow(
        MapPage page,
        int roomIndex,
        out RoomPanel panel)
    {
        panel = null;
        if (page?.subNodes == null) return false;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel candidate ||
                candidate.roomRep?.room?.index != roomIndex)
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

        float sampledWidth =
            uvWidth * Math.Max(1, atlas.width);
        float sampledHeight =
            uvHeight * Math.Max(1, atlas.height);
        if (sampledWidth < 0.5f || sampledHeight < 0.5f)
            return false;

        texture = atlas;
        uv = candidateUv;
        width = Math.Max(
            1f,
            element.sourcePixelSize.x > 0.5f
                ? element.sourcePixelSize.x
                : sampledWidth);
        height = Math.Max(
            1f,
            element.sourcePixelSize.y > 0.5f
                ? element.sourcePixelSize.y
                : sampledHeight);
        return true;
    }
}
