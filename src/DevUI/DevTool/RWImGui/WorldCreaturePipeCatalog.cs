using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared creature-pipe catalog used by ordinary spawns and Lineage.
/// The visible World Map resolves CreatureHole mouths through WorldMapShortcutPresentation, so the
/// inspectors use the same node identities instead of building a second independent Den list.
///
/// This catalog is drawn from immediate-mode UI every frame. Stable rooms therefore keep an
/// immutable-by-convention cached List instead of allocating, sorting and rebuilding the same Den
/// list on every inspector draw. The cache is invalidated only when the room node structure or the
/// shortcut-presentation CreatureHole array actually changes.
/// </summary>
internal static class WorldCreaturePipeCatalog
{
    internal readonly struct Entry
    {
        internal Entry(int nodeIndex, string type, bool exactMouth, bool unresolved)
        {
            NodeIndex = nodeIndex;
            Type = string.IsNullOrWhiteSpace(type) ? "Den" : type;
            ExactMouth = exactMouth;
            Unresolved = unresolved;
        }

        internal int NodeIndex { get; }
        internal string Type { get; }
        internal bool ExactMouth { get; }
        internal bool Unresolved { get; }
    }

    private sealed class RoomCache
    {
        internal string RoomName = string.Empty;
        internal int NodeFingerprint;
        internal WorldMapShortcutPresentation.ShortcutMarker[] ExactMouths =
            Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        internal List<Entry> BaseEntries = new();
        internal readonly Dictionary<int, List<Entry>> PreserveVariants = new();
    }

    private const int MaxCachedRooms = 512;
    private static readonly Dictionary<int, RoomCache> roomCache = new();

    /// <summary>
    /// Returns a cached list. Callers must treat the returned list as read-only.
    /// </summary>
    internal static List<Entry> Get(EditorMapRoomSnapshot room, int preserveNode = -1)
    {
        if (room == null) return EmptyEntries;

        WorldMapShortcutPresentation.ShortcutMarker[] exact =
            WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex) ??
            Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        int nodeFingerprint = ComputeNodeFingerprint(room);

        if (!roomCache.TryGetValue(room.RoomIndex, out RoomCache cached) ||
            !string.Equals(cached.RoomName, room.Name ?? string.Empty, StringComparison.Ordinal) ||
            cached.NodeFingerprint != nodeFingerprint ||
            !ReferenceEquals(cached.ExactMouths, exact))
        {
            cached = Rebuild(room, exact, nodeFingerprint);
            if (roomCache.Count >= MaxCachedRooms && !roomCache.ContainsKey(room.RoomIndex))
                roomCache.Clear();
            roomCache[room.RoomIndex] = cached;
        }

        if (preserveNode < 0 || Contains(cached.BaseEntries, preserveNode))
            return cached.BaseEntries;

        if (cached.PreserveVariants.TryGetValue(preserveNode, out List<Entry> preserved))
            return preserved;

        preserved = new List<Entry>(cached.BaseEntries.Count + 1);
        preserved.AddRange(cached.BaseEntries);
        EditorMapRoomNodeSnapshot node = FindNode(room, preserveNode);
        bool knownDen = IsCreatureDen(node);
        bool hasExact = exact.Length > 0;
        preserved.Add(new Entry(
            preserveNode,
            knownDen ? node.Type : "Node",
            false,
            !knownDen || hasExact));
        preserved.Sort(CompareEntries);
        cached.PreserveVariants[preserveNode] = preserved;
        return preserved;
    }

    internal static bool Contains(IReadOnlyList<Entry> entries, int nodeIndex)
    {
        if (entries == null) return false;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].NodeIndex == nodeIndex) return true;
        return false;
    }

    internal static string Label(Entry entry)
    {
        string label = "#" + entry.NodeIndex + " · " + entry.Type;
        if (entry.Unresolved)
            label += " · " + DevToolUiSettings.T("未定位", "Unresolved");
        return label;
    }

    internal static string Label(IReadOnlyList<Entry> entries, int nodeIndex)
    {
        if (entries != null)
        {
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].NodeIndex == nodeIndex) return Label(entries[i]);
        }
        return "#" + nodeIndex + " · " + DevToolUiSettings.T("未定位", "Unresolved");
    }

    internal static void Clear() => roomCache.Clear();

    private static readonly List<Entry> EmptyEntries = new(0);

    private static RoomCache Rebuild(
        EditorMapRoomSnapshot room,
        WorldMapShortcutPresentation.ShortcutMarker[] exact,
        int nodeFingerprint)
    {
        List<Entry> result = new();
        bool hasExact = false;
        for (int i = 0; i < exact.Length; i++)
        {
            int nodeIndex = exact[i].NodeIndex;
            if (nodeIndex < 0 || Contains(result, nodeIndex)) continue;
            result.Add(new Entry(nodeIndex, NodeType(room, nodeIndex), true, false));
            hasExact = true;
        }

        // Exact shortcut parsing is incremental. Until it is ready, fall back to the actual
        // AbstractRoom Den/GarbageHoles nodes so the selector does not flash empty.
        if (!hasExact)
        {
            EditorMapRoomNodeSnapshot[] nodes = room.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            for (int i = 0; i < nodes.Length; i++)
            {
                EditorMapRoomNodeSnapshot node = nodes[i];
                if (!IsCreatureDen(node) || Contains(result, node.NodeIndex)) continue;
                result.Add(new Entry(node.NodeIndex, node.Type, false, false));
            }
        }

        result.Sort(CompareEntries);
        return new RoomCache
        {
            RoomName = room.Name ?? string.Empty,
            NodeFingerprint = nodeFingerprint,
            ExactMouths = exact,
            BaseEntries = result
        };
    }

    private static int ComputeNodeFingerprint(EditorMapRoomSnapshot room)
    {
        unchecked
        {
            int hash = 17;
            EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
            hash = hash * 31 + nodes.Length;
            for (int i = 0; i < nodes.Length; i++)
            {
                EditorMapRoomNodeSnapshot node = nodes[i];
                if (node == null)
                {
                    hash = hash * 31;
                    continue;
                }
                hash = hash * 31 + node.NodeIndex;
                hash = hash * 31 + (node.Exit ? 1 : 0);
                hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(node.Type ?? string.Empty);
            }
            return hash;
        }
    }

    private static int CompareEntries(Entry a, Entry b) => a.NodeIndex.CompareTo(b.NodeIndex);

    private static bool Contains(List<Entry> entries, int nodeIndex)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].NodeIndex == nodeIndex) return true;
        return false;
    }

    private static string NodeType(EditorMapRoomSnapshot room, int nodeIndex)
    {
        EditorMapRoomNodeSnapshot node = FindNode(room, nodeIndex);
        return !string.IsNullOrWhiteSpace(node?.Type) ? node.Type : "Den";
    }

    private static EditorMapRoomNodeSnapshot FindNode(EditorMapRoomSnapshot room, int nodeIndex)
    {
        EditorMapRoomNodeSnapshot[] nodes = room?.Nodes ?? Array.Empty<EditorMapRoomNodeSnapshot>();
        for (int i = 0; i < nodes.Length; i++)
            if (nodes[i]?.NodeIndex == nodeIndex) return nodes[i];
        return null;
    }

    private static bool IsCreatureDen(EditorMapRoomNodeSnapshot node)
    {
        if (node == null || node.Exit) return false;
        return string.Equals(node.Type, "Den", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(node.Type, "GarbageHoles", StringComparison.OrdinalIgnoreCase);
    }
}
