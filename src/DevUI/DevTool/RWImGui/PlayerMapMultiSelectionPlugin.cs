using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Multi-room selection/box selection/group movement for the rebuilt Player Map canvas. Group moves
/// are submitted to PlayerMapGroupCommandQueue and therefore become one CompositeHistoryEntry.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapCanvasAuthoringPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(PlayerMapGroupCommandPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapMultiSelectionPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.MultiSelection";
    public const string PluginName = "DryCycle Player Map Multi Selection";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapMultiSelection.Enable(Logger);
    private void OnDisable() => PlayerMapMultiSelection.Disable();
}

internal static class PlayerMapMultiSelection
{
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
    private delegate void OrigHandleRoomInteraction(
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io);
    private delegate void HookHandleRoomInteraction(
        OrigHandleRoomInteraction orig,
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io);

    private static readonly HookDrawRooms DrawRoomsHookDelegate = DrawRoomsHook;
    private static readonly HookHandleRoomInteraction HandleRoomInteractionHookDelegate = HandleRoomInteractionHook;
    private static readonly HashSet<int> Selection = new();
    private static readonly Dictionary<int, Vector2> DragStartPositions = new();

    private static IDisposable drawRoomsHook;
    private static IDisposable interactionHook;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo layerVisibleField;
    private static FieldInfo defCreateArmedField;
    private static FieldInfo defDragKindField;
    private static FieldInfo defConsumedField;
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
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type view = typeof(PlayerMapWorkspaceView);
            MethodInfo drawRooms = view.GetMethod("DrawRooms", flags, null,
                new[]
                {
                    typeof(ImDrawListPtr), typeof(PlayerMapPresentationSnapshot), typeof(Num.Vector2),
                    typeof(PlayerMapRoomSnapshot)
                }, null);
            MethodInfo interaction = view.GetMethod("HandleRoomInteraction", flags, null,
                new[]
                {
                    typeof(PlayerMapPresentationSnapshot), typeof(bool), typeof(PlayerMapRoomSnapshot),
                    typeof(Num.Vector2), typeof(ImGuiIOPtr)
                }, null);
            panField = view.GetField("pan", flags);
            zoomField = view.GetField("zoom", flags);
            layerVisibleField = view.GetField("LayerVisible", flags);

            Type defTools = typeof(PlayerMapCanvasAuthoring);
            defCreateArmedField = defTools.GetField("createArmed", flags);
            defDragKindField = defTools.GetField("dragKind", flags);
            defConsumedField = defTools.GetField("consumedCanvasInput", flags);

            if (drawRooms == null || interaction == null || panField == null || zoomField == null ||
                layerVisibleField == null || defCreateArmedField == null || defDragKindField == null || defConsumedField == null)
                throw new MissingMemberException("Player Map multi-selection targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawRoomsHook = constructor.Invoke(new object[] { drawRooms, DrawRoomsHookDelegate }) as IDisposable;
            interactionHook = constructor.Invoke(new object[] { interaction, HandleRoomInteractionHookDelegate }) as IDisposable;
            if (drawRoomsHook == null || interactionHook == null)
                throw new InvalidOperationException("Player Map multi-selection hooks were not created.");

            enabled = true;
            log?.LogInfo("Player Map multi-selection/group movement enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map multi-selection could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref interactionHook);
        Dispose(ref drawRoomsHook);
        panField = null;
        zoomField = null;
        layerVisibleField = null;
        defCreateArmedField = null;
        defDragKindField = null;
        defConsumedField = null;
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

    private static void DrawRoomsHook(
        OrigDrawRooms orig,
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        orig(draw, snapshot, canvasMin, hovered);
        if (!enabled || snapshot?.Available != true || !TryViewState(out Num.Vector2 pan, out float zoom, out bool[] layers))
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

    private static void HandleRoomInteractionHook(
        OrigHandleRoomInteraction orig,
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io)
    {
        if (!enabled || snapshot?.Available != true || DefToolOwnsCanvas())
        {
            orig(snapshot, canvasHovered, hoveredRoom, canvasMin, io);
            return;
        }
        if (!TryViewState(out Num.Vector2 pan, out float zoom, out bool[] layers))
        {
            orig(snapshot, canvasHovered, hoveredRoom, canvasMin, io);
            return;
        }

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
                return;
            }

            // Empty canvas starts a box selection. Without Shift, replacement semantics are used at
            // mouse-up; with Shift the new hits are added to the current group.
            boxSelecting = true;
            boxAdditive = io.KeyShift;
            boxStartCanon = mouseCanon;
            boxEndCanon = mouseCanon;
            groupDragging = false;
            DragStartPositions.Clear();
            return;
        }

        if (groupDragging)
        {
            dragDelta = mouseCanon - dragStartMouseCanon;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CommitGroupDrag();
                groupDragging = false;
                dragAnchorRoom = -1;
                DragStartPositions.Clear();
                dragDelta = Vector2.zero;
            }
            return;
        }

        if (boxSelecting)
        {
            boxEndCanon = mouseCanon;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                CommitBoxSelection(snapshot, layers);
                boxSelecting = false;
            }
            return;
        }

        // Middle/right canvas panning is handled before this method by PlayerMapWorkspaceView. There
        // is no reason to call the old single-room left-drag implementation when our selection model
        // is active; group-of-one movement covers that case too.
        if (!canvasHovered)
            orig(snapshot, canvasHovered, hoveredRoom, canvasMin, io);
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

    private static bool DefToolOwnsCanvas()
    {
        try
        {
            if ((bool)defCreateArmedField.GetValue(null)) return true;
            if ((bool)defConsumedField.GetValue(null)) return true;
            object drag = defDragKindField.GetValue(null);
            return drag != null && Convert.ToInt32(drag) != 0;
        }
        catch
        {
            return false;
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
            return zoom > 0f && !float.IsNaN(zoom) && !float.IsInfinity(zoom) && layers != null;
        }
        catch (Exception error)
        {
            log?.LogDebug("Player Map multi-selection view-state read failed: " + error.Message);
            return false;
        }
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

    private static void Dispose(ref IDisposable hook)
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
