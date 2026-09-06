using System;
using System.Threading;

namespace DryCycle.Debugging.AI;

[Flags]
internal enum AIDebugRoomTileFlags : byte
{
    None = 0,
    Solid = 1 << 0,
    Shortcut = 1 << 1,
    VerticalBeam = 1 << 2,
    HorizontalBeam = 1 << 3,
    Water = 1 << 4,
    AIWalkable = 1 << 5,
    AINarrow = 1 << 6,
    AIAir = 1 << 7
}

// Immutable detached room geometry. One byte per tile is intentionally simple: room
// geometry is captured once on the Unity/main thread and Presentation can then draw it
// repeatedly without touching Room, AImap, Tile or Unity objects from Present.
internal sealed class AIDebugRoomGeometrySnapshot
{
    internal readonly int RoomIndex;
    internal readonly string RoomName;
    internal readonly int Width;
    internal readonly int Height;
    internal readonly AIDebugRoomTileFlags[] Tiles;

    internal AIDebugRoomGeometrySnapshot(
        int roomIndex,
        string roomName,
        int width,
        int height,
        AIDebugRoomTileFlags[] tiles)
    {
        RoomIndex = roomIndex;
        RoomName = roomName ?? ("room " + roomIndex);
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        Tiles = tiles ?? Array.Empty<AIDebugRoomTileFlags>();
    }

    internal AIDebugRoomTileFlags Get(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return AIDebugRoomTileFlags.None;
        int index = y * Width + x;
        return (uint)index < (uint)Tiles.Length ? Tiles[index] : AIDebugRoomTileFlags.None;
    }
}

internal static class AIDebugRoomGeometryCache
{
    private const int Capacity = 12;
    private static readonly AIDebugRoomGeometrySnapshot[] Slots = new AIDebugRoomGeometrySnapshot[Capacity];
    private static int replace;

    // Main-thread only. If the room is already cached the immutable snapshot is reused.
    internal static void Capture(Room room)
    {
        if (room?.abstractRoom == null || room.Width <= 0 || room.Height <= 0) return;
        int roomIndex = room.abstractRoom.index;
        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugRoomGeometrySnapshot existing = Volatile.Read(ref Slots[i]);
            if (existing != null && existing.RoomIndex == roomIndex &&
                existing.Width == room.Width && existing.Height == room.Height)
                return;
        }

        int tileCount;
        try { tileCount = checked(room.Width * room.Height); }
        catch { return; }
        if (tileCount <= 0 || tileCount > 1024 * 1024) return;

        var flags = new AIDebugRoomTileFlags[tileCount];
        for (int y = 0; y < room.Height; y++)
        {
            for (int x = 0; x < room.Width; x++)
            {
                Room.Tile tile = room.GetTile(x, y);
                AIDebugRoomTileFlags value = AIDebugRoomTileFlags.None;
                if (tile != null)
                {
                    if (tile.Solid) value |= AIDebugRoomTileFlags.Solid;
                    if (tile.shortCut > 0 || tile.Terrain == Room.Tile.TerrainType.ShortcutEntrance)
                        value |= AIDebugRoomTileFlags.Shortcut;
                    if (tile.verticalBeam) value |= AIDebugRoomTileFlags.VerticalBeam;
                    if (tile.horizontalBeam) value |= AIDebugRoomTileFlags.HorizontalBeam;
                    if (tile.AnyWater) value |= AIDebugRoomTileFlags.Water;
                }

                if (room.aimap != null)
                {
                    try
                    {
                        AItile ai = room.aimap.getAItile(x, y);
                        if (ai != null)
                        {
                            if (ai.walkable) value |= AIDebugRoomTileFlags.AIWalkable;
                            if (ai.narrowSpace) value |= AIDebugRoomTileFlags.AINarrow;
                            if (ai.acc == AItile.Accessibility.Air) value |= AIDebugRoomTileFlags.AIAir;
                        }
                    }
                    catch
                    {
                        // AIMap can be between generations during room realization. Static
                        // physical geometry remains valid and a later capture can fill AIMap.
                    }
                }

                flags[y * room.Width + x] = value;
            }
        }

        var snapshot = new AIDebugRoomGeometrySnapshot(
            roomIndex,
            room.abstractRoom.name,
            room.Width,
            room.Height,
            flags);

        int target = -1;
        for (int i = 0; i < Slots.Length; i++)
        {
            if (Volatile.Read(ref Slots[i]) == null)
            {
                target = i;
                break;
            }
        }
        if (target < 0)
        {
            target = replace++;
            if (replace >= Capacity) replace = 0;
        }
        Volatile.Write(ref Slots[target], snapshot);
    }

    // Present-safe. Returned snapshots and tile arrays are immutable after publication.
    internal static bool TryGet(int roomIndex, out AIDebugRoomGeometrySnapshot snapshot)
    {
        for (int i = 0; i < Slots.Length; i++)
        {
            AIDebugRoomGeometrySnapshot candidate = Volatile.Read(ref Slots[i]);
            if (candidate != null && candidate.RoomIndex == roomIndex)
            {
                snapshot = candidate;
                return true;
            }
        }
        snapshot = null;
        return false;
    }

    internal static void Reset()
    {
        for (int i = 0; i < Slots.Length; i++) Volatile.Write(ref Slots[i], null);
        replace = 0;
    }
}
