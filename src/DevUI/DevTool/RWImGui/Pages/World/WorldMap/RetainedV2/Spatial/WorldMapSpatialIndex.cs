using System;
using System.Collections.Generic;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Incremental world-space room spatial hash.
///
/// Static map geometry is sparse and rooms move individually while authoring, so a fixed world-space
/// hash gives predictable O(local-cells) pan/hover queries and cheaper incremental updates than
/// rebuilding a hierarchy during every room drag.
/// </summary>
internal sealed class WorldMapSpatialIndex
{
    private sealed class Entry
    {
        internal int RoomIndex;
        internal int Layer;
        internal Num.Vector2 Min;
        internal Num.Vector2 Max;
        internal long[] Cells = Array.Empty<long>();
    }

    private const float CellSize = 256f;

    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries = new();
    private readonly Dictionary<long, HashSet<int>> cells = new();
    private readonly HashSet<int> querySeen = new();

    internal int Count
    {
        get
        {
            lock (gate) return entries.Count;
        }
    }

    internal void ApplyDirty(
        WorldMapScene scene,
        WorldMapRoomResourceStore resources,
        WorldMapDirtySet dirty)
    {
        if (scene == null || dirty == null) return;

        lock (gate)
        {
            if (dirty.FullRebuild)
            {
                ClearLocked();
                foreach (WorldMapScene.RoomNode room in scene.Rooms.Values)
                    UpsertLocked(room, resources);
                return;
            }

            foreach (int roomIndex in dirty.RemovedRooms)
                RemoveLocked(roomIndex);

            foreach (int roomIndex in dirty.RoomTransforms)
                UpsertFromSceneLocked(scene, resources, roomIndex);
            foreach (int roomIndex in dirty.RoomMetadata)
                UpsertFromSceneLocked(scene, resources, roomIndex);
            foreach (int roomIndex in dirty.RoomPorts)
            {
                if (!entries.ContainsKey(roomIndex))
                    UpsertFromSceneLocked(scene, resources, roomIndex);
            }
        }
    }

    internal void InvalidateRooms(
        WorldMapScene scene,
        WorldMapRoomResourceStore resources,
        IEnumerable<int> roomIndices)
    {
        if (scene == null || roomIndices == null) return;
        lock (gate)
        {
            foreach (int roomIndex in roomIndices)
                UpsertFromSceneLocked(scene, resources, roomIndex);
        }
    }

    internal bool TryHitRoom(
        Num.Vector2 point,
        int layerMask,
        out int roomIndex)
    {
        roomIndex = -1;
        lock (gate)
        {
            if (entries.Count == 0) return false;

            long key = CellKey(
                FloorToInt(point.X / CellSize),
                FloorToInt(point.Y / CellSize));
            if (!cells.TryGetValue(key, out HashSet<int> ids))
                return false;

            int bestLayer = int.MinValue;
            int bestRoom = -1;
            foreach (int id in ids)
            {
                if (!entries.TryGetValue(id, out Entry entry) ||
                    !LayerVisible(entry.Layer, layerMask) ||
                    point.X < entry.Min.X || point.X > entry.Max.X ||
                    point.Y < entry.Min.Y || point.Y > entry.Max.Y)
                    continue;

                if (entry.Layer < bestLayer) continue;
                bestLayer = entry.Layer;
                bestRoom = id;
            }

            roomIndex = bestRoom;
            return bestRoom >= 0;
        }
    }

    internal bool Query(
        Num.Vector2 min,
        Num.Vector2 max,
        int layerMask,
        List<int> output)
    {
        if (output == null) return false;
        output.Clear();

        lock (gate)
        {
            if (entries.Count == 0) return false;
            querySeen.Clear();

            int minX = FloorToInt(Math.Min(min.X, max.X) / CellSize);
            int maxX = FloorToInt(Math.Max(min.X, max.X) / CellSize);
            int minY = FloorToInt(Math.Min(min.Y, max.Y) / CellSize);
            int maxY = FloorToInt(Math.Max(min.Y, max.Y) / CellSize);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    if (!cells.TryGetValue(CellKey(x, y), out HashSet<int> ids))
                        continue;

                    foreach (int id in ids)
                    {
                        if (!querySeen.Add(id) ||
                            !entries.TryGetValue(id, out Entry entry) ||
                            !LayerVisible(entry.Layer, layerMask) ||
                            !Intersects(entry.Min, entry.Max, min, max))
                            continue;

                        output.Add(id);
                    }
                }
            }

            output.Sort((a, b) =>
            {
                int la = entries.TryGetValue(a, out Entry ea) ? ea.Layer : 0;
                int lb = entries.TryGetValue(b, out Entry eb) ? eb.Layer : 0;
                int layerCompare = la.CompareTo(lb);
                return layerCompare != 0 ? layerCompare : a.CompareTo(b);
            });
            return true;
        }
    }

    internal void Reset()
    {
        lock (gate)
            ClearLocked();
    }

    private void UpsertFromSceneLocked(
        WorldMapScene scene,
        WorldMapRoomResourceStore resources,
        int roomIndex)
    {
        if (!scene.TryGetRoom(roomIndex, out WorldMapScene.RoomNode room))
        {
            RemoveLocked(roomIndex);
            return;
        }

        UpsertLocked(room, resources);
    }

    private void UpsertLocked(
        WorldMapScene.RoomNode room,
        WorldMapRoomResourceStore resources)
    {
        WorldMapWorldSpaceRouter.GetRoomBounds(
            room,
            resources,
            out Num.Vector2 min,
            out Num.Vector2 max);

        if (entries.TryGetValue(room.RoomIndex, out Entry existing) &&
            existing.Layer == room.Layer &&
            Approximately(existing.Min, min) &&
            Approximately(existing.Max, max))
            return;

        RemoveLocked(room.RoomIndex);

        List<long> occupied = new();
        int minX = FloorToInt(min.X / CellSize);
        int maxX = FloorToInt(max.X / CellSize);
        int minY = FloorToInt(min.Y / CellSize);
        int maxY = FloorToInt(max.Y / CellSize);

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                long key = CellKey(x, y);
                occupied.Add(key);
                if (!cells.TryGetValue(key, out HashSet<int> ids))
                {
                    ids = new HashSet<int>();
                    cells.Add(key, ids);
                }
                ids.Add(room.RoomIndex);
            }
        }

        entries[room.RoomIndex] = new Entry
        {
            RoomIndex = room.RoomIndex,
            Layer = room.Layer,
            Min = min,
            Max = max,
            Cells = occupied.ToArray()
        };
    }

    private void RemoveLocked(int roomIndex)
    {
        if (!entries.TryGetValue(roomIndex, out Entry entry))
            return;

        entries.Remove(roomIndex);
        for (int i = 0; i < entry.Cells.Length; i++)
        {
            long key = entry.Cells[i];
            if (!cells.TryGetValue(key, out HashSet<int> ids)) continue;
            ids.Remove(roomIndex);
            if (ids.Count == 0) cells.Remove(key);
        }
    }

    private void ClearLocked()
    {
        entries.Clear();
        cells.Clear();
        querySeen.Clear();
    }

    private static bool LayerVisible(int layer, int mask) =>
        layer >= 0 && layer < 31 && (mask & (1 << layer)) != 0;

    private static bool Intersects(
        Num.Vector2 aMin,
        Num.Vector2 aMax,
        Num.Vector2 bMin,
        Num.Vector2 bMax) =>
        aMax.X >= bMin.X && aMin.X <= bMax.X &&
        aMax.Y >= bMin.Y && aMin.Y <= bMax.Y;

    private static bool Approximately(Num.Vector2 a, Num.Vector2 b) =>
        Num.Vector2.DistanceSquared(a, b) <= 0.0001f;

    private static int FloorToInt(float value) =>
        (int)Math.Floor(value);

    private static long CellKey(int x, int y) =>
        ((long)(uint)x << 32) | (uint)y;
}
