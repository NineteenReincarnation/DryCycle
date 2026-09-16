using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Editing assists for the rebuilt Player Map: output-pixel movement snapping and cached overlap
/// highlighting. Snapping is applied to movement delta, never to absolute Canon coordinates, so
/// opening an old authored map cannot silently shift its coordinate phase onto a new grid.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapMultiSelectionPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapLayoutAssistPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.LayoutAssist";
    public const string PluginName = "DryCycle Player Map Layout Assist";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapLayoutAssist.Enable(Logger);
    private void OnDisable() => PlayerMapLayoutAssist.Disable();
}

internal static class PlayerMapLayoutAssist
{
    private sealed class Diagnostics
    {
        internal string Region = string.Empty;
        internal long Revision = long.MinValue;
        internal readonly HashSet<int> OverlapRooms = new();
    }

    private delegate void OrigDrawToolbar(PlayerMapPresentationSnapshot snapshot);
    private delegate void HookDrawToolbar(OrigDrawToolbar orig, PlayerMapPresentationSnapshot snapshot);
    private delegate void OrigDrawRooms(ImDrawListPtr draw, PlayerMapPresentationSnapshot snapshot, Num.Vector2 canvasMin, PlayerMapRoomSnapshot hovered);
    private delegate void HookDrawRooms(OrigDrawRooms orig, ImDrawListPtr draw, PlayerMapPresentationSnapshot snapshot, Num.Vector2 canvasMin, PlayerMapRoomSnapshot hovered);
    private delegate void OrigGroupEnqueue(PlayerMapGroupMoveCommand command);
    private delegate void HookGroupEnqueue(OrigGroupEnqueue orig, PlayerMapGroupMoveCommand command);
    private delegate void OrigHandleRoomInteraction(PlayerMapPresentationSnapshot snapshot, bool canvasHovered, PlayerMapRoomSnapshot hoveredRoom, Num.Vector2 canvasMin, ImGuiIOPtr io);
    private delegate void HookHandleRoomInteraction(OrigHandleRoomInteraction orig, PlayerMapPresentationSnapshot snapshot, bool canvasHovered, PlayerMapRoomSnapshot hoveredRoom, Num.Vector2 canvasMin, ImGuiIOPtr io);

    private static readonly HookDrawToolbar DrawToolbarHookDelegate = DrawToolbarHook;
    private static readonly HookDrawRooms DrawRoomsHookDelegate = DrawRoomsHook;
    private static readonly HookGroupEnqueue GroupEnqueueHookDelegate = GroupEnqueueHook;
    private static readonly HookHandleRoomInteraction HandleRoomInteractionHookDelegate = HandleRoomInteractionHook;

    private static IDisposable toolbarHook;
    private static IDisposable roomsHook;
    private static IDisposable enqueueHook;
    private static IDisposable interactionHook;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo layerVisibleField;
    private static FieldInfo groupDraggingField;
    private static FieldInfo dragDeltaField;
    private static ManualLogSource log;
    private static bool enabled;
    private static int snapMode = 1; // 0=off, 1=one output pixel, 2=ten output pixels
    private static readonly Diagnostics Cached = new();

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type view = typeof(PlayerMapWorkspaceView);
            MethodInfo toolbar = view.GetMethod("DrawToolbar", flags, null,
                new[] { typeof(PlayerMapPresentationSnapshot) }, null);
            MethodInfo rooms = view.GetMethod("DrawRooms", flags, null,
                new[] { typeof(ImDrawListPtr), typeof(PlayerMapPresentationSnapshot), typeof(Num.Vector2), typeof(PlayerMapRoomSnapshot) }, null);
            MethodInfo interaction = view.GetMethod("HandleRoomInteraction", flags, null,
                new[] { typeof(PlayerMapPresentationSnapshot), typeof(bool), typeof(PlayerMapRoomSnapshot), typeof(Num.Vector2), typeof(ImGuiIOPtr) }, null);
            MethodInfo enqueue = typeof(PlayerMapGroupCommandQueue).GetMethod("Enqueue", flags, null,
                new[] { typeof(PlayerMapGroupMoveCommand) }, null);

            panField = view.GetField("pan", flags);
            zoomField = view.GetField("zoom", flags);
            layerVisibleField = view.GetField("LayerVisible", flags);
            Type selection = typeof(PlayerMapMultiSelection);
            groupDraggingField = selection.GetField("groupDragging", flags);
            dragDeltaField = selection.GetField("dragDelta", flags);

            if (toolbar == null || rooms == null || interaction == null || enqueue == null ||
                panField == null || zoomField == null || layerVisibleField == null ||
                groupDraggingField == null || dragDeltaField == null)
                throw new MissingMemberException("Player Map layout-assist targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            toolbarHook = constructor.Invoke(new object[] { toolbar, DrawToolbarHookDelegate }) as IDisposable;
            roomsHook = constructor.Invoke(new object[] { rooms, DrawRoomsHookDelegate }) as IDisposable;
            enqueueHook = constructor.Invoke(new object[] { enqueue, GroupEnqueueHookDelegate }) as IDisposable;
            interactionHook = constructor.Invoke(new object[] { interaction, HandleRoomInteractionHookDelegate }) as IDisposable;
            if (toolbarHook == null || roomsHook == null || enqueueHook == null || interactionHook == null)
                throw new InvalidOperationException("One or more Player Map layout-assist hooks were not created.");

            enabled = true;
            log?.LogInfo("Player Map movement snap and overlap highlighting enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map layout assist could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        Dispose(ref interactionHook);
        Dispose(ref enqueueHook);
        Dispose(ref roomsHook);
        Dispose(ref toolbarHook);
        panField = null;
        zoomField = null;
        layerVisibleField = null;
        groupDraggingField = null;
        dragDeltaField = null;
        Cached.Region = string.Empty;
        Cached.Revision = long.MinValue;
        Cached.OverlapRooms.Clear();
        enabled = false;
        log = null;
    }

    private static void DrawToolbarHook(OrigDrawToolbar orig, PlayerMapPresentationSnapshot snapshot)
    {
        orig(snapshot);
        if (!enabled) return;

        ImGui.SameLine(0f, 12f);
        ImGui.TextDisabled("Snap");
        ImGui.SameLine(0f, 5f);
        DrawSnapButton("Off", 0);
        ImGui.SameLine(0f, 3f);
        DrawSnapButton("1px", 1);
        ImGui.SameLine(0f, 3f);
        DrawSnapButton("10px", 2);
    }

    private static void DrawSnapButton(string label, int mode)
    {
        if (DevToolWidgets.ActionButton(
                label,
                "PlayerMapSnap" + mode,
                snapMode == mode ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
            snapMode = mode;
    }

    private static void GroupEnqueueHook(OrigGroupEnqueue orig, PlayerMapGroupMoveCommand command)
    {
        if (!enabled || snapMode == 0 || command.RoomIndices == null || command.EffectivePositions == null ||
            command.RoomIndices.Length == 0 || command.RoomIndices.Length != command.EffectivePositions.Length)
        {
            orig(command);
            return;
        }

        PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(DevToolRuntime.ActiveSession);
        PlayerMapRoomSnapshot anchor = FindRoom(snapshot, command.RoomIndices[0]);
        if (anchor == null)
        {
            orig(command);
            return;
        }

        // GroupMove guarantees a shared translation. Quantize that translation rather than the
        // destination coordinate itself, preserving legacy Canon phase and all relative offsets.
        Vector2 requestedDelta = command.EffectivePositions[0] - anchor.EffectivePosition;
        Vector2 snappedDelta = SnapDelta(requestedDelta, SnapGrid);
        Vector2[] positions = new Vector2[command.RoomIndices.Length];
        for (int i = 0; i < command.RoomIndices.Length; i++)
        {
            PlayerMapRoomSnapshot room = FindRoom(snapshot, command.RoomIndices[i]);
            positions[i] = room == null
                ? command.EffectivePositions[i]
                : room.EffectivePosition + snappedDelta;
        }

        orig(new PlayerMapGroupMoveCommand(command.RoomIndices, positions, command.Label));
    }

    private static void HandleRoomInteractionHook(
        OrigHandleRoomInteraction orig,
        PlayerMapPresentationSnapshot snapshot,
        bool canvasHovered,
        PlayerMapRoomSnapshot hoveredRoom,
        Num.Vector2 canvasMin,
        ImGuiIOPtr io)
    {
        orig(snapshot, canvasHovered, hoveredRoom, canvasMin, io);
        if (!enabled || snapMode == 0) return;

        // Multi-selection computes raw shared delta. Quantize that delta in-place so room previews
        // and exact shortcut-mouth connection previews both show the same final movement before release.
        try
        {
            if (!(bool)groupDraggingField.GetValue(null)) return;
            Vector2 delta = (Vector2)dragDeltaField.GetValue(null);
            dragDeltaField.SetValue(null, SnapDelta(delta, SnapGrid));
        }
        catch (Exception error)
        {
            log?.LogDebug("Player Map snap preview update failed: " + error.Message);
        }
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
        Diagnostics diagnostics = GetDiagnostics(snapshot);
        if (diagnostics.OverlapRooms.Count == 0 || !TryViewState(out Num.Vector2 pan, out float zoom, out bool[] layers))
            return;

        uint warning = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || !diagnostics.OverlapRooms.Contains(room.RoomIndex)) continue;
            int layer = Math.Max(0, Math.Min(2, room.Layer));
            if (layers == null || layer >= layers.Length || !layers[layer]) continue;

            Vector2 position = room.EffectivePosition;
            if (PlayerMapMultiSelection.TryGetPreviewPosition(room.RoomIndex, out Vector2 preview))
                position = preview;
            float halfW = Math.Max(1, room.Bake?.Width ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f;
            float halfH = Math.Max(1, room.Bake?.Height ?? 1) * PlayerMapCoordinateSystem.CanonPixelsPerTile * zoom * 0.5f;
            Num.Vector2 center = canvasMin + pan + new Num.Vector2(position.x, position.y) * zoom;
            draw.AddRect(
                center - new Num.Vector2(halfW, halfH),
                center + new Num.Vector2(halfW, halfH),
                warning,
                0f,
                ImDrawFlags.None,
                2.25f);
        }
    }

    private static Diagnostics GetDiagnostics(PlayerMapPresentationSnapshot snapshot)
    {
        string region = snapshot.RegionName ?? string.Empty;
        if (Cached.Revision == snapshot.Revision && string.Equals(Cached.Region, region, StringComparison.OrdinalIgnoreCase))
            return Cached;

        Cached.Region = region;
        Cached.Revision = snapshot.Revision;
        Cached.OverlapRooms.Clear();
        PlayerMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        List<(PlayerMapRoomSnapshot Room, int X, int Y, int W, int H)> rects = new();
        float minX = float.MaxValue;
        float minY = float.MaxValue;

        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || room.Bake?.Status != RoomMapBakeStatus.Ready) continue;
            float halfW = room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float halfH = room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            minX = Math.Min(minX, room.EffectivePosition.x - halfW);
            minY = Math.Min(minY, room.EffectivePosition.y - halfH);
        }
        if (minX == float.MaxValue) return Cached;

        for (int i = 0; i < rooms.Length; i++)
        {
            PlayerMapRoomSnapshot room = rooms[i];
            if (room == null || room.Disabled || room.Bake?.Status != RoomMapBakeStatus.Ready) continue;
            float left = room.EffectivePosition.x - room.Bake.Width * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            float bottom = room.EffectivePosition.y - room.Bake.Height * PlayerMapCoordinateSystem.CanonPixelsPerTile * 0.5f;
            int x = (int)((left - minX) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            int y = (int)((bottom - minY) / PlayerMapCoordinateSystem.CanonPixelsPerTile) + PlayerMapCoordinateSystem.OutputPadding;
            rects.Add((room, x, y, room.Bake.Width, room.Bake.Height));
        }

        for (int i = 0; i < rects.Count; i++)
        {
            var a = rects[i];
            int ax2 = a.X + a.W;
            int ay2 = a.Y + a.H;
            for (int j = i + 1; j < rects.Count; j++)
            {
                var b = rects[j];
                if (a.Room.Layer != b.Room.Layer) continue;
                int bx2 = b.X + b.W;
                int by2 = b.Y + b.H;
                if (a.X >= bx2 || b.X >= ax2 || a.Y >= by2 || b.Y >= ay2) continue;
                Cached.OverlapRooms.Add(a.Room.RoomIndex);
                Cached.OverlapRooms.Add(b.Room.RoomIndex);
            }
        }
        return Cached;
    }

    private static PlayerMapRoomSnapshot FindRoom(PlayerMapPresentationSnapshot snapshot, int roomIndex)
    {
        PlayerMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.RoomIndex == roomIndex) return rooms[i];
        return null;
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
        catch
        {
            return false;
        }
    }

    private static float SnapGrid => PlayerMapCoordinateSystem.CanonPixelsPerTile * (snapMode == 2 ? 10f : 1f);

    private static Vector2 SnapDelta(Vector2 delta, float grid)
    {
        if (grid <= 0f) return delta;
        return new Vector2(
            Mathf.Round(delta.x / grid) * grid,
            Mathf.Round(delta.y / grid) * grid);
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
