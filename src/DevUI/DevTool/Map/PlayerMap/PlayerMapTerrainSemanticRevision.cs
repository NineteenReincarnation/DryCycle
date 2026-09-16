using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Low-budget revision bridge for authored terrain that lives outside the static room text bake.
/// The World Map geometry cache already rebuilds TerrainHandle/LocalTerrain/CurvedSlope/SuperSlope
/// and DryCycle custom terrain into semantic fill runs. This tracker observes only that semantic
/// revision, in small batches, so Player Map does not scan every room on stable frames.
/// </summary>
internal static class PlayerMapTerrainSemanticRevision
{
    private readonly struct ObservedState : IEquatable<ObservedState>
    {
        internal ObservedState(bool ready, int semanticRevision)
        {
            Ready = ready;
            SemanticRevision = semanticRevision;
        }

        internal bool Ready { get; }
        internal int SemanticRevision { get; }

        public bool Equals(ObservedState other) =>
            Ready == other.Ready && SemanticRevision == other.SemanticRevision;
    }

    private static readonly Dictionary<int, ObservedState> Observed = new();
    private static int cursor;
    private static int revision = 1;

    internal static int Revision => revision;

    /// <summary>
    /// Audits at most <paramref name="budget"/> rooms. A pending->ready transition is a semantic
    /// change too: it means Render may now safely consume the authored terrain result.
    /// </summary>
    internal static void Audit(PlayerMapRoomSnapshot[] rooms, int budget)
    {
        rooms ??= Array.Empty<PlayerMapRoomSnapshot>();
        if (rooms.Length == 0)
        {
            if (Observed.Count > 0)
            {
                Observed.Clear();
                cursor = 0;
                Bump();
            }
            return;
        }

        budget = Math.Max(1, Math.Min(rooms.Length, budget));
        bool changed = false;
        for (int checkedRooms = 0; checkedRooms < budget; checkedRooms++)
        {
            if (cursor >= rooms.Length) cursor = 0;
            PlayerMapRoomSnapshot room = rooms[cursor++];
            if (room == null) continue;

            bool ready = MapRoomGeometryPresentationHub.TryGetPlayerMapTerrainFillRuns(
                room.RoomIndex,
                out _,
                out int semanticRevision);
            ObservedState next = new(ready, ready ? semanticRevision : 0);
            if (!Observed.TryGetValue(room.RoomIndex, out ObservedState previous) || !previous.Equals(next))
            {
                Observed[room.RoomIndex] = next;
                changed = true;
            }
        }

        // Remove stale room ids only at the end of a full audit cycle. This keeps stable-frame work
        // bounded and avoids allocating a HashSet every frame.
        if (cursor == 0 && Observed.Count > rooms.Length)
        {
            HashSet<int> alive = new();
            for (int i = 0; i < rooms.Length; i++)
                if (rooms[i] != null) alive.Add(rooms[i].RoomIndex);
            List<int> stale = new();
            foreach (int roomIndex in Observed.Keys)
                if (!alive.Contains(roomIndex)) stale.Add(roomIndex);
            for (int i = 0; i < stale.Count; i++) Observed.Remove(stale[i]);
            changed |= stale.Count > 0;
        }

        if (changed) Bump();
    }

    internal static void Reset()
    {
        if (Observed.Count == 0 && cursor == 0) return;
        Observed.Clear();
        cursor = 0;
        Bump();
    }

    private static void Bump() => revision = revision >= int.MaxValue ? 1 : revision + 1;
}
