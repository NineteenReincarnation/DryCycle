using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using ImGuiNET;
using UnityEngine;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Performance guard for the unified World Map.
///
/// The routed-link renderer intentionally has a rich visual pipeline, but most of its derived
/// geometry is invariant while the author is simply looking at or panning the map. Cache those
/// products by source-array identity and make the router fingerprint translation-invariant so a
/// pure canvas/map pan never rebuilds A* routes. At very small overview zooms the tile raster is
/// below useful screen resolution, so non-focused rooms use a cheap overview card until the user
/// zooms in or hovers/selects them.
///
/// The core map snapshot and background MapTex/RoomSettings scanners are also deliberately kept
/// below render frequency. They are authoring data, not animation data: selection/region/current
/// room changes still refresh immediately while steady-state rebuilds are spread over frames.
///
/// Finally, hot room/connection/endpoint queries are indexed once per immutable presentation
/// snapshot. This removes the old N x M scans performed by every Exit and every routed link.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(BridgePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldConnectionRoutingPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapPerformancePlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMapPerformance";
    public const string PluginName = "DryCycle DevTool World Map Performance";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapPerformance.Enable(Logger);

    private void OnDisable() => WorldMapPerformance.Disable();
}

internal static class WorldMapPerformance
{
    private const float OverviewLodZoom = 0.42f;
    private const float CoordinateQuantization = 4f;
    private const int SnapshotIntervalFrames = 2;
    private const int PreviewPrimeIntervalFrames = 2;

    private sealed class PolylineCache
    {
        internal int SourceLength;
        internal Num.Vector2 SourceFirst;
        internal Num.Vector2 SourceLast;
        internal Num.Vector2[] Result = Array.Empty<Num.Vector2>();
    }

    private readonly struct OffsetKey : IEquatable<OffsetKey>
    {
        internal OffsetKey(Num.Vector2[] path, float offset)
        {
            Path = path;
            OffsetHash = Quantize(offset);
        }

        internal Num.Vector2[] Path { get; }
        internal int OffsetHash { get; }

        public bool Equals(OffsetKey other) =>
            ReferenceEquals(Path, other.Path) && OffsetHash == other.OffsetHash;

        public override bool Equals(object obj) => obj is OffsetKey other && Equals(other);

        public override int GetHashCode() =>
            ((Path == null ? 0 : RuntimeHelpers.GetHashCode(Path)) * 397) ^ OffsetHash;
    }

    private static readonly Dictionary<Num.Vector2[], PolylineCache> roundedCache = new();
    private static readonly Dictionary<Num.Vector2[], PolylineCache> trimmedCache = new();
    private static readonly Dictionary<OffsetKey, PolylineCache> offsetCache = new();
    private static readonly Dictionary<int, EditorMapRoomSnapshot> roomsByIndex = new();
    private static readonly Dictionary<string, EditorMapConnectionSnapshot> connectionsById =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<long, EditorMapConnectionSnapshot> connectionsByEndpoint = new();

    private static ManualLogSource log;
    private static bool enabled;

    private static bool routeCacheValid;
    private static int routeFingerprint;
    private static Num.Vector2 routeAnchor;
    private static WorldConnectionRouter.Route[] cachedRoutes = Array.Empty<WorldConnectionRouter.Route>();
    private static EditorMapPresentationSnapshot indexedSnapshot;

    private static int lastPublishFrame = -1000;
    private static string lastPublishRegion = string.Empty;
    private static int lastPublishSelection = int.MinValue;
    private static int lastPublishCurrentRoom = int.MinValue;

    private static int lastGeometryPrimeFrame = -1000;
    private static string lastGeometryRegion = string.Empty;
    private static int lastGeometrySelection = int.MinValue;
    private static int lastGeometryCurrentRoom = int.MinValue;

    private static int lastShortcutPrimeFrame = -1000;
    private static string lastShortcutRegion = string.Empty;
    private static int lastShortcutSelection = int.MinValue;
    private static int lastShortcutCurrentRoom = int.MinValue;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        ResetCaches();
        ResetThrottles();
        ClearLookupIndex();
        logger?.LogInfo("World Map performance cache enabled through direct call sites; no self-detours attached.");
    }

    internal static void Disable()
    {
        WorldMapHotState.Invalidate();
        ResetCaches();
        ResetThrottles();
        ClearLookupIndex();
        enabled = false;
        log = null;
    }

    internal static bool ShouldPublish(EditorSession session)
    {
        if (!enabled) return true;

        if (session?.ToolMode != EditorToolMode.Map)
        {
            lastPublishFrame = -1000;
            lastPublishRegion = string.Empty;
            lastPublishSelection = int.MinValue;
            lastPublishCurrentRoom = int.MinValue;
            ClearLookupIndex();
            return true;
        }

        string region = session.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        bool urgent = !MapEditorPresentationHub.Current.Available ||
                      !string.Equals(region, lastPublishRegion, StringComparison.OrdinalIgnoreCase) ||
                      selected != lastPublishSelection ||
                      currentRoom != lastPublishCurrentRoom;

        if (!urgent && Time.frameCount - lastPublishFrame < SnapshotIntervalFrames)
            return false;

        lastPublishFrame = Time.frameCount;
        lastPublishRegion = region;
        lastPublishSelection = selected;
        lastPublishCurrentRoom = currentRoom;
        return true;
    }

    internal static bool ShouldPrimeGeometry(EditorSession session)
    {
        if (!enabled) return true;

        string region = session?.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent = !string.Equals(region, lastGeometryRegion, StringComparison.OrdinalIgnoreCase) ||
                      selected != lastGeometrySelection ||
                      currentRoom != lastGeometryCurrentRoom;

        if (!urgent && Time.frameCount - lastGeometryPrimeFrame < PreviewPrimeIntervalFrames)
            return false;

        lastGeometryPrimeFrame = Time.frameCount;
        lastGeometryRegion = region;
        lastGeometrySelection = selected;
        lastGeometryCurrentRoom = currentRoom;
        return true;
    }

    internal static bool ShouldPrimeShortcuts(EditorSession session, int selectedRoomIndex)
    {
        if (!enabled) return true;

        string region = session?.World?.name ?? string.Empty;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent = !string.Equals(region, lastShortcutRegion, StringComparison.OrdinalIgnoreCase) ||
                      selectedRoomIndex != lastShortcutSelection ||
                      currentRoom != lastShortcutCurrentRoom;

        if (!urgent && Time.frameCount - lastShortcutPrimeFrame < PreviewPrimeIntervalFrames)
            return false;

        lastShortcutPrimeFrame = Time.frameCount;
        lastShortcutRegion = region;
        lastShortcutSelection = selectedRoomIndex;
        lastShortcutCurrentRoom = currentRoom;
        return true;
    }

    internal static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EnsureLookupIndex(snapshot);
        return roomsByIndex.TryGetValue(roomIndex, out EditorMapRoomSnapshot room) ? room : null;
    }

    internal static EditorMapConnectionSnapshot FindConnection(EditorMapPresentationSnapshot snapshot, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLookupIndex(snapshot);
        return connectionsById.TryGetValue(id, out EditorMapConnectionSnapshot connection) ? connection : null;
    }

    internal static EditorMapConnectionSnapshot FindConnectionAtEndpoint(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex)
    {
        EnsureLookupIndex(snapshot);
        return connectionsByEndpoint.TryGetValue(EndpointKey(roomIndex, nodeIndex), out EditorMapConnectionSnapshot connection)
            ? connection
            : null;
    }

    internal static bool IsEndpointFree(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node)
    {
        if (node == null || !node.Exit || node.ConnectedRoomIndex >= 0) return false;
        EnsureLookupIndex(snapshot);
        return !connectionsByEndpoint.ContainsKey(EndpointKey(roomIndex, node.NodeIndex));
    }

    internal static WorldConnectionRouter.Route[] BuildRoutes(
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
        if (!enabled)
            return WorldConnectionRouter.BuildRoutesCore(requests, obstacles);

        Num.Vector2 anchor = ResolveAnchor(requests, obstacles);
        int fingerprint = ComputeRouteFingerprint(requests, obstacles, anchor);
        int requestCount = requests?.Count ?? 0;

        if (routeCacheValid && routeFingerprint == fingerprint && cachedRoutes.Length == requestCount)
        {
            Num.Vector2 delta = anchor - routeAnchor;
            if (delta.LengthSquared() > 0.0001f)
                TranslateRoutes(cachedRoutes, delta);
            routeAnchor = anchor;
            return cachedRoutes;
        }

        WorldConnectionRouter.Route[] routes =
            WorldConnectionRouter.BuildRoutesCore(requests, obstacles) ?? Array.Empty<WorldConnectionRouter.Route>();
        cachedRoutes = routes;
        routeFingerprint = fingerprint;
        routeAnchor = anchor;
        routeCacheValid = true;
        ClearPolylineCaches();
        return routes;
    }

    internal static Num.Vector2[] BuildRoundedPolyline(Num.Vector2[] path, float radius)
    {
        if (!enabled || path == null || path.Length == 0)
            return WorldConnectionOverlay.BuildRoundedPolylineCore(path, radius);

        if (TryReuseTranslated(roundedCache, path, out Num.Vector2[] result))
            return result;

        result = WorldConnectionOverlay.BuildRoundedPolylineCore(path, radius) ?? Array.Empty<Num.Vector2>();
        StorePolyline(roundedCache, path, result);
        return result;
    }

    internal static Num.Vector2[] TrimEnds(Num.Vector2[] path, float amount)
    {
        if (!enabled || path == null || path.Length == 0)
            return WorldConnectionOverlay.TrimEndsCore(path, amount);

        if (TryReuseTranslated(trimmedCache, path, out Num.Vector2[] result))
            return result;

        result = WorldConnectionOverlay.TrimEndsCore(path, amount) ?? Array.Empty<Num.Vector2>();
        StorePolyline(trimmedCache, path, result);
        return result;
    }

    internal static Num.Vector2[] OffsetPolyline(Num.Vector2[] path, float offset)
    {
        if (!enabled || path == null || path.Length == 0)
            return WorldConnectionOverlay.OffsetPolylineCore(path, offset);

        OffsetKey key = new(path, offset);
        if (offsetCache.TryGetValue(key, out PolylineCache cached) &&
            TryReuseTranslated(cached, path, out Num.Vector2[] result))
            return result;

        result = WorldConnectionOverlay.OffsetPolylineCore(path, offset) ?? Array.Empty<Num.Vector2>();
        offsetCache[key] = CreatePolylineCache(path, result);
        return result;
    }

    internal static bool TryDrawOverviewRoom(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered)
    {
        if (!enabled) return false;

        float zoom = WorldMapHotState.Zoom;
        if (zoom >= OverviewLodZoom || selected || hovered || room?.CurrentRoom == true)
            return false;

        float scale = 2f * zoom;
        float width = Math.Max(1f, visual?.WidthTiles ?? 12f) * scale;
        float height = Math.Max(1f, visual?.HeightTiles ?? 6f) * scale;
        Num.Vector2 roomMax = roomMin + new Num.Vector2(width, height);

        uint fill = ImGui.GetColorU32(ImGuiCol.FrameBg);
        uint outline = ImGui.GetColorU32(
            room?.Disabled == true ? ImGuiCol.TextDisabled : ImGuiCol.Border);
        float rounding = Math.Max(1f, 2.2f * zoom);
        draw.AddRectFilled(roomMin, roomMax, fill, rounding);
        draw.AddRect(roomMin, roomMax, outline, rounding, ImDrawFlags.None, 1f);
        return true;
    }

    private static void EnsureLookupIndex(EditorMapPresentationSnapshot snapshot)
    {
        if (ReferenceEquals(indexedSnapshot, snapshot)) return;

        roomsByIndex.Clear();
        connectionsById.Clear();
        connectionsByEndpoint.Clear();
        indexedSnapshot = snapshot;

        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
        {
            EditorMapRoomSnapshot room = rooms[i];
            if (room != null) roomsByIndex[room.RoomIndex] = room;
        }

        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null) continue;
            if (!string.IsNullOrEmpty(connection.ConnectionId))
                connectionsById[connection.ConnectionId] = connection;

            long fromKey = EndpointKey(connection.FromRoomIndex, connection.FromNodeIndex);
            if (!connectionsByEndpoint.ContainsKey(fromKey))
                connectionsByEndpoint[fromKey] = connection;

            if (connection.ToNodeIndex >= 0)
            {
                long toKey = EndpointKey(connection.ToRoomIndex, connection.ToNodeIndex);
                if (!connectionsByEndpoint.ContainsKey(toKey))
                    connectionsByEndpoint[toKey] = connection;
            }
        }
    }

    private static void ClearLookupIndex()
    {
        indexedSnapshot = null;
        roomsByIndex.Clear();
        connectionsById.Clear();
        connectionsByEndpoint.Clear();
    }

    private static long EndpointKey(int roomIndex, int nodeIndex) =>
        ((long)(uint)roomIndex << 32) | (uint)nodeIndex;

    private static Num.Vector2 ResolveAnchor(
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
        if (requests != null && requests.Count > 0 && requests[0] != null)
            return requests[0].Start;
        if (obstacles != null && obstacles.Count > 0)
            return obstacles[0].Min;
        return Num.Vector2.Zero;
    }

    private static int ComputeRouteFingerprint(
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles,
        Num.Vector2 anchor)
    {
        unchecked
        {
            int hash = 17;
            int requestCount = requests?.Count ?? 0;
            int obstacleCount = obstacles?.Count ?? 0;
            hash = Mix(hash, requestCount);
            hash = Mix(hash, obstacleCount);

            for (int i = 0; i < requestCount; i++)
            {
                WorldConnectionRouter.Request request = requests[i];
                if (request == null)
                {
                    hash = Mix(hash, 0);
                    continue;
                }

                hash = Mix(hash, StringComparer.Ordinal.GetHashCode(request.Id ?? string.Empty));
                hash = Mix(hash, request.StartRoom);
                hash = Mix(hash, request.EndRoom);
                hash = MixVector(hash, request.Start - anchor);
                hash = MixVector(hash, request.End - anchor);
                hash = MixVector(hash, request.StartDirection);
                hash = MixVector(hash, request.EndDirection);
                hash = Mix(hash, Quantize(request.LaneOffset));
            }

            for (int i = 0; i < obstacleCount; i++)
            {
                WorldConnectionRouter.Obstacle obstacle = obstacles[i];
                hash = Mix(hash, obstacle.RoomIndex);
                hash = MixVector(hash, obstacle.Min - anchor);
                hash = MixVector(hash, obstacle.Max - anchor);
            }

            return hash;
        }
    }

    private static int MixVector(int hash, Num.Vector2 value)
    {
        hash = Mix(hash, Quantize(value.X));
        return Mix(hash, Quantize(value.Y));
    }

    private static int Mix(int hash, int value) => unchecked(hash * 397 ^ value);

    private static int Quantize(float value)
    {
        if (float.IsNaN(value)) return int.MinValue;
        if (float.IsPositiveInfinity(value)) return int.MaxValue;
        if (float.IsNegativeInfinity(value)) return int.MinValue + 1;

        double scaled = Math.Round(value * CoordinateQuantization);
        if (scaled > int.MaxValue) return int.MaxValue;
        if (scaled < int.MinValue) return int.MinValue;
        return (int)scaled;
    }

    private static void TranslateRoutes(WorldConnectionRouter.Route[] routes, Num.Vector2 delta)
    {
        for (int i = 0; i < routes.Length; i++)
        {
            Num.Vector2[] points = routes[i]?.Points;
            if (points == null) continue;
            Translate(points, delta);
        }
    }

    private static bool TryReuseTranslated(
        Dictionary<Num.Vector2[], PolylineCache> cache,
        Num.Vector2[] source,
        out Num.Vector2[] result)
    {
        result = null;
        if (!cache.TryGetValue(source, out PolylineCache cached)) return false;
        return TryReuseTranslated(cached, source, out result);
    }

    private static bool TryReuseTranslated(
        PolylineCache cached,
        Num.Vector2[] source,
        out Num.Vector2[] result)
    {
        result = null;
        if (cached == null || source == null || source.Length == 0 || cached.SourceLength != source.Length)
            return false;

        Num.Vector2 firstDelta = source[0] - cached.SourceFirst;
        Num.Vector2 lastDelta = source[source.Length - 1] - cached.SourceLast;
        if (Num.Vector2.DistanceSquared(firstDelta, lastDelta) > 0.0025f)
            return false;

        if (firstDelta.LengthSquared() > 0.0001f)
            Translate(cached.Result, firstDelta);

        cached.SourceFirst = source[0];
        cached.SourceLast = source[source.Length - 1];
        result = cached.Result;
        return true;
    }

    private static void StorePolyline(
        Dictionary<Num.Vector2[], PolylineCache> cache,
        Num.Vector2[] source,
        Num.Vector2[] result)
    {
        cache[source] = CreatePolylineCache(source, result);
    }

    private static PolylineCache CreatePolylineCache(Num.Vector2[] source, Num.Vector2[] result)
    {
        return new PolylineCache
        {
            SourceLength = source?.Length ?? 0,
            SourceFirst = source != null && source.Length > 0 ? source[0] : Num.Vector2.Zero,
            SourceLast = source != null && source.Length > 0 ? source[source.Length - 1] : Num.Vector2.Zero,
            Result = result ?? Array.Empty<Num.Vector2>()
        };
    }

    private static void Translate(Num.Vector2[] points, Num.Vector2 delta)
    {
        if (points == null || delta.LengthSquared() <= 0.0001f) return;
        for (int i = 0; i < points.Length; i++) points[i] += delta;
    }

    private static void ClearPolylineCaches()
    {
        roundedCache.Clear();
        trimmedCache.Clear();
        offsetCache.Clear();
    }

    private static void ResetCaches()
    {
        routeCacheValid = false;
        routeFingerprint = 0;
        routeAnchor = Num.Vector2.Zero;
        cachedRoutes = Array.Empty<WorldConnectionRouter.Route>();
        ClearPolylineCaches();
    }

    private static void ResetThrottles()
    {
        lastPublishFrame = -1000;
        lastPublishRegion = string.Empty;
        lastPublishSelection = int.MinValue;
        lastPublishCurrentRoom = int.MinValue;
        lastGeometryPrimeFrame = -1000;
        lastGeometryRegion = string.Empty;
        lastGeometrySelection = int.MinValue;
        lastGeometryCurrentRoom = int.MinValue;
        lastShortcutPrimeFrame = -1000;
        lastShortcutRegion = string.Empty;
        lastShortcutSelection = int.MinValue;
        lastShortcutCurrentRoom = int.MinValue;
    }

}
