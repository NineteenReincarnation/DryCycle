using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Map.PlayerMap;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Replaces temporary room-centre Player Map links with endpoint-aware connection rendering. Exact
/// world connections are keyed by source/target node index, so repeated pipes between the same room
/// pair remain distinct and terminate at their real shortcut mouths. Group-drag preview positions are
/// consumed directly, keeping precise pipes attached while several rooms move together.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(PlayerMapMultiSelectionPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapMultiPipeConnectionPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.PlayerMap.MultiPipeConnections";
    public const string PluginName = "DryCycle Player Map Multi-Pipe Connections";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => PlayerMapMultiPipeConnections.Enable(Logger);
    private void OnDisable() => PlayerMapMultiPipeConnections.Disable();
}

internal static class PlayerMapMultiPipeConnections
{
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        logger?.LogInfo("Player Map exact multi-pipe connection rendering enabled through direct view calls; no self-detour attached.");
    }

    internal static void Disable()
    {
        enabled = false;
        log = null;
    }

    internal static bool Draw(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        EditorMapPresentationSnapshot worldSnapshot,
        Num.Vector2 canvasMin)
    {
        if (!enabled || snapshot?.Available != true || worldSnapshot?.Connections == null)
            return false;

        Num.Vector2 pan = PlayerMapWorkspaceView.Pan;
        float zoom = PlayerMapWorkspaceView.Zoom;
        bool[] layers = PlayerMapWorkspaceView.LayerVisibility;
        int draggingRoom = PlayerMapWorkspaceView.DraggingRoom;
        Vector2 dragPreviewPosition = PlayerMapWorkspaceView.DragPreviewPosition;
        if (zoom <= 0f || float.IsNaN(zoom) || float.IsInfinity(zoom) || layers == null)
            return false;

        EditorMapConnectionSnapshot[] links = worldSnapshot.Connections;
        Dictionary<string, int> pairCounts = CountRoomPairs(links);
        uint exactColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        uint fallbackColor = ImGui.GetColorU32(ImGuiCol.Border);
        uint unresolvedColor = ImGui.GetColorU32(ImGuiCol.Text);

        for (int i = 0; i < links.Length; i++)
        {
            EditorMapConnectionSnapshot link = links[i];
            if (link == null) continue;
            PlayerMapRoomSnapshot a = FindRoom(snapshot, link.FromRoomIndex);
            PlayerMapRoomSnapshot b = FindRoom(snapshot, link.ToRoomIndex);
            if (!Visible(a, layers) || !Visible(b, layers)) continue;

            string pair = PairKey(link.FromRoomIndex, link.ToRoomIndex);
            bool repeatedPair = pairCounts.TryGetValue(pair, out int pairCount) && pairCount > 1;
            bool trustedExact = !link.Ambiguous && link.FromNodeIndex >= 0 && link.ToNodeIndex >= 0;
            Num.Vector2 pa = default;
            Num.Vector2 pb = default;
            bool exactA = trustedExact && TryEndpointScreen(
                a, link.FromNodeIndex, canvasMin, pan, zoom, draggingRoom, dragPreviewPosition, out pa);
            bool exactB = trustedExact && TryEndpointScreen(
                b, link.ToNodeIndex, canvasMin, pan, zoom, draggingRoom, dragPreviewPosition, out pb);

            if (exactA && exactB)
            {
                draw.AddLine(pa, pb, exactColor, 1.35f);
                draw.AddCircleFilled(pa, Math.Max(1.6f, 2.15f * zoom), exactColor);
                draw.AddCircleFilled(pb, Math.Max(1.6f, 2.15f * zoom), exactColor);
                continue;
            }

            if (!repeatedPair)
            {
                // Compatibility only: one unresolved legacy link cannot be confused with another
                // pipe, so a centre line remains safe. Repeated unresolved links never get this path.
                Num.Vector2 ca = RoomCenter(a, canvasMin, pan, zoom, draggingRoom, dragPreviewPosition);
                Num.Vector2 cb = RoomCenter(b, canvasMin, pan, zoom, draggingRoom, dragPreviewPosition);
                draw.AddLine(ca, cb, fallbackColor, 1f);
                continue;
            }

            if (exactA) DrawUnresolvedEndpoint(draw, pa, unresolvedColor);
            if (exactB) DrawUnresolvedEndpoint(draw, pb, unresolvedColor);
        }
        return true;
    }

    private static Dictionary<string, int> CountRoomPairs(EditorMapConnectionSnapshot[] links)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        for (int i = 0; i < links.Length; i++)
        {
            EditorMapConnectionSnapshot link = links[i];
            if (link == null) continue;
            string key = PairKey(link.FromRoomIndex, link.ToRoomIndex);
            result.TryGetValue(key, out int count);
            result[key] = count + 1;
        }
        return result;
    }

    private static string PairKey(int a, int b)
    {
        int low = Math.Min(a, b);
        int high = Math.Max(a, b);
        return low + ":" + high;
    }

    private static bool TryEndpointScreen(
        PlayerMapRoomSnapshot room,
        int nodeIndex,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        int draggingRoom,
        Vector2 dragPreviewPosition,
        out Num.Vector2 screen)
    {
        screen = default;
        if (room?.Bake == null || room.Bake.Status != RoomMapBakeStatus.Ready ||
            room.Bake.Width <= 0 || room.Bake.Height <= 0 ||
            !room.Bake.TryGetNodeAnchor(nodeIndex, out RoomMapNodeAnchorSnapshot anchor) ||
            anchor.Kind != RoomMapPixelKind.RoomExit)
            return false;

        Vector2 center = ResolveCenter(room, draggingRoom, dragPreviewPosition);
        // Room bake coordinates use Rain World's bottom-origin tile Y while ImGui screen Y grows
        // downward. Mirror only the room-local Y here; the room's global Player Map position stays
        // in the authoring coordinate system.
        Vector2 local = new(
            (anchor.EntranceX - room.Bake.Width * 0.5f) * PlayerMapCoordinateSystem.CanonPixelsPerTile,
            (room.Bake.Height * 0.5f - anchor.EntranceY) * PlayerMapCoordinateSystem.CanonPixelsPerTile);
        Vector2 point = center + local;
        screen = canvasMin + pan + new Num.Vector2(point.x, point.y) * zoom;
        return true;
    }

    private static Num.Vector2 RoomCenter(
        PlayerMapRoomSnapshot room,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        int draggingRoom,
        Vector2 dragPreviewPosition)
    {
        Vector2 center = ResolveCenter(room, draggingRoom, dragPreviewPosition);
        return canvasMin + pan + new Num.Vector2(center.x, center.y) * zoom;
    }

    private static Vector2 ResolveCenter(
        PlayerMapRoomSnapshot room,
        int draggingRoom,
        Vector2 dragPreviewPosition)
    {
        if (room != null && PlayerMapMultiSelection.TryGetPreviewPosition(room.RoomIndex, out Vector2 groupPreview))
            return groupPreview;
        return draggingRoom == room?.RoomIndex ? dragPreviewPosition : room?.EffectivePosition ?? Vector2.zero;
    }

    private static bool Visible(PlayerMapRoomSnapshot room, bool[] layers)
    {
        if (room == null || room.Disabled) return false;
        int layer = Math.Max(0, Math.Min(PlayerMapCoordinateSystem.LayerCount - 1, room.Layer));
        return layers != null && layer < layers.Length && layers[layer];
    }

    private static PlayerMapRoomSnapshot FindRoom(PlayerMapPresentationSnapshot snapshot, int roomIndex)
    {
        PlayerMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<PlayerMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static void DrawUnresolvedEndpoint(ImDrawListPtr draw, Num.Vector2 point, uint color)
    {
        const float radius = 4f;
        draw.AddLine(point + new Num.Vector2(-radius, -radius), point + new Num.Vector2(radius, radius), color, 1.4f);
        draw.AddLine(point + new Num.Vector2(-radius, radius), point + new Num.Vector2(radius, -radius), color, 1.4f);
    }

}
