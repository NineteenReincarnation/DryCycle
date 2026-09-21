using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Routed World Map connection presentation.
///
/// This used to detour WorldMapView.DrawCanvas. It is now a normal render service called directly
/// by WorldMapView, so connection ownership and draw order are explicit instead of depending on
/// RuntimeDetour ordering. The service still owns routed geometry, route hit-testing and link
/// presentation; WorldMapView remains authoritative for rooms, shortcuts and topology commands.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldConnectionRoutingPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldConnectionRouting";
    public const string PluginName = "DryCycle DevTool World Connection Routing";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Enable(
            PluginName + ".OnEnable",
            () => WorldConnectionOverlay.Enable(Logger),
            WorldConnectionOverlay.Disable);

    private void OnDisable() =>
        global::DryCycle.AuxiliaryPluginStartupGuard.Disable(
            PluginName + ".OnDisable",
            WorldConnectionOverlay.Disable);
}

internal static class WorldConnectionOverlay
{
    private const float TileDisplaySize = 2f;
    private const float LaneSpacing = 9f;
    private const float MaxLaneOffset = 27f;
    private const float EndpointHoverRadius = 18f;
    private const float RouteHoverRadius = 12f;
    private const float CornerRadius = 10f;
    private const float TerminalGap = 8.5f;

    private sealed class Entry
    {
        internal EditorMapConnectionSnapshot Connection;
        internal EditorMapRoomSnapshot StartRoom;
        internal EditorMapRoomSnapshot EndRoom;
        internal Num.Vector2 Start;
        internal Num.Vector2 End;
        internal Num.Vector2 StartDirection;
        internal Num.Vector2 EndDirection;
        internal float LaneOffset;
        internal WorldConnectionRouter.Route Route;
        internal Num.Vector2[] Rounded = Array.Empty<Num.Vector2>();
        internal readonly List<Crossing> Crossings = new();
    }

    private readonly struct Crossing
    {
        internal Crossing(Num.Vector2 point, Num.Vector2 tangent)
        {
            Point = point;
            Tangent = tangent;
        }

        internal Num.Vector2 Point { get; }
        internal Num.Vector2 Tangent { get; }
    }

    private static ManualLogSource log;
    private static bool enabled;

    private static FieldInfo showConnectionsField;
    private static FieldInfo panField;
    private static FieldInfo zoomField;
    private static FieldInfo localPositionsField;
    private static FieldInfo layerVisibleField;
    private static FieldInfo selectedConnectionIdField;
    private static FieldInfo hoveredConnectionIdField;
    private static FieldInfo draggingRoomField;
    private static FieldInfo linkingRoomField;

    internal static bool Ready => enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type mapType = typeof(WorldMapView);

            showConnectionsField = mapType.GetField("showConnections", flags);
            panField = mapType.GetField("pan", flags);
            zoomField = mapType.GetField("zoom", flags);
            localPositionsField = mapType.GetField("localPositions", flags);
            layerVisibleField = mapType.GetField("layerVisible", flags);
            selectedConnectionIdField = mapType.GetField("selectedConnectionId", flags);
            hoveredConnectionIdField = mapType.GetField("hoveredConnectionId", flags);
            draggingRoomField = mapType.GetField("draggingRoom", flags);
            linkingRoomField = mapType.GetField("linkingRoom", flags);

            if (showConnectionsField == null || panField == null || zoomField == null ||
                localPositionsField == null || layerVisibleField == null || selectedConnectionIdField == null ||
                hoveredConnectionIdField == null || draggingRoomField == null || linkingRoomField == null)
                throw new MissingMemberException("WorldMapView members required by connection routing were not found.");

            enabled = true;
            log?.LogInfo("World Map routed connection service enabled without a DrawCanvas detour.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map routed connection service could not initialize: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        WorldConnectionRouter.Clear();
        showConnectionsField = null;
        panField = null;
        zoomField = null;
        localPositionsField = null;
        layerVisibleField = null;
        selectedConnectionIdField = null;
        hoveredConnectionIdField = null;
        draggingRoomField = null;
        linkingRoomField = null;
        enabled = false;
        log = null;
    }

    internal static void DrawRoutedLayer(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 canvasMax,
        bool mapWindowHovered)
    {
        if (!enabled || snapshot?.Available != true) return;
        if (!(showConnectionsField?.GetValue(null) is bool showConnections) || !showConnections) return;

        float zoom = zoomField?.GetValue(null) is float z ? z : 1f;
        Num.Vector2 pan = panField?.GetValue(null) is Num.Vector2 p ? p : Num.Vector2.Zero;
        Dictionary<int, Num.Vector2> localPositions =
            localPositionsField?.GetValue(null) as Dictionary<int, Num.Vector2>;
        bool[] layerVisible = layerVisibleField?.GetValue(null) as bool[];

        List<WorldConnectionRouter.Obstacle> obstacles = BuildObstacles(
            snapshot,
            canvasMin,
            pan,
            zoom,
            localPositions,
            layerVisible);
        List<Entry> entries = BuildEntries(
            snapshot,
            canvasMin,
            pan,
            zoom,
            localPositions,
            layerVisible);
        if (entries.Count == 0)
        {
            hoveredConnectionIdField?.SetValue(null, string.Empty);
            return;
        }

        AssignLanes(entries);
        BuildRoutes(entries, obstacles);
        BuildCrossings(entries);

        ImGuiIOPtr io = ImGui.GetIO();
        bool mouseInsideCanvas =
            io.MousePos.X >= canvasMin.X && io.MousePos.X <= canvasMax.X &&
            io.MousePos.Y >= canvasMin.Y && io.MousePos.Y <= canvasMax.Y;
        bool canInteract = mapWindowHovered && mouseInsideCanvas;

        string selectedId = selectedConnectionIdField?.GetValue(null) as string ?? string.Empty;
        Entry endpointHover = canInteract ? FindEndpointHover(entries, io.MousePos) : null;
        Entry routeHover = canInteract && endpointHover == null ? FindRouteHover(entries, io.MousePos) : null;
        Entry hovered = endpointHover ?? routeHover;
        string hoveredId = hovered?.Connection?.ConnectionId ?? string.Empty;
        hoveredConnectionIdField?.SetValue(null, hoveredId);

        string focusId = !string.IsNullOrEmpty(hoveredId) ? hoveredId : selectedId;
        bool hasFocus = !string.IsNullOrEmpty(focusId);
        bool linkCreationActive = linkingRoomField?.GetValue(null) is int activeLinkRoom && activeLinkRoom >= 0;

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(canvasMin, canvasMax, true);
        try
        {
            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                bool selected = string.Equals(selectedId, entry.Connection.ConnectionId, StringComparison.Ordinal);
                bool hover = string.Equals(hoveredId, entry.Connection.ConnectionId, StringComparison.Ordinal);
                if (selected || hover) continue;
                DrawEntry(draw, entry, dimmed: hasFocus || linkCreationActive, focused: false, selected: false);
            }

            for (int i = 0; i < entries.Count; i++)
                DrawConnectedEndpointMarks(draw, entries[i], hasFocus || linkCreationActive ? 0.24f : 0.58f);

            for (int i = 0; i < entries.Count; i++)
            {
                Entry entry = entries[i];
                bool selected = string.Equals(selectedId, entry.Connection.ConnectionId, StringComparison.Ordinal);
                bool hover = string.Equals(hoveredId, entry.Connection.ConnectionId, StringComparison.Ordinal);
                if (!selected && !hover) continue;
                DrawEntry(draw, entry, dimmed: false, focused: true, selected: selected);
                DrawEndpointFocus(draw, entry, selected ? 1f : 0.88f);
            }
        }
        finally
        {
            draw.PopClipRect();
        }

        if (canInteract && hovered != null)
            DrawConnectionTooltip(snapshot.RegionName, hovered);

        if (canInteract)
            HandleRouteClick(snapshot, hovered, io);
    }

    private static List<WorldConnectionRouter.Obstacle> BuildObstacles(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Dictionary<int, Num.Vector2> localPositions,
        bool[] layerVisible)
    {
        EditorMapRoomSnapshot[] rooms = snapshot.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        List<WorldConnectionRouter.Obstacle> result = new(rooms.Length);
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (!IsLayerVisible(room.Layer, layerVisible)) continue;
            GetRoomRect(room, canvasMin, pan, zoom, localPositions, out Num.Vector2 min, out Num.Vector2 max);
            result.Add(new WorldConnectionRouter.Obstacle(room.RoomIndex, min, max));
        }
        return result;
    }

    private static List<Entry> BuildEntries(
        EditorMapPresentationSnapshot snapshot,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Dictionary<int, Num.Vector2> localPositions,
        bool[] layerVisible)
    {
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        List<Entry> result = new(connections.Length);
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            EditorMapRoomSnapshot startRoom = FindRoom(snapshot, connection.FromRoomIndex);
            EditorMapRoomSnapshot endRoom = FindRoom(snapshot, connection.ToRoomIndex);
            if (startRoom == null || endRoom == null ||
                !IsLayerVisible(startRoom.Layer, layerVisible) || !IsLayerVisible(endRoom.Layer, layerVisible))
                continue;

            GetRoomRect(startRoom, canvasMin, pan, zoom, localPositions, out Num.Vector2 startMin, out Num.Vector2 startMax);
            GetRoomRect(endRoom, canvasMin, pan, zoom, localPositions, out Num.Vector2 endMin, out Num.Vector2 endMax);

            Num.Vector2 start = EndpointPosition(startRoom, connection.FromNodeIndex, canvasMin, pan, zoom, localPositions);
            Num.Vector2 end = connection.ToNodeIndex >= 0
                ? EndpointPosition(endRoom, connection.ToNodeIndex, canvasMin, pan, zoom, localPositions)
                : BoundaryToward(endMin, endMax, start);

            result.Add(new Entry
            {
                Connection = connection,
                StartRoom = startRoom,
                EndRoom = endRoom,
                Start = start,
                End = end,
                StartDirection = WorldConnectionRouter.InferPortDirection(start, startMin, startMax),
                EndDirection = WorldConnectionRouter.InferPortDirection(end, endMin, endMax)
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Connection.ConnectionId, b.Connection.ConnectionId));
        return result;
    }

    private static void AssignLanes(List<Entry> entries)
    {
        Dictionary<long, List<Entry>> groups = new();
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            int a = Math.Min(entry.Connection.FromRoomIndex, entry.Connection.ToRoomIndex);
            int b = Math.Max(entry.Connection.FromRoomIndex, entry.Connection.ToRoomIndex);
            long key = ((long)(uint)a << 32) | (uint)b;
            if (!groups.TryGetValue(key, out List<Entry> group))
            {
                group = new List<Entry>();
                groups.Add(key, group);
            }
            group.Add(entry);
        }

        foreach (List<Entry> group in groups.Values)
        {
            group.Sort((a, b) =>
            {
                int a0 = Math.Min(a.Connection.FromNodeIndex, a.Connection.ToNodeIndex);
                int b0 = Math.Min(b.Connection.FromNodeIndex, b.Connection.ToNodeIndex);
                int byFirst = a0.CompareTo(b0);
                if (byFirst != 0) return byFirst;
                int a1 = Math.Max(a.Connection.FromNodeIndex, a.Connection.ToNodeIndex);
                int b1 = Math.Max(b.Connection.FromNodeIndex, b.Connection.ToNodeIndex);
                int bySecond = a1.CompareTo(b1);
                return bySecond != 0 ? bySecond : string.CompareOrdinal(a.Connection.ConnectionId, b.Connection.ConnectionId);
            });

            float center = (group.Count - 1) * 0.5f;
            for (int i = 0; i < group.Count; i++)
                group[i].LaneOffset = Clamp((i - center) * LaneSpacing, -MaxLaneOffset, MaxLaneOffset);
        }
    }

    private static void BuildRoutes(List<Entry> entries, List<WorldConnectionRouter.Obstacle> obstacles)
    {
        List<WorldConnectionRouter.Request> requests = new(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            requests.Add(new WorldConnectionRouter.Request
            {
                Id = entry.Connection.ConnectionId,
                StartRoom = entry.Connection.FromRoomIndex,
                EndRoom = entry.Connection.ToRoomIndex,
                Start = entry.Start,
                End = entry.End,
                StartDirection = entry.StartDirection,
                EndDirection = entry.EndDirection,
                LaneOffset = entry.LaneOffset
            });
        }

        WorldConnectionRouter.Route[] routes = WorldConnectionRouter.BuildRoutes(requests, obstacles);
        int count = Math.Min(entries.Count, routes.Length);
        for (int i = 0; i < count; i++)
        {
            entries[i].Route = routes[i];
            entries[i].Rounded = BuildRoundedPolyline(routes[i]?.Points, CornerRadius);
        }
    }

    private static void BuildCrossings(List<Entry> entries)
    {
        for (int i = 0; i < entries.Count; i++) entries[i].Crossings.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            Num.Vector2[] a = entries[i].Route?.Points;
            if (a == null || a.Length < 2) continue;
            for (int j = i + 1; j < entries.Count; j++)
            {
                Num.Vector2[] b = entries[j].Route?.Points;
                if (b == null || b.Length < 2) continue;
                FindCrossings(a, b, entries[j].Crossings);
            }
        }
    }

    private static void FindCrossings(Num.Vector2[] a, Num.Vector2[] b, List<Crossing> output)
    {
        for (int i = 0; i < a.Length - 1; i++)
        {
            Num.Vector2 ad = a[i + 1] - a[i];
            if (ad.LengthSquared() < 1f) continue;
            for (int j = 0; j < b.Length - 1; j++)
            {
                Num.Vector2 bd = b[j + 1] - b[j];
                if (bd.LengthSquared() < 1f) continue;
                if (!TrySegmentIntersection(a[i], a[i + 1], b[j], b[j + 1], out Num.Vector2 point)) continue;
                if (NearAnyEndpoint(point, a) || NearAnyEndpoint(point, b)) continue;
                if (Math.Abs(Cross(Normalize(ad), Normalize(bd))) < 0.35f) continue;
                output.Add(new Crossing(point, Normalize(bd)));
            }
        }
    }

    private static Entry FindEndpointHover(List<Entry> entries, Num.Vector2 mouse)
    {
        float thresholdSq = EndpointHoverRadius * EndpointHoverRadius;
        Entry best = null;
        float bestDistance = thresholdSq;
        for (int i = 0; i < entries.Count; i++)
        {
            float start = Num.Vector2.DistanceSquared(mouse, entries[i].Start);
            float end = Num.Vector2.DistanceSquared(mouse, entries[i].End);
            float distance = Math.Min(start, end);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = entries[i];
        }
        return best;
    }

    private static Entry FindRouteHover(List<Entry> entries, Num.Vector2 mouse)
    {
        float thresholdSq = RouteHoverRadius * RouteHoverRadius;
        Entry best = null;
        float bestDistance = thresholdSq;
        for (int i = 0; i < entries.Count; i++)
        {
            Num.Vector2[] points = entries[i].Rounded;
            for (int p = 0; p < points.Length - 1; p++)
            {
                float distance = DistanceToSegmentSquared(mouse, points[p], points[p + 1]);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = entries[i];
            }
        }
        return best;
    }

    private static void DrawEntry(
        ImDrawListPtr draw,
        Entry entry,
        bool dimmed,
        bool focused,
        bool selected)
    {
        Num.Vector2[] path = TrimEnds(entry.Rounded, TerminalGap);
        if (path.Length < 2) return;

        bool bidirectional = entry.Connection.Direction == WorldConnectionDirection.Bidirectional;
        bool ambiguous = entry.Connection.Ambiguous;
        float alpha = dimmed ? 0.24f : ambiguous ? 0.60f : 1f;
        Num.Vector4 coreColor = ResolveConnectionColor(entry.Connection.Direction, focused, selected, alpha);
        Num.Vector4 shadowColor = new(0.015f, 0.020f, 0.028f, Math.Max(0.40f, alpha * 0.94f));
        uint core = ImGui.GetColorU32(coreColor);
        uint shadow = ImGui.GetColorU32(shadowColor);

        float coreThickness = focused ? 4.4f : bidirectional ? 3.4f : 3.05f;
        float shadowThickness = coreThickness + (focused ? 6.4f : 5.0f);

        if (ambiguous)
        {
            DrawDashedPolyline(draw, path, shadow, shadowThickness, 10f, 6f);
            DrawDashedPolyline(draw, path, core, coreThickness, 10f, 6f);
        }
        else
        {
            DrawPolyline(draw, path, shadow, shadowThickness);
            DrawPolyline(draw, path, core, coreThickness);
        }

        DrawDirectionArrows(draw, path, entry.Connection.Direction, shadow, core, focused ? 8.8f : 7.4f);

        if (entry.Route?.Kind == WorldConnectionRouter.RouteKind.Bridge)
            DrawBridgeBadge(draw, path, entry.Connection.Direction, shadow, core, focused);

        if (!dimmed)
        {
            for (int i = 0; i < entry.Crossings.Count; i++)
                DrawCrossingBridge(draw, entry.Crossings[i], shadow, core, coreThickness);
        }
    }

    private static Num.Vector4 ResolveConnectionColor(
        WorldConnectionDirection direction,
        bool focused,
        bool selected,
        float alpha)
    {
        // Direction semantics are stable colors. Selection/hover changes brightness and thickness,
        // never the meaning of the color itself.
        if (direction == WorldConnectionDirection.Bidirectional)
        {
            if (selected) return new Num.Vector4(1.00f, 0.88f, 0.30f, alpha);
            if (focused) return new Num.Vector4(1.00f, 0.84f, 0.22f, alpha);
            return new Num.Vector4(0.98f, 0.72f, 0.10f, alpha);
        }

        if (selected || focused) return new Num.Vector4(1.00f, 1.00f, 1.00f, alpha);
        return new Num.Vector4(0.92f, 0.94f, 0.97f, alpha);
    }

    private static void DrawConnectedEndpointMarks(ImDrawListPtr draw, Entry entry, float alpha)
    {
        Num.Vector4 connection = ResolveConnectionColor(entry.Connection.Direction, focused: false, selected: false, alpha);
        uint color = ImGui.GetColorU32(connection);
        Num.Vector2 half = new(11.3f, 11.3f);
        draw.AddRect(entry.Start - half, entry.Start + half, color, 4.8f, ImDrawFlags.None, 1.35f);
        draw.AddRect(entry.End - half, entry.End + half, color, 4.8f, ImDrawFlags.None, 1.35f);
    }

    private static void DrawEndpointFocus(ImDrawListPtr draw, Entry entry, float alpha)
    {
        Num.Vector4 focus = ResolveConnectionColor(entry.Connection.Direction, focused: true, selected: true, alpha);
        uint color = ImGui.GetColorU32(focus);
        Num.Vector2 half = new(12.5f, 12.5f);
        draw.AddRect(entry.Start - half, entry.Start + half, color, 5f, ImDrawFlags.None, 2.2f);
        draw.AddRect(entry.End - half, entry.End + half, color, 5f, ImDrawFlags.None, 2.2f);
    }

    private static void DrawBridgeBadge(
        ImDrawListPtr draw,
        Num.Vector2[] path,
        WorldConnectionDirection direction,
        uint shadow,
        uint core,
        bool focused)
    {
        if (!TryPointAtFraction(path, 0.5f, out Num.Vector2 point, out _)) return;
        string text = direction switch
        {
            WorldConnectionDirection.AToB => "->",
            WorldConnectionDirection.BToA => "<-",
            _ => "<->"
        };
        Num.Vector2 size = ImGui.CalcTextSize(text);
        Num.Vector2 pad = new(focused ? 6f : 5f, 3f);
        draw.AddRectFilled(point - size * 0.5f - pad, point + size * 0.5f + pad, shadow, 5f);
        draw.AddRect(point - size * 0.5f - pad, point + size * 0.5f + pad, core, 5f, ImDrawFlags.None, focused ? 1.8f : 1.3f);
        draw.AddText(point - size * 0.5f, core, text);
    }

    private static void DrawCrossingBridge(
        ImDrawListPtr draw,
        Crossing crossing,
        uint shadow,
        uint core,
        float coreThickness)
    {
        Num.Vector2 tangent = Normalize(crossing.Tangent);
        if (tangent.LengthSquared() < 0.5f) return;
        Num.Vector2 normal = new(-tangent.Y, tangent.X);
        float radius = 7f;
        float rise = 5.5f;
        Num.Vector2 a = crossing.Point - tangent * radius;
        Num.Vector2 b = crossing.Point - tangent * 2.4f + normal * rise;
        Num.Vector2 c = crossing.Point + tangent * 2.4f + normal * rise;
        Num.Vector2 d = crossing.Point + tangent * radius;

        draw.AddLine(a - tangent * 1.5f, d + tangent * 1.5f, ImGui.GetColorU32(ImGuiCol.WindowBg), coreThickness + 6.5f);
        DrawBridgeSegments(draw, a, b, c, d, shadow, coreThickness + 5.2f);
        DrawBridgeSegments(draw, a, b, c, d, core, coreThickness);
    }

    private static void DrawBridgeSegments(
        ImDrawListPtr draw,
        Num.Vector2 a,
        Num.Vector2 b,
        Num.Vector2 c,
        Num.Vector2 d,
        uint color,
        float thickness)
    {
        Num.Vector2 midpoint = (b + c) * 0.5f;
        Num.Vector2 previous = a;
        for (int i = 1; i <= 8; i++)
        {
            float t = i / 8f;
            Num.Vector2 point;
            if (t < 0.5f)
            {
                float u = t * 2f;
                point = Quadratic(a, b, midpoint, u);
            }
            else
            {
                float u = (t - 0.5f) * 2f;
                point = Quadratic(midpoint, c, d, u);
            }
            draw.AddLine(previous, point, color, thickness);
            previous = point;
        }
    }

    private static void DrawDirectionArrows(
        ImDrawListPtr draw,
        Num.Vector2[] path,
        WorldConnectionDirection direction,
        uint shadow,
        uint core,
        float size)
    {
        if (path.Length < 2 || WorldConnectionRouter.PathLength(path) < 28f) return;

        switch (direction)
        {
            case WorldConnectionDirection.AToB:
                DrawArrowAt(draw, path, 0.58f, false, shadow, core, size);
                break;
            case WorldConnectionDirection.BToA:
                DrawArrowAt(draw, path, 0.42f, true, shadow, core, size);
                break;
            default:
                // Yellow bidirectional links carry exactly one arrow for each travel direction.
                DrawArrowAt(draw, path, 0.40f, false, shadow, core, size * 0.94f);
                DrawArrowAt(draw, path, 0.60f, true, shadow, core, size * 0.94f);
                break;
        }
    }

    private static void DrawArrowAt(
        ImDrawListPtr draw,
        Num.Vector2[] path,
        float fraction,
        bool reverse,
        uint shadow,
        uint core,
        float size)
    {
        if (!TryPointAtFraction(path, fraction, out Num.Vector2 point, out Num.Vector2 tangent)) return;
        if (reverse) tangent = -tangent;
        tangent = Normalize(tangent);
        if (tangent.LengthSquared() < 0.5f) return;
        Num.Vector2 normal = new(-tangent.Y, tangent.X);
        DrawArrowTriangle(draw, point, tangent, normal, shadow, size + 2.4f);
        DrawArrowTriangle(draw, point, tangent, normal, core, size);
    }

    private static void DrawArrowTriangle(
        ImDrawListPtr draw,
        Num.Vector2 tip,
        Num.Vector2 forward,
        Num.Vector2 normal,
        uint color,
        float size)
    {
        Num.Vector2 baseCenter = tip - forward * size;
        float wing = size * 0.58f;
        draw.AddTriangleFilled(tip, baseCenter + normal * wing, baseCenter - normal * wing, color);
    }

    private static void DrawPolyline(ImDrawListPtr draw, Num.Vector2[] points, uint color, float thickness)
    {
        for (int i = 0; i < points.Length - 1; i++)
            draw.AddLine(points[i], points[i + 1], color, thickness);
    }

    private static void DrawDashedPolyline(
        ImDrawListPtr draw,
        Num.Vector2[] points,
        uint color,
        float thickness,
        float dash,
        float gap)
    {
        float phase = 0f;
        for (int i = 0; i < points.Length - 1; i++)
        {
            Num.Vector2 a = points[i];
            Num.Vector2 b = points[i + 1];
            Num.Vector2 delta = b - a;
            float length = delta.Length();
            if (length < 0.01f) continue;
            Num.Vector2 direction = delta / length;
            float cursor = -phase;
            while (cursor < length)
            {
                float start = Math.Max(0f, cursor);
                float end = Math.Min(length, cursor + dash);
                if (end > start)
                    draw.AddLine(a + direction * start, a + direction * end, color, thickness);
                cursor += dash + gap;
            }
            phase = (phase + length) % (dash + gap);
        }
    }

    private static Num.Vector2[] BuildRoundedPolyline(Num.Vector2[] raw, float radius) =>
        WorldMapPerformance.BuildRoundedPolyline(raw, radius);

    internal static Num.Vector2[] BuildRoundedPolylineCore(Num.Vector2[] raw, float radius)
    {
        raw = WorldConnectionRouter.Simplify(raw);
        if (raw == null || raw.Length < 3) return raw ?? Array.Empty<Num.Vector2>();

        List<Num.Vector2> output = new(raw.Length * 4) { raw[0] };
        for (int i = 1; i < raw.Length - 1; i++)
        {
            Num.Vector2 previous = raw[i - 1];
            Num.Vector2 corner = raw[i];
            Num.Vector2 next = raw[i + 1];
            Num.Vector2 incoming = corner - previous;
            Num.Vector2 outgoing = next - corner;
            float inLength = incoming.Length();
            float outLength = outgoing.Length();
            if (inLength < 0.5f || outLength < 0.5f)
            {
                output.Add(corner);
                continue;
            }

            Num.Vector2 inDirection = incoming / inLength;
            Num.Vector2 outDirection = outgoing / outLength;
            if (Math.Abs(Cross(inDirection, outDirection)) < 0.04f)
            {
                output.Add(corner);
                continue;
            }

            float trim = Math.Min(radius, Math.Min(inLength * 0.34f, outLength * 0.34f));
            Num.Vector2 enter = corner - inDirection * trim;
            Num.Vector2 exit = corner + outDirection * trim;
            output.Add(enter);
            for (int step = 1; step <= 4; step++)
            {
                float t = step / 4f;
                output.Add(Quadratic(enter, corner, exit, t));
            }
        }
        output.Add(raw[raw.Length - 1]);
        return RemoveNearDuplicates(output);
    }

    private static Num.Vector2[] OffsetPolyline(Num.Vector2[] path, float offset) =>
        WorldMapPerformance.OffsetPolyline(path, offset);

    internal static Num.Vector2[] OffsetPolylineCore(Num.Vector2[] path, float offset)
    {
        if (path == null || path.Length < 2 || Math.Abs(offset) < 0.01f) return path ?? Array.Empty<Num.Vector2>();
        Num.Vector2[] result = new Num.Vector2[path.Length];
        for (int i = 0; i < path.Length; i++)
        {
            Num.Vector2 tangent;
            if (i == 0) tangent = path[1] - path[0];
            else if (i == path.Length - 1) tangent = path[path.Length - 1] - path[path.Length - 2];
            else tangent = Normalize(path[i] - path[i - 1]) + Normalize(path[i + 1] - path[i]);
            tangent = Normalize(tangent);
            Num.Vector2 normal = new(-tangent.Y, tangent.X);
            result[i] = path[i] + normal * offset;
        }
        return result;
    }

    private static Num.Vector2[] TrimEnds(Num.Vector2[] path, float amount) =>
        WorldMapPerformance.TrimEnds(path, amount);

    internal static Num.Vector2[] TrimEndsCore(Num.Vector2[] path, float amount)
    {
        if (path == null || path.Length < 2 || amount <= 0f) return path ?? Array.Empty<Num.Vector2>();
        float total = WorldConnectionRouter.PathLength(path);
        if (total <= amount * 2f + 2f) return path;

        List<Num.Vector2> result = new(path.Length + 2);
        float startRemaining = amount;
        int firstSegment = 0;
        Num.Vector2 first = path[0];
        for (; firstSegment < path.Length - 1; firstSegment++)
        {
            float length = Num.Vector2.Distance(path[firstSegment], path[firstSegment + 1]);
            if (length <= startRemaining)
            {
                startRemaining -= length;
                continue;
            }
            float t = length <= 0.001f ? 0f : startRemaining / length;
            first = Num.Vector2.Lerp(path[firstSegment], path[firstSegment + 1], t);
            break;
        }

        float endRemaining = amount;
        int lastSegment = path.Length - 2;
        Num.Vector2 last = path[path.Length - 1];
        for (; lastSegment >= 0; lastSegment--)
        {
            float length = Num.Vector2.Distance(path[lastSegment], path[lastSegment + 1]);
            if (length <= endRemaining)
            {
                endRemaining -= length;
                continue;
            }
            float t = length <= 0.001f ? 1f : 1f - endRemaining / length;
            last = Num.Vector2.Lerp(path[lastSegment], path[lastSegment + 1], t);
            break;
        }

        result.Add(first);
        for (int i = firstSegment + 1; i <= lastSegment; i++) result.Add(path[i]);
        result.Add(last);
        return RemoveNearDuplicates(result);
    }

    private static Num.Vector2[] RemoveNearDuplicates(List<Num.Vector2> source)
    {
        List<Num.Vector2> result = new(source.Count);
        for (int i = 0; i < source.Count; i++)
        {
            if (result.Count > 0 && Num.Vector2.DistanceSquared(result[result.Count - 1], source[i]) < 0.08f) continue;
            result.Add(source[i]);
        }
        return result.ToArray();
    }

    private static bool TryPointAtFraction(
        Num.Vector2[] path,
        float fraction,
        out Num.Vector2 point,
        out Num.Vector2 tangent)
    {
        point = default;
        tangent = default;
        if (path == null || path.Length < 2) return false;
        float total = WorldConnectionRouter.PathLength(path);
        if (total < 0.01f) return false;
        float target = Clamp(fraction, 0f, 1f) * total;
        float accumulated = 0f;
        for (int i = 0; i < path.Length - 1; i++)
        {
            Num.Vector2 delta = path[i + 1] - path[i];
            float length = delta.Length();
            if (length < 0.001f) continue;
            if (accumulated + length < target)
            {
                accumulated += length;
                continue;
            }
            float t = (target - accumulated) / length;
            point = Num.Vector2.Lerp(path[i], path[i + 1], Clamp(t, 0f, 1f));
            tangent = delta / length;
            return true;
        }
        point = path[path.Length - 1];
        tangent = Normalize(path[path.Length - 1] - path[path.Length - 2]);
        return true;
    }

    private static void DrawConnectionTooltip(string region, Entry entry)
    {
        ImGui.BeginTooltip();
        string direction = entry.Connection.Direction switch
        {
            WorldConnectionDirection.AToB => "A > B",
            WorldConnectionDirection.BToA => "A < B",
            _ => "Both"
        };
        ImGui.TextUnformatted(
            entry.StartRoom.Name + ":" + entry.Connection.FromNodeIndex + "  " + direction + "  " +
            entry.EndRoom.Name + ":" + (entry.Connection.ToNodeIndex >= 0 ? entry.Connection.ToNodeIndex.ToString() : "?"));

        DrawWorldToken(region, entry.StartRoom.Name, entry.Connection.FromNodeIndex);
        if (entry.Connection.ToNodeIndex >= 0)
            DrawWorldToken(region, entry.EndRoom.Name, entry.Connection.ToNodeIndex);
        if (entry.Connection.Ambiguous)
            ImGui.TextDisabled(DevToolUiSettings.T("目标出口不明确", "Ambiguous target exit"));
        ImGui.EndTooltip();
    }

    private static void DrawWorldToken(string region, string roomName, int nodeIndex)
    {
        if (!WorldTextRegistry.TryGetConnectionEndpoint(
                region,
                roomName,
                nodeIndex,
                out string destinationRoom,
                out int destinationNode))
            return;

        string token = destinationNode >= 0
            ? "<" + destinationNode + ">" + destinationRoom
            : destinationRoom;
        ImGui.TextDisabled(roomName + ":" + nodeIndex + " -> " + token);
    }

    private static void HandleRouteClick(EditorMapPresentationSnapshot snapshot, Entry hovered, ImGuiIOPtr io)
    {
        if (hovered?.Connection == null || io.WantTextInput || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        selectedConnectionIdField?.SetValue(null, hovered.Connection.ConnectionId);
        hoveredConnectionIdField?.SetValue(null, hovered.Connection.ConnectionId);
        draggingRoomField?.SetValue(null, -1);
        MapEditorCommandQueue.Enqueue(new MapEditorCommand(
            MapEditorCommandKind.SelectRoom,
            hovered.Connection.FromRoomIndex));
    }

    private static Num.Vector2 EndpointPosition(
        EditorMapRoomSnapshot room,
        int nodeIndex,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Dictionary<int, Num.Vector2> localPositions)
    {
        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
        Num.Vector2 roomMin = RoomScreenMin(room, canvasMin, pan, zoom, localPositions);

        if (WorldMapShortcutPresentation.TryGetExitMouth(
                room.RoomIndex,
                nodeIndex,
                out WorldMapShortcutPresentation.ShortcutMarker mouth))
            return LocalToScreen(roomMin, visual, mouth.X, mouth.Y, zoom);

        EditorMapNodeVisualSnapshot[] nodes = visual.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].NodeIndex != nodeIndex) continue;
            return LocalToScreen(roomMin, visual, nodes[i].X, nodes[i].Y, zoom);
        }

        GetRoomRect(room, canvasMin, pan, zoom, localPositions, out Num.Vector2 min, out Num.Vector2 max);
        EditorMapRoomNodeSnapshot[] roomNodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        int ordinal = 0;
        int exits = 0;
        for (int i = 0; i < roomNodes.Length; i++)
        {
            if (!roomNodes[i].Exit) continue;
            if (roomNodes[i].NodeIndex == nodeIndex) ordinal = exits;
            exits++;
        }
        if (exits <= 0) return (min + max) * 0.5f;
        bool right = ordinal % 2 == 0;
        int row = ordinal / 2;
        int rows = right ? (exits + 1) / 2 : exits / 2;
        float t = (row + 1f) / (rows + 1f);
        return new Num.Vector2(right ? max.X : min.X, min.Y + (max.Y - min.Y) * t);
    }

    private static void GetRoomRect(
        EditorMapRoomSnapshot room,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Dictionary<int, Num.Vector2> localPositions,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        EditorMapRoomVisualSnapshot visual = MapRoomGeometryPresentationHub.Get(room.RoomIndex);
        min = RoomScreenMin(room, canvasMin, pan, zoom, localPositions);
        max = min + new Num.Vector2(
            Math.Max(1f, visual.WidthTiles) * TileDisplaySize * zoom,
            Math.Max(1f, visual.HeightTiles) * TileDisplaySize * zoom);
    }

    private static Num.Vector2 RoomScreenMin(
        EditorMapRoomSnapshot room,
        Num.Vector2 canvasMin,
        Num.Vector2 pan,
        float zoom,
        Dictionary<int, Num.Vector2> localPositions)
    {
        Num.Vector2 worldPosition = localPositions != null && localPositions.TryGetValue(room.RoomIndex, out Num.Vector2 value)
            ? value
            : new Num.Vector2(room.X, room.Y);
        return canvasMin + pan + worldPosition * zoom;
    }

    private static Num.Vector2 LocalToScreen(
        Num.Vector2 roomMin,
        EditorMapRoomVisualSnapshot visual,
        float tileX,
        float tileY,
        float zoom)
    {
        float scale = TileDisplaySize * zoom;
        return new Num.Vector2(
            roomMin.X + tileX * scale,
            roomMin.Y + (visual.HeightTiles - tileY) * scale);
    }

    private static Num.Vector2 BoundaryToward(Num.Vector2 min, Num.Vector2 max, Num.Vector2 source)
    {
        Num.Vector2 center = (min + max) * 0.5f;
        Num.Vector2 direction = source - center;
        if (direction.LengthSquared() < 0.01f) return new Num.Vector2(min.X, center.Y);
        float tx = Math.Abs(direction.X) < 0.001f ? float.MaxValue : (max.X - min.X) * 0.5f / Math.Abs(direction.X);
        float ty = Math.Abs(direction.Y) < 0.001f ? float.MaxValue : (max.Y - min.Y) * 0.5f / Math.Abs(direction.Y);
        float t = Math.Min(tx, ty);
        return center + direction * t;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex) =>
        WorldMapPerformance.FindRoom(snapshot, roomIndex);

    private static EditorMapConnectionSnapshot FindConnection(EditorMapPresentationSnapshot snapshot, string id) =>
        WorldMapPerformance.FindConnection(snapshot, id);

    private static bool IsLayerVisible(int layer, bool[] visibility) =>
        visibility == null || layer < 0 || layer >= visibility.Length || visibility[layer];

    private static bool NearAnyEndpoint(Num.Vector2 point, Num.Vector2[] path)
    {
        if (path == null || path.Length == 0) return true;
        return Num.Vector2.DistanceSquared(point, path[0]) < 196f ||
               Num.Vector2.DistanceSquared(point, path[path.Length - 1]) < 196f;
    }

    private static bool TrySegmentIntersection(
        Num.Vector2 a,
        Num.Vector2 b,
        Num.Vector2 c,
        Num.Vector2 d,
        out Num.Vector2 point)
    {
        point = default;
        Num.Vector2 r = b - a;
        Num.Vector2 s = d - c;
        float denominator = Cross(r, s);
        if (Math.Abs(denominator) < 0.001f) return false;
        Num.Vector2 ca = c - a;
        float t = Cross(ca, s) / denominator;
        float u = Cross(ca, r) / denominator;
        if (t <= 0.02f || t >= 0.98f || u <= 0.02f || u >= 0.98f) return false;
        point = a + r * t;
        return true;
    }

    private static float DistanceToSegmentSquared(Num.Vector2 p, Num.Vector2 a, Num.Vector2 b)
    {
        Num.Vector2 ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq <= 0.0001f) return Num.Vector2.DistanceSquared(p, a);
        float t = Clamp(Num.Vector2.Dot(p - a, ab) / lengthSq, 0f, 1f);
        return Num.Vector2.DistanceSquared(p, a + ab * t);
    }

    private static Num.Vector2 Quadratic(Num.Vector2 a, Num.Vector2 b, Num.Vector2 c, float t)
    {
        float u = 1f - t;
        return a * (u * u) + b * (2f * u * t) + c * (t * t);
    }

    private static Num.Vector2 Normalize(Num.Vector2 value)
    {
        float length = value.Length();
        return length <= 0.0001f ? Num.Vector2.Zero : value / length;
    }

    private static float Cross(Num.Vector2 a, Num.Vector2 b) => a.X * b.Y - a.Y * b.X;

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
