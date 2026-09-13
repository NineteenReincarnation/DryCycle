using System;
using System.Collections.Generic;
using System.Reflection;
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
    private const float CoordinateQuantization = 4f; // quarter-pixel fingerprint precision
    private const int SnapshotIntervalFrames = 2;
    private const int PreviewPrimeIntervalFrames = 2;

    private delegate WorldConnectionRouter.Route[] OrigBuildRoutes(
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles);
    private delegate WorldConnectionRouter.Route[] HookBuildRoutes(
        OrigBuildRoutes orig,
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles);

    private delegate Num.Vector2[] OrigPolylineTransform(Num.Vector2[] path, float value);
    private delegate Num.Vector2[] HookRoundedPolyline(OrigPolylineTransform orig, Num.Vector2[] path, float radius);
    private delegate Num.Vector2[] HookTrimEnds(OrigPolylineTransform orig, Num.Vector2[] path, float amount);
    private delegate Num.Vector2[] HookOffsetPolyline(OrigPolylineTransform orig, Num.Vector2[] path, float offset);

    private delegate void OrigDrawRoomGeometry(
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered);
    private delegate void HookDrawRoomGeometry(
        OrigDrawRoomGeometry orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered);

    private delegate void OrigMapPublish(EditorSession session);
    private delegate void HookMapPublish(OrigMapPublish orig, EditorSession session);
    private delegate void OrigGeometryPrime(EditorSession session);
    private delegate void HookGeometryPrime(OrigGeometryPrime orig, EditorSession session);
    private delegate void OrigShortcutPrime(EditorSession session, int selectedRoomIndex);
    private delegate void HookShortcutPrime(OrigShortcutPrime orig, EditorSession session, int selectedRoomIndex);

    private delegate EditorMapRoomSnapshot OrigFindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex);
    private delegate EditorMapRoomSnapshot HookFindRoom(
        OrigFindRoom orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex);
    private delegate EditorMapConnectionSnapshot OrigFindConnection(EditorMapPresentationSnapshot snapshot, string id);
    private delegate EditorMapConnectionSnapshot HookFindConnection(
        OrigFindConnection orig,
        EditorMapPresentationSnapshot snapshot,
        string id);
    private delegate EditorMapConnectionSnapshot OrigFindConnectionAtEndpoint(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex);
    private delegate EditorMapConnectionSnapshot HookFindConnectionAtEndpoint(
        OrigFindConnectionAtEndpoint orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex);
    private delegate bool OrigIsEndpointFree(
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node);
    private delegate bool HookIsEndpointFree(
        OrigIsEndpointFree orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node);

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

    private static readonly HookBuildRoutes BuildRoutesHookDelegate = BuildRoutesHook;
    private static readonly HookRoundedPolyline RoundedHookDelegate = RoundedPolylineHook;
    private static readonly HookTrimEnds TrimHookDelegate = TrimEndsHook;
    private static readonly HookOffsetPolyline OffsetHookDelegate = OffsetPolylineHook;
    private static readonly HookDrawRoomGeometry RoomGeometryHookDelegate = DrawRoomGeometryHook;
    private static readonly HookMapPublish MapPublishHookDelegate = MapPublishHook;
    private static readonly HookGeometryPrime GeometryPrimeHookDelegate = GeometryPrimeHook;
    private static readonly HookShortcutPrime ShortcutPrimeHookDelegate = ShortcutPrimeHook;
    private static readonly HookFindRoom FindRoomHookDelegate = FindRoomHook;
    private static readonly HookFindConnection FindConnectionHookDelegate = FindConnectionHook;
    private static readonly HookFindConnectionAtEndpoint FindConnectionAtEndpointHookDelegate = FindConnectionAtEndpointHook;
    private static readonly HookIsEndpointFree IsEndpointFreeHookDelegate = IsEndpointFreeHook;

    private static readonly Dictionary<Num.Vector2[], PolylineCache> roundedCache = new();
    private static readonly Dictionary<Num.Vector2[], PolylineCache> trimmedCache = new();
    private static readonly Dictionary<OffsetKey, PolylineCache> offsetCache = new();
    private static readonly Dictionary<int, EditorMapRoomSnapshot> roomsByIndex = new();
    private static readonly Dictionary<string, EditorMapConnectionSnapshot> connectionsById =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<long, EditorMapConnectionSnapshot> connectionsByEndpoint = new();

    private static ManualLogSource log;
    private static IDisposable routeHook;
    private static IDisposable roundedHook;
    private static IDisposable trimHook;
    private static IDisposable offsetHook;
    private static IDisposable roomGeometryHook;
    private static IDisposable mapPublishHook;
    private static IDisposable geometryPrimeHook;
    private static IDisposable shortcutPrimeHook;
    private static IDisposable mapFindRoomHook;
    private static IDisposable overlayFindRoomHook;
    private static IDisposable mapFindConnectionHook;
    private static IDisposable overlayFindConnectionHook;
    private static IDisposable findConnectionAtEndpointHook;
    private static IDisposable isEndpointFreeHook;
    private static FieldInfo zoomField;
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
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");

            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            MethodInfo buildRoutes = typeof(WorldConnectionRouter).GetMethod(
                "BuildRoutes",
                flags,
                null,
                new[]
                {
                    typeof(IReadOnlyList<WorldConnectionRouter.Request>),
                    typeof(IReadOnlyList<WorldConnectionRouter.Obstacle>)
                },
                null);

            Type overlayType = typeof(WorldConnectionOverlay);
            MethodInfo buildRounded = overlayType.GetMethod(
                "BuildRoundedPolyline",
                flags,
                null,
                new[] { typeof(Num.Vector2[]), typeof(float) },
                null);
            MethodInfo trimEnds = overlayType.GetMethod(
                "TrimEnds",
                flags,
                null,
                new[] { typeof(Num.Vector2[]), typeof(float) },
                null);
            MethodInfo offsetPolyline = overlayType.GetMethod(
                "OffsetPolyline",
                flags,
                null,
                new[] { typeof(Num.Vector2[]), typeof(float) },
                null);
            MethodInfo overlayFindRoom = overlayType.GetMethod(
                "FindRoom",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(int) },
                null);
            MethodInfo overlayFindConnection = overlayType.GetMethod(
                "FindConnection",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(string) },
                null);

            Type mapType = typeof(WorldMapView);
            MethodInfo drawRoomGeometry = mapType.GetMethod(
                "DrawRoomGeometry",
                flags,
                null,
                new[]
                {
                    typeof(ImDrawListPtr),
                    typeof(EditorMapRoomSnapshot),
                    typeof(EditorMapRoomVisualSnapshot),
                    typeof(Num.Vector2),
                    typeof(bool),
                    typeof(bool)
                },
                null);
            MethodInfo mapFindRoom = mapType.GetMethod(
                "FindRoom",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(int) },
                null);
            MethodInfo mapFindConnection = mapType.GetMethod(
                "FindConnection",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(string) },
                null);
            MethodInfo findConnectionAtEndpoint = mapType.GetMethod(
                "FindConnectionAtEndpoint",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(int), typeof(int) },
                null);
            MethodInfo isEndpointFree = mapType.GetMethod(
                "IsEndpointFree",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot), typeof(int), typeof(EditorMapRoomNodeSnapshot) },
                null);
            zoomField = mapType.GetField("zoom", flags);

            MethodInfo mapPublish = typeof(MapEditorPresentationHub).GetMethod(
                "Publish",
                flags,
                null,
                new[] { typeof(EditorSession) },
                null);
            MethodInfo geometryPrime = typeof(MapRoomGeometryPresentationHub).GetMethod(
                "Prime",
                flags,
                null,
                new[] { typeof(EditorSession) },
                null);
            MethodInfo shortcutPrime = typeof(WorldMapShortcutPresentation).GetMethod(
                "Prime",
                flags,
                null,
                new[] { typeof(EditorSession), typeof(int) },
                null);

            if (buildRoutes == null || buildRounded == null || trimEnds == null || offsetPolyline == null ||
                drawRoomGeometry == null || zoomField == null || mapPublish == null ||
                geometryPrime == null || shortcutPrime == null || mapFindRoom == null ||
                overlayFindRoom == null || mapFindConnection == null || overlayFindConnection == null ||
                findConnectionAtEndpoint == null || isEndpointFree == null)
                throw new MissingMemberException("World Map performance hook targets were not found.");

            routeHook = constructor.Invoke(new object[] { buildRoutes, BuildRoutesHookDelegate }) as IDisposable;
            roundedHook = constructor.Invoke(new object[] { buildRounded, RoundedHookDelegate }) as IDisposable;
            trimHook = constructor.Invoke(new object[] { trimEnds, TrimHookDelegate }) as IDisposable;
            offsetHook = constructor.Invoke(new object[] { offsetPolyline, OffsetHookDelegate }) as IDisposable;
            roomGeometryHook = constructor.Invoke(new object[] { drawRoomGeometry, RoomGeometryHookDelegate }) as IDisposable;
            mapPublishHook = constructor.Invoke(new object[] { mapPublish, MapPublishHookDelegate }) as IDisposable;
            geometryPrimeHook = constructor.Invoke(new object[] { geometryPrime, GeometryPrimeHookDelegate }) as IDisposable;
            shortcutPrimeHook = constructor.Invoke(new object[] { shortcutPrime, ShortcutPrimeHookDelegate }) as IDisposable;
            mapFindRoomHook = constructor.Invoke(new object[] { mapFindRoom, FindRoomHookDelegate }) as IDisposable;
            overlayFindRoomHook = constructor.Invoke(new object[] { overlayFindRoom, FindRoomHookDelegate }) as IDisposable;
            mapFindConnectionHook = constructor.Invoke(new object[] { mapFindConnection, FindConnectionHookDelegate }) as IDisposable;
            overlayFindConnectionHook = constructor.Invoke(new object[] { overlayFindConnection, FindConnectionHookDelegate }) as IDisposable;
            findConnectionAtEndpointHook = constructor.Invoke(
                new object[] { findConnectionAtEndpoint, FindConnectionAtEndpointHookDelegate }) as IDisposable;
            isEndpointFreeHook = constructor.Invoke(new object[] { isEndpointFree, IsEndpointFreeHookDelegate }) as IDisposable;

            enabled = true;
            log?.LogInfo("World Map performance cache enabled.");
        }
        catch (Exception error)
        {
            Disable();
            log?.LogWarning("World Map performance cache could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref isEndpointFreeHook);
        DisposeHook(ref findConnectionAtEndpointHook);
        DisposeHook(ref overlayFindConnectionHook);
        DisposeHook(ref mapFindConnectionHook);
        DisposeHook(ref overlayFindRoomHook);
        DisposeHook(ref mapFindRoomHook);
        DisposeHook(ref shortcutPrimeHook);
        DisposeHook(ref geometryPrimeHook);
        DisposeHook(ref mapPublishHook);
        DisposeHook(ref roomGeometryHook);
        DisposeHook(ref offsetHook);
        DisposeHook(ref trimHook);
        DisposeHook(ref roundedHook);
        DisposeHook(ref routeHook);
        zoomField = null;
        ResetCaches();
        ResetThrottles();
        ClearLookupIndex();
        enabled = false;
        log = null;
    }

    private static void MapPublishHook(OrigMapPublish orig, EditorSession session)
    {
        // Always let the core clear the Map snapshot when the author leaves the Map page. Keeping a
        // stale region snapshot alive behind another tool is both incorrect and more expensive.
        if (session?.ToolMode != EditorToolMode.Map)
        {
            orig(session);
            lastPublishFrame = -1000;
            lastPublishRegion = string.Empty;
            lastPublishSelection = int.MinValue;
            lastPublishCurrentRoom = int.MinValue;
            ClearLookupIndex();
            return;
        }

        string region = session.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        bool urgent = !MapEditorPresentationHub.Current.Available ||
                      !string.Equals(region, lastPublishRegion, StringComparison.OrdinalIgnoreCase) ||
                      selected != lastPublishSelection ||
                      currentRoom != lastPublishCurrentRoom;

        if (!urgent && Time.frameCount - lastPublishFrame < SnapshotIntervalFrames)
            return;

        orig(session);
        lastPublishFrame = Time.frameCount;
        lastPublishRegion = region;
        lastPublishSelection = selected;
        lastPublishCurrentRoom = currentRoom;
    }

    private static void GeometryPrimeHook(OrigGeometryPrime orig, EditorSession session)
    {
        string region = session?.World?.name ?? string.Empty;
        int selected = MapEditorStateHub.Get(session)?.SelectedRoomIndex ?? -1;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent = !string.Equals(region, lastGeometryRegion, StringComparison.OrdinalIgnoreCase) ||
                      selected != lastGeometrySelection ||
                      currentRoom != lastGeometryCurrentRoom;

        if (!urgent && Time.frameCount - lastGeometryPrimeFrame < PreviewPrimeIntervalFrames)
            return;

        orig(session);
        lastGeometryPrimeFrame = Time.frameCount;
        lastGeometryRegion = region;
        lastGeometrySelection = selected;
        lastGeometryCurrentRoom = currentRoom;
    }

    private static void ShortcutPrimeHook(
        OrigShortcutPrime orig,
        EditorSession session,
        int selectedRoomIndex)
    {
        string region = session?.World?.name ?? string.Empty;
        int currentRoom = session?.Room?.abstractRoom?.index ?? -1;
        bool urgent = !string.Equals(region, lastShortcutRegion, StringComparison.OrdinalIgnoreCase) ||
                      selectedRoomIndex != lastShortcutSelection ||
                      currentRoom != lastShortcutCurrentRoom;

        if (!urgent && Time.frameCount - lastShortcutPrimeFrame < PreviewPrimeIntervalFrames)
            return;

        orig(session, selectedRoomIndex);
        lastShortcutPrimeFrame = Time.frameCount;
        lastShortcutRegion = region;
        lastShortcutSelection = selectedRoomIndex;
        lastShortcutCurrentRoom = currentRoom;
    }

    private static EditorMapRoomSnapshot FindRoomHook(
        OrigFindRoom orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex)
    {
        EnsureLookupIndex(snapshot);
        return roomsByIndex.TryGetValue(roomIndex, out EditorMapRoomSnapshot room) ? room : null;
    }

    private static EditorMapConnectionSnapshot FindConnectionHook(
        OrigFindConnection orig,
        EditorMapPresentationSnapshot snapshot,
        string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLookupIndex(snapshot);
        return connectionsById.TryGetValue(id, out EditorMapConnectionSnapshot connection) ? connection : null;
    }

    private static EditorMapConnectionSnapshot FindConnectionAtEndpointHook(
        OrigFindConnectionAtEndpoint orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        int nodeIndex)
    {
        EnsureLookupIndex(snapshot);
        return connectionsByEndpoint.TryGetValue(EndpointKey(roomIndex, nodeIndex), out EditorMapConnectionSnapshot connection)
            ? connection
            : null;
    }

    private static bool IsEndpointFreeHook(
        OrigIsEndpointFree orig,
        EditorMapPresentationSnapshot snapshot,
        int roomIndex,
        EditorMapRoomNodeSnapshot node)
    {
        if (node == null || !node.Exit || node.ConnectedRoomIndex >= 0) return false;
        EnsureLookupIndex(snapshot);
        return !connectionsByEndpoint.ContainsKey(EndpointKey(roomIndex, node.NodeIndex));
    }

    private static WorldConnectionRouter.Route[] BuildRoutesHook(
        OrigBuildRoutes orig,
        IReadOnlyList<WorldConnectionRouter.Request> requests,
        IReadOnlyList<WorldConnectionRouter.Obstacle> obstacles)
    {
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

        WorldConnectionRouter.Route[] routes = orig(requests, obstacles) ?? Array.Empty<WorldConnectionRouter.Route>();
        cachedRoutes = routes;
        routeFingerprint = fingerprint;
        routeAnchor = anchor;
        routeCacheValid = true;
        ClearPolylineCaches();
        return routes;
    }

    private static Num.Vector2[] RoundedPolylineHook(
        OrigPolylineTransform orig,
        Num.Vector2[] path,
        float radius)
    {
        if (path == null || path.Length == 0)
            return orig(path, radius);

        if (TryReuseTranslated(roundedCache, path, out Num.Vector2[] result))
            return result;

        result = orig(path, radius) ?? Array.Empty<Num.Vector2>();
        StorePolyline(roundedCache, path, result);
        return result;
    }

    private static Num.Vector2[] TrimEndsHook(
        OrigPolylineTransform orig,
        Num.Vector2[] path,
        float amount)
    {
        if (path == null || path.Length == 0)
            return orig(path, amount);

        if (TryReuseTranslated(trimmedCache, path, out Num.Vector2[] result))
            return result;

        result = orig(path, amount) ?? Array.Empty<Num.Vector2>();
        StorePolyline(trimmedCache, path, result);
        return result;
    }

    private static Num.Vector2[] OffsetPolylineHook(
        OrigPolylineTransform orig,
        Num.Vector2[] path,
        float offset)
    {
        if (path == null || path.Length == 0)
            return orig(path, offset);

        OffsetKey key = new(path, offset);
        if (offsetCache.TryGetValue(key, out PolylineCache cached) &&
            TryReuseTranslated(cached, path, out Num.Vector2[] result))
            return result;

        result = orig(path, offset) ?? Array.Empty<Num.Vector2>();
        offsetCache[key] = CreatePolylineCache(path, result);
        return result;
    }

    private static void DrawRoomGeometryHook(
        OrigDrawRoomGeometry orig,
        ImDrawListPtr draw,
        EditorMapRoomSnapshot room,
        EditorMapRoomVisualSnapshot visual,
        Num.Vector2 roomMin,
        bool selected,
        bool hovered)
    {
        float zoom = zoomField?.GetValue(null) is float value ? value : 1f;
        if (zoom >= OverviewLodZoom || selected || hovered || room?.CurrentRoom == true)
        {
            orig(draw, room, visual, roomMin, selected, hovered);
            return;
        }

        // At overview zoom one authored tile is below a screen pixel. Submitting every cached raster
        // run and every curve segment is pure overdraw, so render the room as a stable overview card.
        // Hovering/selecting it immediately switches back to the full terrain preview.
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

    private static void DisposeHook(ref IDisposable hook)
    {
        try
        {
            hook?.Dispose();
        }
        catch
        {
        }
        finally
        {
            hook = null;
        }
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
