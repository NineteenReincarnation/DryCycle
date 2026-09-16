using System;
using System.Collections.Generic;
using System.Reflection;
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

    private delegate void OrigDrawRooms(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered);
    private delegate void HookDrawRooms(
        OrigDrawRooms orig,
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered);

    private static readonly HookDrawRooms DrawRoomsHookDelegate = DrawRoomsHook;
    private static IDisposable drawHook;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo layerVisibleField;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type view = typeof(PlayerMapWorkspaceView);
            MethodInfo drawRooms = view.GetMethod("DrawRooms", flags, null,
                new[] { typeof(ImDrawListPtr), typeof(PlayerMapPresentationSnapshot), typeof(Num.Vector2), typeof(PlayerMapRoomSnapshot) }, null);
            panField = view.GetField("pan", flags);
            zoomField = view.GetField("zoom", flags);
            layerVisibleField = view.GetField("LayerVisible", flags);
            if (drawRooms == null || panField == null || zoomField == null || layerVisibleField == null)
                throw new MissingMemberException("Player Map live-overlap targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawHook = constructor.Invoke(new object[] { drawRooms, DrawRoomsHookDelegate }) as IDisposable;
            if (drawHook == null)
                throw new InvalidOperationException("Player Map live-overlap hook was not created.");

            enabled = true;
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map live overlap preview could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { drawHook?.Dispose(); }
        catch { }
        drawHook = null;
        panField = null;
        zoomField = null;
        layerVisibleField = null;
        enabled = false;
        log = null;
    }

    private static void DrawRoomsHook(
        OrigDrawRooms orig,
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        orig(draw, snapshot, canvasMin, hovered);
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
        if (!anyPreview || !TryViewState(out Num.Vector2 pan, out float zoom, out bool[] layers)) return;

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

    private static bool TryViewState(out Num.Vector2 pan, out float zoom, out bool[] layers)
    {
        pan = default;
        zoom = 1f;
        layers = null;
        try
        {
            pan = (Num.Vector2)panField.GetValue(null);
            zoom = (float)zoomField.GetValue(null);
            layers = layerVisibleField.GetValue(null) as bool[];
            return zoom > 0f && !float.IsNaN(zoom) && !float.IsInfinity(zoom);
        }
        catch (Exception error)
        {
            log?.LogDebug("Player Map live-overlap view state failed: " + error.Message);
            return false;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
