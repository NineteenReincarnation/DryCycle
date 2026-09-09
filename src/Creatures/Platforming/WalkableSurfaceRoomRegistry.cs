using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace DryCycle.Creatures.Platforming;

/// <summary>
/// Room-local weak registry for dynamic walkable surfaces. The room update list is scanned at
/// most once per Room.ticker, so several players can acquire surfaces without each rescanning
/// every object in the room.
/// </summary>
internal static class WalkableSurfaceRoomRegistry
{
    private sealed class SurfaceBucket
    {
        internal readonly List<WeakReference<IWalkableDynamicSurface>> Surfaces = new();
        internal int LastRefreshTicker = int.MinValue;
        internal int LastUpdateListCount = -1;
    }

    private static ConditionalWeakTable<Room, SurfaceBucket> rooms = new();

    internal static void Register(IWalkableDynamicSurface surface)
    {
        Room room = surface?.SurfaceRoom;
        if (room == null) return;
        AddUnique(rooms.GetValue(room, _ => new SurfaceBucket()), surface);
    }

    internal static void Unregister(IWalkableDynamicSurface surface)
    {
        Room room = surface?.SurfaceRoom;
        if (room == null || !rooms.TryGetValue(room, out SurfaceBucket bucket)) return;

        for (int i = bucket.Surfaces.Count - 1; i >= 0; i--)
        {
            if (!bucket.Surfaces[i].TryGetTarget(out IWalkableDynamicSurface existing) ||
                existing == null || ReferenceEquals(existing, surface))
                bucket.Surfaces.RemoveAt(i);
        }
    }

    internal static void CopyActive(Room room, List<IWalkableDynamicSurface> destination)
    {
        destination.Clear();
        if (room == null) return;

        SurfaceBucket bucket = rooms.GetValue(room, _ => new SurfaceBucket());
        RefreshFromRoom(room, bucket);

        for (int i = bucket.Surfaces.Count - 1; i >= 0; i--)
        {
            if (!bucket.Surfaces[i].TryGetTarget(out IWalkableDynamicSurface surface) ||
                surface == null || !surface.SurfaceEnabled || surface.SurfaceRoom != room)
            {
                bucket.Surfaces.RemoveAt(i);
                continue;
            }

            destination.Add(surface);
        }
    }

    private static void RefreshFromRoom(Room room, SurfaceBucket bucket)
    {
        int updateCount = room.updateList?.Count ?? 0;
        if (bucket.LastRefreshTicker == room.ticker && bucket.LastUpdateListCount == updateCount) return;
        bucket.LastRefreshTicker = room.ticker;
        bucket.LastUpdateListCount = updateCount;

        if (room.updateList == null) return;
        for (int i = 0; i < room.updateList.Count; i++)
        {
            if (room.updateList[i] is IWalkableDynamicSurface surface &&
                surface.SurfaceEnabled && surface.SurfaceRoom == room)
                AddUnique(bucket, surface);
        }
    }

    private static void AddUnique(SurfaceBucket bucket, IWalkableDynamicSurface surface)
    {
        for (int i = bucket.Surfaces.Count - 1; i >= 0; i--)
        {
            if (!bucket.Surfaces[i].TryGetTarget(out IWalkableDynamicSurface existing) || existing == null)
            {
                bucket.Surfaces.RemoveAt(i);
                continue;
            }

            if (ReferenceEquals(existing, surface))
                return;
        }

        bucket.Surfaces.Add(new WeakReference<IWalkableDynamicSurface>(surface));
    }

    internal static void Reset() => rooms = new ConditionalWeakTable<Room, SurfaceBucket>();
}
