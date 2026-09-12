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
        if (region.Length == 0 || !source.IsValid || !destination.IsValid) return;
        lock (Sync)
        {
            if (!LoadedRoutes.TryGetValue(region, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> routes))
            {
                routes = new Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>();
                LoadedRoutes.Add(region, routes);
            }
            routes[source] = destination;
        }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
