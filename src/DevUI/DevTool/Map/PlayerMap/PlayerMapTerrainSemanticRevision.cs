using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Low-budget revision bridge for authored terrain that lives outside the static room-text bake.
/// Observations are explicitly scoped to a region so reused AbstractRoom indices can never carry a
/// previous region's terrain state into the next Player Map session.
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
    private static string region = string.Empty;
    private static int cursor;
    private static int revision = 1;

    internal static int Revision => revision;
    internal static string Region => region;

    /// <summary>
    /// Audits at most <paramref name="budget"/> rooms. A pending->ready transition is a semantic
    /// change too: it means Render may now safely consume the authored-terrain result.
    /// </summary>
    internal static void Audit(string regionName, PlayerMapRoomSnapshot[] rooms, int budget)
    {
        EnsureRegion(regionName);
        rooms ??= Array.Empty<PlayerMapRoomSnapshot>();
        if (rooms.Length == 0)
        {
            if (Observed.Count > 0 || cursor != 0)
            {
                Observed.Clear();
                cursor = 0;
                Bump();
            }
            return;
        }

        budget = Math.Max(1, Math.Min(rooms.Length, budget));
        bool changed = false;
        bool wrapped = false;
        for (int checkedRooms = 0; checkedRooms < budget; checkedRooms++)
        {
            if (cursor >= rooms.Length)
            {
                cursor = 0;
                wrapped = true;
            }

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

        if (cursor >= rooms.Length)
        {
            cursor = 0;
            wrapped = true;
        }

        // Remove stale ids only at a completed audit cycle. This bounds stable-frame work while
        // still guaranteeing exact cleanup after rooms are removed or a topology reload reshapes the
        // region.
        if (wrapped && Observed.Count > rooms.Length)
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

    /// <summary>
    /// Establishes a complete baseline before a frozen Render job starts. This prevents the guard
    /// itself from discovering untouched rooms on later frames and cancelling a render for a false
    /// positive rather than a real semantic change.
    /// </summary>
    internal static void AuditAll(string regionName, PlayerMapRoomSnapshot[] rooms)
    {
        rooms ??= Array.Empty<PlayerMapRoomSnapshot>();
        Audit(regionName, rooms, Math.Max(1, rooms.Length));
    }

    internal static void Reset()
    {
        bool hadState = Observed.Count > 0 || cursor != 0 || region.Length > 0;
        Observed.Clear();
        region = string.Empty;
        cursor = 0;
        if (hadState) Bump();
    }

    private static void EnsureRegion(string regionName)
    {
        string next = regionName ?? string.Empty;
        if (string.Equals(region, next, StringComparison.OrdinalIgnoreCase)) return;
        region = next;
        Observed.Clear();
        cursor = 0;
        Bump();
    }

    private static void Bump() => revision = revision >= int.MaxValue ? 1 : revision + 1;
}
