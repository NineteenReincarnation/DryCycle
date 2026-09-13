using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Map;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared creature-pipe catalog used by ordinary spawns and Lineage.
/// The visible World Map resolves CreatureHole mouths through WorldMapShortcutPresentation, so the
/// inspectors use the same node identities instead of building a second independent Den list.
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

    internal static List<Entry> Get(EditorMapRoomSnapshot room, int preserveNode = -1)
    {
        List<Entry> result = new();
        if (room == null) return result;

        WorldMapShortcutPresentation.ShortcutMarker[] exact =
            WorldMapShortcutPresentation.GetCreatureHoles(room.RoomIndex) ??
            Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();

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

        // Existing world.txt data must be lossless. Preserve its Den even if the exact cache has
        // not resolved that node this frame; never silently remap it to the first visible pipe.
        if (preserveNode >= 0 && !Contains(result, preserveNode))
        {
            EditorMapRoomNodeSnapshot node = FindNode(room, preserveNode);
            bool knownDen = IsCreatureDen(node);
            result.Add(new Entry(
                preserveNode,
                knownDen ? node.Type : "Node",
                false,
                !knownDen || hasExact));
        }

        result.Sort((a, b) => a.NodeIndex.CompareTo(b.NodeIndex));
        return result;
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
