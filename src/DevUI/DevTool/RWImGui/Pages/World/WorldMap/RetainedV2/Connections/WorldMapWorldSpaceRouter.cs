using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// World-space adapter for the proven orthogonal routing core.
///
/// The existing routing algorithm is coordinate-system agnostic; V2 feeds it retained world-space
/// room bounds and endpoints and isolates its cache IDs with a V2 prefix. Pan/zoom never reaches this
/// class. The algorithm core can be physically moved into V2 when the legacy screen-space caller is
/// retired.
/// </summary>
internal static class WorldMapWorldSpaceRouter
{
    private const float TileDisplaySize = 2f;

    internal static Dictionary<string, ConnectionRouteResource> Build(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources,
        IReadOnlyList<WorldMapScene.ConnectionNode> connections,
        IReadOnlyDictionary<string, float> laneOffsets)
    {
        Dictionary<string, ConnectionRouteResource> result =
            new(StringComparer.Ordinal);
        if (scene == null || connections == null || connections.Count == 0)
            return result;

        List<WorldConnectionRouter.Obstacle> obstacles = BuildObstacles(scene, roomResources);
        List<WorldConnectionRouter.Request> requests = new(connections.Count);
        List<WorldMapScene.ConnectionNode> accepted = new(connections.Count);

        for (int i = 0; i < connections.Count; i++)
        {
            WorldMapScene.ConnectionNode connection = connections[i];
            if (connection == null ||
                !scene.TryGetRoom(connection.FromRoomIndex, out WorldMapScene.RoomNode startRoom) ||
                !scene.TryGetRoom(connection.ToRoomIndex, out WorldMapScene.RoomNode endRoom))
                continue;

            GetRoomBounds(startRoom, roomResources, out Num.Vector2 startMin, out Num.Vector2 startMax);
            GetRoomBounds(endRoom, roomResources, out Num.Vector2 endMin, out Num.Vector2 endMax);

            Num.Vector2 start = EndpointPosition(
                startRoom,
                connection.FromNodeIndex,
                roomResources);
            Num.Vector2 end = connection.ToNodeIndex >= 0
                ? EndpointPosition(endRoom, connection.ToNodeIndex, roomResources)
                : BoundaryToward(endMin, endMax, start);

            float laneOffset = 0f;
            if (laneOffsets != null)
                laneOffsets.TryGetValue(connection.Id, out laneOffset);

            requests.Add(new WorldConnectionRouter.Request
            {
                Id = "v2:" + connection.Id,
                StartRoom = connection.FromRoomIndex,
                EndRoom = connection.ToRoomIndex,
                Start = start,
                End = end,
                StartDirection = WorldConnectionRouter.InferPortDirection(start, startMin, startMax),
                EndDirection = WorldConnectionRouter.InferPortDirection(end, endMin, endMax),
                LaneOffset = laneOffset
            });
            accepted.Add(connection);
        }

        WorldConnectionRouter.Route[] routes =
            WorldConnectionRouter.BuildRoutesCore(requests, obstacles);
        int count = Math.Min(accepted.Count, routes.Length);
        for (int i = 0; i < count; i++)
        {
            WorldMapScene.ConnectionNode connection = accepted[i];
            WorldConnectionRouter.Route route = routes[i];
            if (route == null) continue;

            result[connection.Id] = new ConnectionRouteResource
            {
                ConnectionId = connection.Id,
                FromRoomIndex = connection.FromRoomIndex,
                ToRoomIndex = connection.ToRoomIndex,
                Direction = connection.Direction,
                Ambiguous = connection.Ambiguous,
                Kind = route.Kind,
                Points = route.Points == null
                    ? Array.Empty<Num.Vector2>()
                    : (Num.Vector2[])route.Points.Clone(),
                StartDirection = route.StartDirection,
                EndDirection = route.EndDirection
            };
        }

        return result;
    }

    internal static void GetRoomBounds(
        WorldMapScene.RoomNode room,
        WorldMapRoomResourceStore roomResources,
        out Num.Vector2 min,
        out Num.Vector2 max)
    {
        float widthTiles = 12f;
        float heightTiles = 6f;
        if (roomResources != null &&
            roomResources.TryGet(room.RoomIndex, out WorldMapRoomResourceStore.RoomResource resource) &&
            resource?.Geometry != null)
        {
            widthTiles = Math.Max(1f, resource.Geometry.WidthTiles);
            heightTiles = Math.Max(1f, resource.Geometry.HeightTiles);
        }

        min = room.WorldPosition;
        max = min + new Num.Vector2(
            widthTiles * TileDisplaySize,
            heightTiles * TileDisplaySize);
    }

    internal static Num.Vector2 EndpointPosition(
        WorldMapScene.RoomNode room,
        int nodeIndex,
        WorldMapRoomResourceStore roomResources)
    {
        GetRoomBounds(room, roomResources, out Num.Vector2 min, out Num.Vector2 max);
        float widthTiles = Math.Max(1f, (max.X - min.X) / TileDisplaySize);
        float heightTiles = Math.Max(1f, (max.Y - min.Y) / TileDisplaySize);

        if (roomResources != null &&
            roomResources.TryGet(room.RoomIndex, out WorldMapRoomResourceStore.RoomResource resource))
        {
            EditorMapNodeVisualSnapshot[] nodes =
                resource?.Geometry?.Nodes ?? Array.Empty<EditorMapNodeVisualSnapshot>();
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i].NodeIndex != nodeIndex) continue;
                return new Num.Vector2(
                    min.X + nodes[i].X * TileDisplaySize,
                    min.Y + (heightTiles - nodes[i].Y) * TileDisplaySize);
            }
        }

        EditorMapRoomNodeSnapshot[] ports =
            room.Ports ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        int ordinal = 0;
        int exits = 0;
        for (int i = 0; i < ports.Length; i++)
        {
            EditorMapRoomNodeSnapshot port = ports[i];
            if (port?.Exit != true) continue;
            if (port.NodeIndex == nodeIndex) ordinal = exits;
            exits++;
        }

        if (exits <= 0)
            return (min + max) * 0.5f;

        bool right = ordinal % 2 == 0;
        int row = ordinal / 2;
        int rows = right ? (exits + 1) / 2 : exits / 2;
        float t = (row + 1f) / (rows + 1f);
        return new Num.Vector2(
            right ? max.X : min.X,
            min.Y + (max.Y - min.Y) * t);
    }

    private static List<WorldConnectionRouter.Obstacle> BuildObstacles(
        WorldMapScene scene,
        WorldMapRoomResourceStore roomResources)
    {
        List<WorldConnectionRouter.Obstacle> result = new(scene.Rooms.Count);
        foreach (WorldMapScene.RoomNode room in scene.Rooms.Values)
        {
            GetRoomBounds(room, roomResources, out Num.Vector2 min, out Num.Vector2 max);
            result.Add(new WorldConnectionRouter.Obstacle(room.RoomIndex, min, max));
        }
        return result;
    }

    private static Num.Vector2 BoundaryToward(
        Num.Vector2 min,
        Num.Vector2 max,
        Num.Vector2 target)
    {
        Num.Vector2 center = (min + max) * 0.5f;
        Num.Vector2 delta = target - center;
        if (Math.Abs(delta.X) >= Math.Abs(delta.Y))
            return new Num.Vector2(delta.X >= 0f ? max.X : min.X, center.Y);
        return new Num.Vector2(center.X, delta.Y >= 0f ? max.Y : min.Y);
    }
}
