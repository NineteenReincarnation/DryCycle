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
    private delegate void OrigDrawConnections(
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        EditorMapPresentationSnapshot worldSnapshot,
        Num.Vector2 canvasMin);

    private delegate void HookDrawConnections(
        OrigDrawConnections orig,
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        EditorMapPresentationSnapshot worldSnapshot,
        Num.Vector2 canvasMin);

    private static readonly HookDrawConnections DrawConnectionsHookDelegate = DrawConnectionsHook;

    private static IDisposable drawConnectionsHook;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo layerVisibleField;
    private static FieldInfo draggingRoomField;
    private static FieldInfo dragPreviewPositionField;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type viewType = typeof(PlayerMapWorkspaceView);
            MethodInfo drawConnections = viewType.GetMethod(
                "DrawConnections",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr),
                    typeof(PlayerMapPresentationSnapshot),
                    typeof(EditorMapPresentationSnapshot),
                    typeof(Num.Vector2)
                },
                null);

            panField = viewType.GetField("pan", flags);
            zoomField = viewType.GetField("zoom", flags);
            layerVisibleField = viewType.GetField("LayerVisible", flags);
            draggingRoomField = viewType.GetField("draggingRoom", flags);
            dragPreviewPositionField = viewType.GetField("dragPreviewPosition", flags);

            if (drawConnections == null || panField == null || zoomField == null || layerVisibleField == null ||
                draggingRoomField == null || dragPreviewPositionField == null)
                throw new MissingMemberException("Player Map connection presentation targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawConnectionsHook = constructor.Invoke(new object[] { drawConnections, DrawConnectionsHookDelegate }) as IDisposable;
            if (drawConnectionsHook == null)
                throw new InvalidOperationException("Player Map multi-pipe connection hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map exact multi-pipe connection rendering enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map multi-pipe rendering could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { drawConnectionsHook?.Dispose(); }
        catch { }
        drawConnectionsHook = null;
        panField = null;
        zoomField = null;
        layerVisibleField = null;
        draggingRoomField = null;
        dragPreviewPositionField = null;
        enabled = false;
        log = null;
    }

    private static void DrawConnectionsHook(
        OrigDrawConnections orig,
        ImDrawListPtr draw,
        PlayerMapPresentationSnapshot snapshot,
        EditorMapPresentationSnapshot worldSnapshot,
        Num.Vector2 canvasMin)
    {
        if (!enabled || snapshot?.Available != true || worldSnapshot?.Connections == null ||
            !TryReadViewState(out Num.Vector2 pan, out float zoom, out bool[] layers, out int draggingRoom,
                out Vector2 dragPreviewPosition))
        {
            orig(draw, snapshot, worldSnapshot, canvasMin);
            return;
        }

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
        Vector2 local = new(
            (anchor.EntranceX - room.Bake.Width * 0.5f) * PlayerMapCoordinateSystem.CanonPixelsPerTile,
            (anchor.EntranceY - room.Bake.Height * 0.5f) * PlayerMapCoordinateSystem.CanonPixelsPerTile);
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

    private static bool TryReadViewState(
        out Num.Vector2 pan,
        out float zoom,
        out bool[] layers,
        out int draggingRoom,
        out Vector2 dragPreviewPosition)
    {
        pan = default;
        zoom = 1f;
        layers = null;
        draggingRoom = -1;
        dragPreviewPosition = default;
        try
        {
            pan = (Num.Vector2)panField.GetValue(null);
            zoom = (float)zoomField.GetValue(null);
            layers = layerVisibleField.GetValue(null) as bool[];
            draggingRoom = (int)draggingRoomField.GetValue(null);
            dragPreviewPosition = (Vector2)dragPreviewPositionField.GetValue(null);
            return zoom > 0f && !float.IsNaN(zoom) && !float.IsInfinity(zoom) && layers != null;
        }
        catch (Exception error)
        {
            log?.LogDebug("Player Map connection view-state read failed: " + error.Message);
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
