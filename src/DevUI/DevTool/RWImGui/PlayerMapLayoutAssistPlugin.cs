using System;
using System.Collections.Generic;
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

    private static ManualLogSource log;
    private static bool enabled;
    private static int snapMode = 1; // 0=off, 1=one output pixel, 2=ten output pixels
    private static readonly Diagnostics Cached = new();

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        PlayerMapGroupCommandQueue.RegisterMoveTransformer(TransformGroupCommand);
        logger?.LogInfo("Player Map movement snap and overlap highlighting enabled through direct view/queue APIs; no self-detour attached.");
    }

    internal static void Disable()
    {
        PlayerMapGroupCommandQueue.UnregisterMoveTransformer(TransformGroupCommand);
        Cached.Region = string.Empty;
        Cached.Revision = long.MinValue;
        Cached.OverlapRooms.Clear();
        enabled = false;
        log = null;
    }

    internal static void DrawToolbar(PlayerMapPresentationSnapshot snapshot)
    {
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

    private static PlayerMapGroupMoveCommand TransformGroupCommand(PlayerMapGroupMoveCommand command)
    {
        if (!enabled || snapMode == 0 || command.RoomIndices == null || command.EffectivePositions == null ||
            command.RoomIndices.Length == 0 || command.RoomIndices.Length != command.EffectivePositions.Length)
        {
            return command;
        }

        PlayerMapPresentationSnapshot snapshot = PlayerMapWorkspaceRuntime.GetPresentation(DevToolRuntime.ActiveSession);
        PlayerMapRoomSnapshot anchor = FindRoom(snapshot, command.RoomIndices[0]);
        if (anchor == null)
        {
            return command;
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

        return new PlayerMapGroupMoveCommand(command.RoomIndices, positions, command.Label);
    }

    internal static void DrawOverlay(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        PlayerMapRoomSnapshot hovered)
    {
        if (!enabled || snapshot?.Available != true) return;
        Diagnostics diagnostics = GetDiagnostics(snapshot);
        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        bool[] layers = PlayerMapWorkspaceView.LayerVisibility;
        if (diagnostics.OverlapRooms.Count == 0 || zoom <= 0f ||
            float.IsNaN(zoom) || float.IsInfinity(zoom) || layers == null)
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

    internal static Vector2 AdjustPreviewDelta(Vector2 delta) =>
        enabled && snapMode != 0 ? SnapDelta(delta, SnapGrid) : delta;

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

    private static float SnapGrid => PlayerMapCoordinateSystem.CanonPixelsPerTile * (snapMode == 2 ? 10f : 1f);

    private static Vector2 SnapDelta(Vector2 delta, float grid)
    {
        if (grid <= 0f) return delta;
        return new Vector2(
            Mathf.Round(delta.x / grid) * grid,
            Mathf.Round(delta.y / grid) * grid);
    }

}
