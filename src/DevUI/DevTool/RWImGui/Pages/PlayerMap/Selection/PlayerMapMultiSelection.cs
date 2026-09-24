using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class PlayerMapMultiSelection
{
    private static readonly HashSet<int> Selection = new();
    internal static HashSet<int> SelectionSet => Selection;
    private static readonly Dictionary<int, Vector2> DragStartPositions = new();

    private static ManualLogSource log;
    private static bool enabled;

    private static string region = string.Empty;
    private static int lastObservedInspectorRoom = int.MinValue;
    private static int expectedInspectorRoom = int.MinValue;
    private static bool groupDragging;
    private static int dragAnchorRoom = -1;
    private static Vector2 dragStartMouseCanon;
    private static Vector2 dragDelta;
    private static bool boxSelecting;
    private static bool boxAdditive;
    private static Vector2 boxStartCanon;
    private static Vector2 boxEndCanon;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map multi-selection/group movement enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        ResetSelection();
        enabled = false;
        log = null;
    }

    internal static bool TryGetPreviewPosition(int roomIndex, out Vector2 position)
    {
        if (groupDragging && DragStartPositions.TryGetValue(roomIndex, out Vector2 start))
        {
            position = start + dragDelta;
            return true;
        }
        position = default;
        return false;
    }

    internal static void DrawOverlay(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        if (!enabled || snapshot?.Available != true)
            return;

        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        bool[] layers = PlayerMapWorkspaceView.LayerVisibility;
        if (zoom <= 0f || float.IsNaN(zoom) || float.IsInfinity(zoom) || layers == null)
            return;

        SynchronizeRegionAndInspector(snapshot);
        uint outline = ImGui.GetColorU32(ImGuiCol.HeaderActive);
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || !Selection.Contains(room.RoomIndex) || !LayerVisible(room, layers))
                continue;

            Vector2 position = groupDragging && DragStartPositions.TryGetValue(room.RoomIndex, out Vector2 start)
                ? start + dragDelta
                : room.EffectivePosition;
            RoomScreenRect(room, position, canvasMin, pan, zoom, out Num.Vector2 min, out Num.Vector2 max);
            draw.AddRect(min, max, outline, 0f, ImDrawFlags.None, groupDragging ? 2.8f : 2.2f);
        }

        if (boxSelecting)
        {
            Num.Vector2 a = CanonToScreen(boxStartCanon, canvasMin, pan, zoom);
            Num.Vector2 b = CanonToScreen(boxEndCanon, canvasMin, pan, zoom);
            Num.Vector2 min = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            Num.Vector2 max = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.HeaderHovered) & 0x55FFFFFFu);
            draw.AddRect(min, max, outline, 0f, ImDrawFlags.None, 1.5f);
        }
    }

    internal static bool HandleInteraction(
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io)
    {
        if (!enabled || snapshot?.Available != true || PlayerMapCanvasAuthoring.OwnsCanvas)
            return false;

        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        bool[] layers = PlayerMapWorkspaceView.LayerVisibility;
        if (zoom <= 0f || float.IsNaN(zoom) || float.IsInfinity(zoom) || layers == null)
            return false;

        SynchronizeRegionAndInspector(snapshot);
        Vector2 mouseCanon = ScreenToCanon(io.MousePos, canvasMin, pan, zoom);

        if (canvasHovered && !ImGui.IsAnyItemActive())
            HandleNudge(snapshot, io);

        if (canvasHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (hoveredRoom != null && !hoveredRoom.Disabled && LayerVisible(hoveredRoom, layers))
            {
                bool shift = io.KeyShift;
                if (shift)
                {
                    if (!Selection.Add(hoveredRoom.RoomIndex))
                        Selection.Remove(hoveredRoom.RoomIndex);
                }
                else if (!Selection.Contains(hoveredRoom.RoomIndex))
                {
                    Selection.Clear();
                    Selection.Add(hoveredRoom.RoomIndex);
                }

                SetInspectorRoom(hoveredRoom.RoomIndex);
                if (Selection.Contains(hoveredRoom.RoomIndex))
                    BeginGroupDrag(snapshot, hoveredRoom.RoomIndex, mouseCanon);
                return true;
            }

            boxSelecting = true;
            boxAdditive = io.KeyShift;
            boxStartCanon = mouseCanon;
            boxEndCanon = mouseCanon;
            groupDragging = false;
            DragStartPositions.Clear();
            return true;
        }

        if (groupDragging)
        {
            Vector2 rawDelta = mouseCanon - dragStartMouseCanon;
            dragDelta = PlayerMapLayoutAssist.AdjustPreviewDelta(rawDelta);
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CommitGroupDrag();
                groupDragging = false;
                dragAnchorRoom = -1;
                DragStartPositions.Clear();
                dragDelta = Vector2.zero;
            }
            return true;
        }

        if (boxSelecting)
        {
            boxEndCanon = mouseCanon;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CommitBoxSelection(snapshot, layers);
                boxSelecting = false;
            }
            return true;
        }

        return canvasHovered;
    }

    private static void BeginGroupDrag(PlayerMapPresentationSnapshot snapshot, int anchorRoom, Vector2 mouseCanon)
    {
        DragStartPositions.Clear();
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room != null && !room.Disabled && Selection.Contains(room.RoomIndex))
                DragStartPositions[room.RoomIndex] = room.EffectivePosition;
        }
        if (DragStartPositions.Count == 0) return;
        groupDragging = true;
        dragAnchorRoom = anchorRoom;
        dragStartMouseCanon = mouseCanon;
        dragDelta = Vector2.zero;
    }

    private static void CommitGroupDrag()
    {
        if (DragStartPositions.Count == 0 || dragDelta.sqrMagnitude <= 0.0001f) return;
        int[] rooms = new int[DragStartPositions.Count];
        Vector2[] positions = new Vector2[DragStartPositions.Count];
        int index = 0;
        foreach (KeyValuePair<int, Vector2> pair in DragStartPositions)
        {
            rooms[index] = pair.Key;
            positions[index] = pair.Value + dragDelta;
            index++;
        }
        Array.Sort(rooms, positions);
        PlayerMapGroupCommandQueue.Enqueue(new PlayerMapGroupMoveCommand(
            rooms,
            positions,
            rooms.Length == 1 ? "Move player-map room" : "Move player-map rooms"));
    }

    private static void CommitBoxSelection(PlayerMapPresentationSnapshot snapshot, bool[] layers)
    {
        float left = Math.Min(boxStartCanon.x, boxEndCanon.x);
        float right = Math.Max(boxStartCanon.x, boxEndCanon.x);
        float bottom = Math.Min(boxStartCanon.y, boxEndCanon.y);
        float top = Math.Max(boxStartCanon.y, boxEndCanon.y);
        if (!boxAdditive) Selection.Clear();

        int inspector = -1;
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || !LayerVisible(room, layers)) continue;
            float halfW = Math.Max(1, room.Bake?.Width ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float halfH = Math.Max(1, room.Bake?.Height ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float roomLeft = room.EffectivePosition.x - halfW;
            float roomRight = room.EffectivePosition.x + halfW;
            float roomBottom = room.EffectivePosition.y - halfH;
            float roomTop = room.EffectivePosition.y + halfH;
            if (roomLeft > right || roomRight < left || roomBottom > top || roomTop < bottom) continue;
            Selection.Add(room.RoomIndex);
            if (inspector < 0) inspector = room.RoomIndex;
        }

        if (inspector >= 0) SetInspectorRoom(inspector);
    }

    private static void HandleNudge(PlayerMapPresentationSnapshot snapshot, ImGuiIOPtr io)
    {
        int dx = 0;
        int dy = 0;
        if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) dx--;
        if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) dx++;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) dy--;
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) dy++;
        if (dx == 0 && dy == 0) return;

        if (Selection.Count == 0 && snapshot.SelectedRoomIndex >= 0)
            Selection.Add(snapshot.SelectedRoomIndex);
        if (Selection.Count == 0) return;

        // Canon coordinates are the 3x editor-space representation. One final Render Map pixel is
        // therefore CanonPixelsPerTile units; Shift nudges ten final pixels.
        float step = PlayerMapCoordinateSystem.CanonPixelsPerTile * (io.KeyShift ? 10f : 1f);
        Vector2 delta = new(dx * step, dy * step);
        List<int> roomIds = new();
        List<Vector2> positions = new();
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || !Selection.Contains(room.RoomIndex)) continue;
            roomIds.Add(room.RoomIndex);
            positions.Add(room.EffectivePosition + delta);
        }
        if (roomIds.Count == 0) return;

        int[] ids = roomIds.ToArray();
        Vector2[] values = positions.ToArray();
        Array.Sort(ids, values);
        PlayerMapGroupCommandQueue.Enqueue(new PlayerMapGroupMoveCommand(
            ids,
            values,
            ids.Length == 1 ? "Nudge player-map room" : "Nudge player-map rooms"));

        string direction =
            dx < 0 ? DevToolGlyphs.ArrowLeft :
            dx > 0 ? DevToolGlyphs.ArrowRight :
            dy < 0 ? DevToolGlyphs.ArrowDown : DevToolGlyphs.ArrowUp;
        string keys =
            (io.KeyShift ? "Shift+" : string.Empty) +
            direction;
        EditorShortcutFeedback.PublishCustom(
            ids.Length == 1 ? "微调玩家地图房间" : "微调玩家地图房间组",
            ids.Length == 1 ? "Nudge Player Map room" : "Nudge Player Map rooms",
            keys,
            true,
            EditorShortcutFeedbackVisual.Move);
    }

    private static void SynchronizeRegionAndInspector(PlayerMapPresentationSnapshot snapshot)
    {
        string nextRegion = snapshot.RegionName ?? string.Empty;
        if (!string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            ResetSelection();
            region = nextRegion;
        }

        int observed = snapshot.SelectedRoomIndex;
        if (observed == lastObservedInspectorRoom) return;
        lastObservedInspectorRoom = observed;
        if (observed == expectedInspectorRoom)
        {
            expectedInspectorRoom = int.MinValue;
            return;
        }

        // Selection coming from Explorer/another workspace becomes a new single selection.
        Selection.Clear();
        if (observed >= 0) Selection.Add(observed);
    }

    private static void SetInspectorRoom(int roomIndex)
    {
        expectedInspectorRoom = roomIndex;
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(MapEditorCommandKind.SelectRoom, roomIndex: roomIndex));
    }

    private static bool LayerVisible(PlayerMapRoomSnapshot room, bool[] layers)
    {
        int layer = Math.Max(0, Math.Min(PlayerMapCoordinateSystem.LayerCount - 1, room.Layer));
        return layers != null && layer < layers.Length && layers[layer];
    }

    private static void RoomScreenRect(
        PlayerMapRoomSnapshot room,
        Vector2 position,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        int width = Math.Max(1, room.Bake?.Width ?? 1);
        int height = Math.Max(1, room.Bake?.Height ?? 1);
        Num.Vector2 center = CanonToScreen(position, canvasMin, pan, zoom);
        Num.Vector2 half = new(
            width * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f,
            height * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f);
        min = center - half;
        max = center + half;
    }

    private static Vector2 ScreenToCanon(
        Num.Vector2 screen,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom)
    {
        Num.Vector2 local = (screen - canvasMin - pan) / Math.Max(0.0001f, zoom);
        return new Vector2(local.X, local.Y);
    }

    private static Num.Vector2 CanonToScreen(
        Vector2 point,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom) =>
        canvasMin + pan + new Num.Vector2(point.x, point.y) * zoom;

    private static void ResetSelection()
    {
        Selection.Clear();
        DragStartPositions.Clear();
        region = string.Empty;
        lastObservedInspectorRoom = int.MinValue;
        expectedInspectorRoom = int.MinValue;
        groupDragging = false;
        dragAnchorRoom = -1;
        dragStartMouseCanon = default;
        dragDelta = default;
        boxSelecting = false;
        boxAdditive = false;
        boxStartCanon = default;
        boxEndCanon = default;
    }

}

/// <summary>
/// Narrow adapter around PlayerMapMultiSelection's retained selection set.
/// The multi-selection controller remains the owner; this type only exposes a filtered snapshot view.
/// </summary>
internal static class PlayerMapSelectionAccess
{
    private static HashSet<int> selection;
    private static string region = string.Empty;

    internal static bool Available => selection != null;

    internal static void Enable(ManualLogSource logger)
    {
        selection ??= PlayerMapMultiSelection.SelectionSet;
    }

    internal static void Disable()
    {
        selection = null;
        region = string.Empty;
    }

    internal static List<PlayerMapRoomSnapshot> Collect(PlayerMapPresentationSnapshot snapshot)
    {
        List<PlayerMapRoomSnapshot> result = new();
        if (selection == null || snapshot?.Available != true) return result;

        NormalizeRegion(snapshot);
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled) continue;
            bool selectedByGroup = selection.Contains(room.RoomIndex);
            bool selectedByInspector = selection.Count == 0 && room.RoomIndex == snapshot.SelectedRoomIndex;
            if (selectedByGroup || selectedByInspector)
                result.Add(room);
        }
        result.Sort((a, b) => a.RoomIndex.CompareTo(b.RoomIndex));
        return result;
    }

    private static void NormalizeRegion(PlayerMapPresentationSnapshot snapshot)
    {
        string next = snapshot.RegionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;
        region = next;
        selection.Clear();
        if (snapshot.SelectedRoomIndex >= 0)
            selection.Add(snapshot.SelectedRoomIndex);
    }
}
