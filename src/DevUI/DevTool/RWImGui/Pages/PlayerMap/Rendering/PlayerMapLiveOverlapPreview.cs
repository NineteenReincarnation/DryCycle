using System;
using System.Collections.Generic;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Transient overlap preview used only while Player Map rooms are being group-dragged. Stable frames
/// use PlayerMapLayoutAssist's revision-cached diagnostics; this pass intentionally spends O(n²)
/// only during active manipulation so the author sees a collision before mouse-up.
/// </summary>
internal static class PlayerMapLiveOverlapPreview
{
    private readonly struct RectRecord
    {
        internal RectRecord(PlayerMapRoomSnapshot room, Vector2 position)
        {
            Room = room;
            Position = position;
            float halfW = Math.Max(1, room.Bake?.Width ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float halfH = Math.Max(1, room.Bake?.Height ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            Left = position.x - halfW;
            Right = position.x + halfW;
            Bottom = position.y - halfH;
            Top = position.y + halfH;
        }

        internal PlayerMapRoomSnapshot Room { get; }
        internal Vector2 Position { get; }
        internal float Left { get; }
        internal float Right { get; }
        internal float Bottom { get; }
        internal float Top { get; }
    }

    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map live overlap preview enabled through direct room overlay calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static void DrawOverlay(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        if (!enabled || snapshot?.Available != true) return;

        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        bool anyPreview = false;
        List<RectRecord> rects = new(rooms.Length);
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || room.Bake?.Status != RoomMapBakeStatus.Ready) continue;
            Vector2 position = room.EffectivePosition;
            if (PlayerMapMultiSelection.TryGetPreviewPosition(room.RoomIndex, out Vector2 preview))
            {
                position = preview;
                anyPreview = true;
            }
            rects.Add(new RectRecord(room, position));
        }
        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        bool[] layers = PlayerMapWorkspaceView.LayerVisibility;
        if (!anyPreview || zoom <= 0f || float.IsNaN(zoom) || float.IsInfinity(zoom) || layers == null) return;

        HashSet<int> overlaps = new();
        for (int i = 0; i < rects.Count; i++)
        {
            RectRecord a = rects[i];
            for (int j = i + 1; j < rects.Count; j++)
            {
                RectRecord b = rects[j];
                if (a.Room.Layer != b.Room.Layer) continue;
                if (a.Left >= b.Right || b.Left >= a.Right || a.Bottom >= b.Top || b.Bottom >= a.Top) continue;
                overlaps.Add(a.Room.RoomIndex);
                overlaps.Add(b.Room.RoomIndex);
            }
        }
        if (overlaps.Count == 0) return;

        uint warning = ImGui.GetColorU32(ImGuiCol.PlotHistogramHovered);
        for (int i = 0; i < rects.Count; i++)
        {
            RectRecord item = rects[i];
            if (!overlaps.Contains(item.Room.RoomIndex)) continue;
            int layer = Math.Max(0, Math.Min(2, item.Room.Layer));
            if (layers == null || layer >= layers.Length || !layers[layer]) continue;

            Num.Vector2 center = canvasMin + pan + new Num.Vector2(item.Position.x, item.Position.y) * zoom;
            float halfW = (item.Right - item.Left) * zoom * 0.5f;
            float halfH = (item.Top - item.Bottom) * zoom * 0.5f;
            Num.Vector2 min = center - new Num.Vector2(halfW, halfH);
            Num.Vector2 max = center + new Num.Vector2(halfW, halfH);
            draw.AddRectFilled(min, max, warning & 0x22FFFFFFu);
            draw.AddRect(min, max, warning, 0f, ImDrawFlags.None, 3f);
        }
    }

}
