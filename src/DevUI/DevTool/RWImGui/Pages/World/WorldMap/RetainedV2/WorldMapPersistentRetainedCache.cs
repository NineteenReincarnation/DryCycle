using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using DryCycle.DevUI.DevTool.Map;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum WorldMapPersistentRouteRestoreResult
{
    None,
    Pending,
    Restored
}

/// <summary>
/// Optional-frontend payload for MapView persistent cache V3.
/// Core owns file IO and room-source stamp validation; this layer owns retained thumbnails/routes.
/// </summary>
internal static class WorldMapPersistentRetainedCache
{
    private static readonly Dictionary<int, MapViewPersistentRoom> validRooms = new();
    private static readonly HashSet<int> invalidRooms = new();
    private static readonly HashSet<int> consumedThumbnailHints = new();
    private static readonly Dictionary<string, MapViewPersistentRoute> routes =
        new(StringComparer.Ordinal);

    private static ManualLogSource log;
    private static bool enabled;
    private static int expectedRoomCount;
    private static long cachedTopologyFingerprint;
    private static long verifiedTopologyFingerprint;
    private static bool topologyRejected;
    private static bool roomValidationComplete;
    private static bool restoreSnapshotLoaded;
    private static long restoreValidationStartedTicks;
    private static long restoreValidationCompletedTicks;
    private static int restoredRouteCount;
    private static int restoredThumbnailCount;

    internal static int ValidatedRoomCount =>
        enabled ? validRooms.Count : 0;

    internal static int StagedRouteCount =>
        enabled ? routes.Count : 0;

    internal static bool RoomValidationComplete =>
        enabled && roomValidationComplete;

    internal static bool TopologyRejected =>
        enabled && topologyRejected;

    internal static bool RestoreSnapshotLoaded =>
        enabled && restoreSnapshotLoaded;

    internal static int RestoredRouteCount =>
        enabled ? restoredRouteCount : 0;

    internal static int RestoredThumbnailCount =>
        enabled ? restoredThumbnailCount : 0;

    internal static double RestoreValidationMilliseconds
    {
        get
        {
            if (!enabled || restoreValidationStartedTicks <= 0L)
                return 0d;
            long end =
                restoreValidationCompletedTicks > 0L
                    ? restoreValidationCompletedTicks
                    : Stopwatch.GetTimestamp();
            return Math.Max(0L, end - restoreValidationStartedTicks) *
                   1000d / Stopwatch.Frequency;
        }
    }

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        enabled = true;
        log = logger;
        Clear();

        WorldMapFrontendBridge.RegisterPersistentCacheCallbacks(
            Capture,
            RestoreSnapshot,
            RestoreRoom,
            CompleteRoomValidation,
            Clear);
    }

    internal static void Disable()
    {
        if (enabled)
        {
            WorldMapFrontendBridge.UnregisterPersistentCacheCallbacks(
                Capture,
                RestoreSnapshot,
                RestoreRoom,
                CompleteRoomValidation,
                Clear);
        }

        enabled = false;
        Clear();
        log = null;
    }

    internal static bool TryResolveThumbnail(
        int roomIndex,
        out WorldMapLegacyRoomSourceService.RoomTextureSource source)
    {
        source = default;
        if (!enabled ||
            consumedThumbnailHints.Contains(roomIndex) ||
            !validRooms.TryGetValue(roomIndex, out MapViewPersistentRoom room) ||
            string.IsNullOrWhiteSpace(room.ThumbnailElementName))
            return false;

        if (!WorldMapLegacyRoomSourceService.TryResolvePersistentAtlasTexture(
                room.ThumbnailElementName,
                out source))
            return false;

        consumedThumbnailHints.Add(roomIndex);
        restoredThumbnailCount++;
        return true;
    }

    internal static WorldMapPersistentRouteRestoreResult TryRestoreRoute(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        string connectionId,
        out ConnectionRouteResource route)
    {
        route = null;
        if (!enabled ||
            scene == null ||
            string.IsNullOrEmpty(connectionId) ||
            !routes.TryGetValue(connectionId, out MapViewPersistentRoute stored))
            return WorldMapPersistentRouteRestoreResult.None;

        if (topologyRejected ||
            cachedTopologyFingerprint == 0L)
        {
            routes.Remove(connectionId);
            return WorldMapPersistentRouteRestoreResult.None;
        }

        if (invalidRooms.Count > 0 ||
            scene.Rooms.Count != expectedRoomCount)
        {
            topologyRejected = true;
            routes.Clear();
            return WorldMapPersistentRouteRestoreResult.None;
        }

        // Before the first full source audit finishes, cached routes may wait briefly. After the
        // audit, any unresolved room is a cache miss and must fail open to normal routing.
        if (validRooms.Count < expectedRoomCount)
        {
            if (!roomValidationComplete)
                return WorldMapPersistentRouteRestoreResult.Pending;

            topologyRejected = true;
            routes.Clear();
            return WorldMapPersistentRouteRestoreResult.None;
        }

        if (verifiedTopologyFingerprint == 0L)
        {
            verifiedTopologyFingerprint =
                ComputeTopologyFingerprint(scene);
            if (verifiedTopologyFingerprint !=
                cachedTopologyFingerprint)
            {
                topologyRejected = true;
                routes.Clear();
                return WorldMapPersistentRouteRestoreResult.None;
            }
        }

        if (!scene.TryGetConnection(connectionId, out WorldMapScene.ConnectionNode connection) ||
            !scene.TryGetRoom(stored.FromRoomIndex, out WorldMapScene.RoomNode fromRoom) ||
            !scene.TryGetRoom(stored.ToRoomIndex, out WorldMapScene.RoomNode toRoom))
        {
            routes.Remove(connectionId);
            return WorldMapPersistentRouteRestoreResult.None;
        }

        if (stored.PolicyVersion != WorldMapOrthogonalRouter.PersistentPolicyVersion ||
            connection.FromRoomIndex != stored.FromRoomIndex ||
            connection.FromNodeIndex != stored.FromNodeIndex ||
            connection.ToRoomIndex != stored.ToRoomIndex ||
            connection.ToNodeIndex != stored.ToNodeIndex ||
            (int)connection.Direction != stored.Direction ||
            connection.Ambiguous != stored.Ambiguous ||
            !string.Equals(fromRoom.Name, stored.FromRoomName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(toRoom.Name, stored.ToRoomName, StringComparison.OrdinalIgnoreCase) ||
            !Approximately(fromRoom.WorldPosition, new Num.Vector2(stored.FromRoomX, stored.FromRoomY)) ||
            !Approximately(toRoom.WorldPosition, new Num.Vector2(stored.ToRoomX, stored.ToRoomY)) ||
            stored.Points == null ||
            stored.Points.Length < 2 ||
            !Enum.IsDefined(typeof(WorldMapOrthogonalRouter.RouteKind), stored.Kind))
        {
            routes.Remove(connectionId);
            return WorldMapPersistentRouteRestoreResult.None;
        }

        Num.Vector2[] points = new Num.Vector2[stored.Points.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Num.Vector2(stored.Points[i].X, stored.Points[i].Y);

        route = new ConnectionRouteResource
        {
            ConnectionId = connectionId,
            FromRoomIndex = stored.FromRoomIndex,
            ToRoomIndex = stored.ToRoomIndex,
            Direction = connection.Direction,
            Ambiguous = stored.Ambiguous,
            Kind = (WorldMapOrthogonalRouter.RouteKind)stored.Kind,
            BasePoints = (Num.Vector2[])points.Clone(),
            Points = points,
            StartDirection = new Num.Vector2(stored.StartDirectionX, stored.StartDirectionY),
            EndDirection = new Num.Vector2(stored.EndDirectionX, stored.EndDirectionY)
        };

        routes.Remove(connectionId);
        restoredRouteCount++;
        return WorldMapPersistentRouteRestoreResult.Restored;
    }

    private static void Capture(MapViewPersistentSnapshot snapshot)
    {
        if (!enabled || snapshot == null) return;

        WorldMapScene scene = WorldMapRetainedV2Runtime.MainSceneForPersistence;
        if (scene == null) return;

        Dictionary<string, MapViewPersistentRoom> roomsByName =
            new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snapshot.Rooms.Count; i++)
        {
            MapViewPersistentRoom room = snapshot.Rooms[i];
            if (room != null && !string.IsNullOrWhiteSpace(room.RoomName))
                roomsByName[room.RoomName] = room;
        }

        foreach (KeyValuePair<int, WorldMapRoomResourceStore.RoomResource> pair
                 in WorldMapRetainedV2Runtime.Resources.Rooms)
        {
            if (!scene.TryGetRoom(pair.Key, out WorldMapScene.RoomNode sceneRoom) ||
                !roomsByName.TryGetValue(sceneRoom.Name, out MapViewPersistentRoom storedRoom))
                continue;

            RoomThumbnailResource thumbnail = pair.Value?.Thumbnail;
            if (thumbnail?.HasCommitted != true) continue;

            RoomThumbnailResource.Descriptor descriptor = thumbnail.Committed;
            if (string.IsNullOrWhiteSpace(descriptor.PersistentElementName))
                continue;

            storedRoom.ThumbnailElementName = descriptor.PersistentElementName;
            storedRoom.ThumbnailUvX = descriptor.Uv.x;
            storedRoom.ThumbnailUvY = descriptor.Uv.y;
            storedRoom.ThumbnailUvWidth = descriptor.Uv.width;
            storedRoom.ThumbnailUvHeight = descriptor.Uv.height;
            storedRoom.ThumbnailPixelWidth = descriptor.PixelWidth;
            storedRoom.ThumbnailPixelHeight = descriptor.PixelHeight;
        }

        snapshot.FrontendTopologyFingerprint =
            ComputeTopologyFingerprint(scene);

        snapshot.Routes.Clear();
        foreach (KeyValuePair<string, ConnectionRouteResource> pair
                 in WorldMapRetainedV2Runtime.Routes.Routes)
        {
            ConnectionRouteResource route = pair.Value;
            Num.Vector2[] persistentPoints =
                route?.BasePoints != null &&
                route.BasePoints.Length >= 2
                    ? route.BasePoints
                    : route?.Points;

            if (persistentPoints == null ||
                persistentPoints.Length < 2 ||
                !scene.TryGetConnection(pair.Key, out WorldMapScene.ConnectionNode connection) ||
                !scene.TryGetRoom(connection.FromRoomIndex, out WorldMapScene.RoomNode fromRoom) ||
                !scene.TryGetRoom(connection.ToRoomIndex, out WorldMapScene.RoomNode toRoom))
                continue;

            EditorMapPointSnapshot[] points =
                new EditorMapPointSnapshot[persistentPoints.Length];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] =
                    new EditorMapPointSnapshot(
                        persistentPoints[i].X,
                        persistentPoints[i].Y);
            }

            snapshot.Routes.Add(new MapViewPersistentRoute
            {
                ConnectionId = pair.Key,
                FromRoomIndex = connection.FromRoomIndex,
                FromNodeIndex = connection.FromNodeIndex,
                ToRoomIndex = connection.ToRoomIndex,
                ToNodeIndex = connection.ToNodeIndex,
                FromRoomName = fromRoom.Name ?? string.Empty,
                ToRoomName = toRoom.Name ?? string.Empty,
                Direction = (int)connection.Direction,
                Ambiguous = connection.Ambiguous,
                PolicyVersion = WorldMapOrthogonalRouter.PersistentPolicyVersion,
                Kind = (int)route.Kind,
                FromRoomX = fromRoom.WorldPosition.X,
                FromRoomY = fromRoom.WorldPosition.Y,
                ToRoomX = toRoom.WorldPosition.X,
                ToRoomY = toRoom.WorldPosition.Y,
                StartDirectionX = route.StartDirection.X,
                StartDirectionY = route.StartDirection.Y,
                EndDirectionX = route.EndDirection.X,
                EndDirectionY = route.EndDirection.Y,
                Points = points
            });
        }
    }

    private static void RestoreSnapshot(MapViewPersistentSnapshot snapshot)
    {
        Clear();
        if (!enabled || snapshot == null) return;

        restoreSnapshotLoaded = true;
        restoreValidationStartedTicks = Stopwatch.GetTimestamp();
        restoreValidationCompletedTicks = 0L;
        restoredRouteCount = 0;
        restoredThumbnailCount = 0;
        expectedRoomCount = snapshot.Rooms.Count;
        cachedTopologyFingerprint =
            snapshot.FrontendTopologyFingerprint;
        verifiedTopologyFingerprint = 0L;
        topologyRejected = false;
        roomValidationComplete = false;
        restoreSnapshotLoaded = false;
        restoreValidationStartedTicks = 0L;
        restoreValidationCompletedTicks = 0L;
        restoredRouteCount = 0;
        restoredThumbnailCount = 0;

        for (int i = 0; i < snapshot.Routes.Count; i++)
        {
            MapViewPersistentRoute route = snapshot.Routes[i];
            if (route == null || string.IsNullOrWhiteSpace(route.ConnectionId))
                continue;
            routes[route.ConnectionId] = route;
        }

        if (routes.Count > 0)
            log?.LogInfo("WorldMap V3 staged " + routes.Count + " persistent route(s).");
    }

    private static void RestoreRoom(
        int roomIndex,
        MapViewPersistentRoom room,
        bool sourceValid)
    {
        if (!enabled) return;

        if (!sourceValid)
        {
            validRooms.Remove(roomIndex);
            invalidRooms.Add(roomIndex);
            return;
        }

        invalidRooms.Remove(roomIndex);
        validRooms[roomIndex] = room;
    }

    private static void CompleteRoomValidation(
        IReadOnlyCollection<int> roomIndices)
    {
        if (!enabled || roomValidationComplete)
            return;

        roomValidationComplete = true;
        restoreValidationCompletedTicks = Stopwatch.GetTimestamp();
        int liveCount = roomIndices?.Count ?? 0;

        if (liveCount != expectedRoomCount)
        {
            topologyRejected = true;
            routes.Clear();
            return;
        }

        if (roomIndices == null)
        {
            topologyRejected = true;
            routes.Clear();
            return;
        }

        foreach (int roomIndex in roomIndices)
        {
            if (validRooms.ContainsKey(roomIndex) ||
                invalidRooms.Contains(roomIndex))
                continue;

            invalidRooms.Add(roomIndex);
        }

        if (invalidRooms.Count > 0)
        {
            topologyRejected = true;
            routes.Clear();
        }

        log?.LogInfo(
            "WorldMap persistent cache validation complete: " +
            RestoreValidationMilliseconds.ToString("F0") +
            " ms, valid rooms " +
            validRooms.Count + "/" + expectedRoomCount +
            ", invalid rooms " + invalidRooms.Count +
            ", staged routes " + routes.Count +
            (topologyRejected ? ", topology rejected." : "."));
    }

    private static void Clear()
    {
        validRooms.Clear();
        invalidRooms.Clear();
        consumedThumbnailHints.Clear();
        routes.Clear();
        expectedRoomCount = 0;
        cachedTopologyFingerprint = 0L;
        verifiedTopologyFingerprint = 0L;
        topologyRejected = false;
        roomValidationComplete = false;
    }

    private static long ComputeTopologyFingerprint(
        WorldMapScene scene)
    {
        if (scene == null) return 0L;

        unchecked
        {
            ulong hash = 1469598103934665603UL;

            List<int> roomIds = new(scene.Rooms.Keys);
            roomIds.Sort();
            hash = Mix(hash, roomIds.Count);
            for (int i = 0; i < roomIds.Count; i++)
            {
                WorldMapScene.RoomNode room =
                    scene.Rooms[roomIds[i]];
                hash = Mix(hash, room.RoomIndex);
                hash = MixString(hash, room.Name);
                hash = Mix(hash, room.WorldPosition.X.GetHashCode());
                hash = Mix(hash, room.WorldPosition.Y.GetHashCode());
            }

            List<string> connectionIds =
                new(scene.Connections.Keys);
            connectionIds.Sort(StringComparer.Ordinal);
            hash = Mix(hash, connectionIds.Count);
            for (int i = 0; i < connectionIds.Count; i++)
            {
                WorldMapScene.ConnectionNode connection =
                    scene.Connections[connectionIds[i]];
                hash = MixString(hash, connection.Id);
                hash = Mix(hash, connection.FromRoomIndex);
                hash = Mix(hash, connection.FromNodeIndex);
                hash = Mix(hash, connection.ToRoomIndex);
                hash = Mix(hash, connection.ToNodeIndex);
                hash = Mix(hash, (int)connection.Direction);
                hash = Mix(hash, connection.Ambiguous ? 1 : 0);
            }

            return (long)hash;
        }
    }

    private static ulong Mix(ulong hash, int value)
    {
        unchecked
        {
            hash = (hash ^ (uint)value) * 1099511628211UL;
            hash = (hash ^ (uint)(value >> 16)) * 1099511628211UL;
            return hash;
        }
    }

    private static ulong MixString(ulong hash, string value)
    {
        if (string.IsNullOrEmpty(value))
            return Mix(hash, 0);

        unchecked
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                hash = (hash ^ (byte)c) * 1099511628211UL;
                hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
            }
            return hash;
        }
    }

    private static bool Approximately(Num.Vector2 a, Num.Vector2 b) =>
        Num.Vector2.DistanceSquared(a, b) <= 0.01f;
}
