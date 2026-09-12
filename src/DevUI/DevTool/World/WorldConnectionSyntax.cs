using System;
using System.Collections.Generic;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Extended world.txt connection syntax:
///
///   ROOM_B      -> vanilla room target, target Exit chosen by vanilla rules.
///   <3>ROOM_B   -> exact target endpoint ROOM_B:3.
///
/// WorldLoader itself does not understand the prefix, so MappingRooms is intercepted long enough
/// to record exact endpoints and present a stripped vanilla-compatible line to the original loader.
/// The source world.txt remains authoritative and human-readable.
///
/// DevTool edits also update LoadedRoutes immediately. This is important because the currently
/// running World has already passed through WorldLoader; requiring a region reload just to test an
/// edited pipe would make live authoring unnecessarily slow.
/// </summary>
internal static class WorldConnectionSyntax
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>> LoadedRoutes =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        enabled = true;
        On.WorldLoader.MappingRooms += WorldLoader_MappingRooms;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        enabled = false;
        On.WorldLoader.MappingRooms -= WorldLoader_MappingRooms;
        lock (Sync) LoadedRoutes.Clear();
    }

    internal static bool TryParseDestination(
        string token,
        out string roomName,
        out int targetNode)
    {
        roomName = string.IsNullOrWhiteSpace(token) ? string.Empty : token.Trim();
        targetNode = -1;

        if (roomName.Length == 0 ||
            string.Equals(roomName, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return roomName.Length > 0;

        if (roomName[0] != '<') return true;
        int close = roomName.IndexOf('>');
        if (close <= 1 || close >= roomName.Length - 1) return false;
        if (!int.TryParse(roomName.Substring(1, close - 1), out targetNode) || targetNode < 0)
            return false;

        roomName = roomName.Substring(close + 1).Trim();
        return roomName.Length > 0;
    }

    internal static string FormatDestination(string roomName, int targetNode)
    {
        string room = string.IsNullOrWhiteSpace(roomName) ? "DISCONNECTED" : roomName.Trim();
        if (targetNode < 0 || string.Equals(room, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return room;
        return "<" + targetNode + ">" + room;
    }

    /// <summary>
    /// Used by the editor when an endpoint edge already exists. This keeps old vanilla text plain,
    /// while exact connections authored by the rebuilt WE are written as &lt;targetExit&gt;Room.
    /// </summary>
    internal static string FormatForSource(
        string region,
        string sourceRoom,
        int sourceNode,
        string destinationToken)
    {
        if (!TryParseDestination(destinationToken, out string destinationRoom, out int existingTarget))
            return destinationToken?.Trim() ?? "DISCONNECTED";
        if (existingTarget >= 0 ||
            string.Equals(destinationRoom, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
            return FormatDestination(destinationRoom, existingTarget);

        WorldConnectionEndpoint source = new(sourceRoom, sourceNode);
        WorldConnectionEdge[] edges = WorldTopologyRegistry.GetRegionEdges(region);
        for (int i = 0; i < edges.Length; i++)
        {
            WorldConnectionEdge edge = edges[i];
            if (edge.A.Equals(source) &&
                string.Equals(edge.B.Room, destinationRoom, StringComparison.OrdinalIgnoreCase))
                return FormatDestination(destinationRoom, edge.B.NodeIndex);
            if (edge.B.Equals(source) &&
                string.Equals(edge.A.Room, destinationRoom, StringComparison.OrdinalIgnoreCase))
                return FormatDestination(destinationRoom, edge.A.NodeIndex);
        }

        return destinationRoom;
    }

    internal static bool TryGetLoadedRoute(
        string region,
        string sourceRoom,
        int sourceNode,
        out WorldConnectionEndpoint destination)
    {
        destination = default;
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0) return false;

        lock (Sync)
        {
            return LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> routes) &&
                   routes.TryGetValue(new WorldConnectionEndpoint(sourceRoom, sourceNode), out destination);
        }
    }

    /// <summary>
    /// Synchronizes one source Exit after a live editor mutation. Plain room names deliberately
    /// remove an exact-route override and therefore fall back to vanilla target-exit resolution.
    /// </summary>
    internal static void SynchronizeRoute(
        string region,
        string sourceRoom,
        int sourceNode,
        string destinationToken)
    {
        string regionKey = NormalizeRegion(region);
        WorldConnectionEndpoint source = new(sourceRoom, sourceNode);
        if (regionKey.Length == 0 || !source.IsValid) return;

        bool exact = TryParseDestination(destinationToken, out string targetRoom, out int targetNode) &&
                     targetNode >= 0 &&
                     !string.Equals(targetRoom, "DISCONNECTED", StringComparison.OrdinalIgnoreCase);
        WorldConnectionEndpoint destination = exact
            ? new WorldConnectionEndpoint(targetRoom, targetNode)
            : default;

        lock (Sync)
        {
            if (exact && destination.IsValid)
            {
                if (!LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> routes))
                {
                    routes = new Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>();
                    LoadedRoutes.Add(regionKey, routes);
                }
                routes[source] = destination;
                return;
            }

            if (!LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> existing))
                return;
            existing.Remove(source);
            if (existing.Count == 0) LoadedRoutes.Remove(regionKey);
        }
    }

    /// <summary>
    /// Rebuilds one region's exact-route table from the editable world.txt document. This makes
    /// world.txt the authoritative topology source even when the region was loaded before DevTool
    /// opened or when Undo/Redo restored an older document state.
    /// </summary>
    internal static void SynchronizeRegion(string region, WorldDocument document)
    {
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0 || document == null) return;

        Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> rebuilt = new();
        foreach (KeyValuePair<string, WorldRoomRecord> roomPair in document.Rooms)
        {
            WorldRoomRecord room = roomPair.Value;
            if (room == null) continue;
            for (int sourceNode = 0; sourceNode < room.Connections.Count; sourceNode++)
            {
                string token = room.Connections[sourceNode];
                if (!TryParseDestination(token, out string targetRoom, out int targetNode) || targetNode < 0)
                    continue;

                WorldConnectionEndpoint source = new(room.Name, sourceNode);
                WorldConnectionEndpoint destination = new(targetRoom, targetNode);
                if (source.IsValid && destination.IsValid)
                    rebuilt[source] = destination;
            }
        }

        lock (Sync)
        {
            if (rebuilt.Count == 0) LoadedRoutes.Remove(regionKey);
            else LoadedRoutes[regionKey] = rebuilt;
        }
    }

    private static void WorldLoader_MappingRooms(
        On.WorldLoader.orig_MappingRooms orig,
        WorldLoader self)
    {
        if (self?.lines == null || self.cntr < 0 || self.cntr >= self.lines.Count)
        {
            orig(self);
            return;
        }

        string region = NormalizeRegion(self.worldName ?? self.world?.name);
        if (self.cntr == self.startOfWorldDefinition)
        {
            lock (Sync)
            {
                if (region.Length > 0) LoadedRoutes.Remove(region);
            }
        }

        string original = self.lines[self.cntr];
        if (!TryRewriteRoomLine(region, original, out string rewritten))
        {
            orig(self);
            return;
        }

        self.lines[self.cntr] = rewritten;
        try
        {
            orig(self);
        }
        finally
        {
            self.lines[self.cntr] = original;
        }
    }

    private static bool TryRewriteRoomLine(string region, string line, out string rewritten)
    {
        rewritten = line;
        if (string.IsNullOrEmpty(line) || !line.Contains(" : ")) return false;

        string[] parts = line.Split(new[] { " : " }, StringSplitOptions.None);
        if (parts.Length < 2) return false;
        string sourceRoom = parts[0]?.Trim() ?? string.Empty;
        if (sourceRoom.Length == 0 || string.IsNullOrWhiteSpace(parts[1])) return false;

        string[] rawConnections = parts[1].Split(',');
        bool changed = false;
        for (int sourceNode = 0; sourceNode < rawConnections.Length; sourceNode++)
        {
            string token = rawConnections[sourceNode].Trim();
            if (!TryParseDestination(token, out string targetRoom, out int targetNode) || targetNode < 0)
                continue;

            rawConnections[sourceNode] = targetRoom;
            changed = true;
            RegisterLoadedRoute(
                region,
                new WorldConnectionEndpoint(sourceRoom, sourceNode),
                new WorldConnectionEndpoint(targetRoom, targetNode));
        }

        if (!changed) return false;
        parts[1] = string.Join(", ", rawConnections);
        rewritten = string.Join(" : ", parts);
        return true;
    }

    private static void RegisterLoadedRoute(
        string region,
        WorldConnectionEndpoint source,
        WorldConnectionEndpoint destination)
    {
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0 || !source.IsValid || !destination.IsValid) return;
        lock (Sync)
        {
            if (!LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> routes))
            {
                routes = new Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>();
                LoadedRoutes.Add(regionKey, routes);
            }
            routes[source] = destination;
        }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
