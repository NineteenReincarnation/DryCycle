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
/// The currently loaded World is also kept in lockstep with editor mutations. This is deliberately
/// owned here rather than by individual UI commands: world.txt parsing, exact-route state and the
/// live AbstractRoom.connections array must advance as one transaction or the map can display a
/// connection that gameplay still cannot traverse.
/// </summary>
internal static class WorldConnectionSyntax
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>> LoadedRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<WorldConnectionEndpoint>> LiveAuthoredEndpoints =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WeakReference<global::World>> LoadedWorlds =
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
        lock (Sync)
        {
            LoadedRoutes.Clear();
            LiveAuthoredEndpoints.Clear();
            LoadedWorlds.Clear();
        }
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

    internal static bool TryGetLoadedWorld(string region, out global::World world)
    {
        world = null;
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0) return false;

        lock (Sync)
        {
            if (!LoadedWorlds.TryGetValue(regionKey, out WeakReference<global::World> weak) ||
                !weak.TryGetTarget(out global::World candidate) ||
                candidate == null)
            {
                LoadedWorlds.Remove(regionKey);
                return false;
            }

            if (!string.Equals(candidate.name, regionKey, StringComparison.OrdinalIgnoreCase))
                return false;

            world = candidate;
            return true;
        }
    }

    /// <summary>
    /// Synchronizes one source Exit after a live editor mutation. Plain room names deliberately
    /// remove an exact-route override and therefore fall back to vanilla target-exit resolution.
    /// The same mutation is applied to the currently loaded AbstractRoom.connections slot here so
    /// gameplay and the editable world document can never drift apart.
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
            if (!LiveAuthoredEndpoints.TryGetValue(regionKey, out HashSet<WorldConnectionEndpoint> authored))
            {
                authored = new HashSet<WorldConnectionEndpoint>();
                LiveAuthoredEndpoints.Add(regionKey, authored);
            }
            authored.Add(source);

            if (exact && destination.IsValid)
            {
                if (!LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> routes))
                {
                    routes = new Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint>();
                    LoadedRoutes.Add(regionKey, routes);
                }
                routes[source] = destination;
            }
            else if (LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> existing))
            {
                existing.Remove(source);
                if (existing.Count == 0) LoadedRoutes.Remove(regionKey);
            }
        }

        SynchronizeLoadedWorldEndpoint(regionKey, source, destinationToken);
    }

    /// <summary>
    /// Rebuilds one region's exact-route table from the editable world.txt document. Save/Reload is
    /// also a reconciliation boundary for endpoints touched by live authoring: if an older command
    /// path failed to update AbstractRoom.connections, this pass repairs it before the developer
    /// tests the pipe in the same running game.
    /// </summary>
    internal static void SynchronizeRegion(string region, WorldDocument document)
    {
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0 || document == null) return;

        Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> rebuilt = new();
        HashSet<WorldConnectionEndpoint> reconcile = new();
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
                {
                    rebuilt[source] = destination;
                    reconcile.Add(source);
                }
            }
        }

        lock (Sync)
        {
            if (LoadedRoutes.TryGetValue(regionKey, out Dictionary<WorldConnectionEndpoint, WorldConnectionEndpoint> previous))
            {
                foreach (WorldConnectionEndpoint source in previous.Keys)
                    reconcile.Add(source);
            }
            if (LiveAuthoredEndpoints.TryGetValue(regionKey, out HashSet<WorldConnectionEndpoint> authored))
            {
                foreach (WorldConnectionEndpoint source in authored)
                    reconcile.Add(source);
            }

            if (rebuilt.Count == 0) LoadedRoutes.Remove(regionKey);
            else LoadedRoutes[regionKey] = rebuilt;
        }

        foreach (WorldConnectionEndpoint source in reconcile)
        {
            if (!document.TryGetConnection(source.Room, source.NodeIndex, out string token))
                token = "DISCONNECTED";
            SynchronizeLoadedWorldEndpoint(regionKey, source, token);
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
        RegisterLoadedWorld(region, self.world);
        if (self.cntr == self.startOfWorldDefinition)
        {
            lock (Sync)
            {
                if (region.Length > 0)
                {
                    LoadedRoutes.Remove(region);
                    LiveAuthoredEndpoints.Remove(region);
                }
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

    private static void RegisterLoadedWorld(string region, global::World world)
    {
        string regionKey = NormalizeRegion(region);
        if (regionKey.Length == 0 || world == null) return;
        lock (Sync)
            LoadedWorlds[regionKey] = new WeakReference<global::World>(world);
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

    private static void SynchronizeLoadedWorldEndpoint(
        string region,
        WorldConnectionEndpoint source,
        string destinationToken)
    {
        if (!TryGetLoadedWorld(region, out global::World world)) return;

        AbstractRoom sourceRoom = world.GetAbstractRoom(source.Room);
        if (sourceRoom == null || source.NodeIndex < 0) return;

        int targetRoomIndex = -1;
        if (TryParseDestination(destinationToken, out string targetRoomName, out _) &&
            !string.IsNullOrWhiteSpace(targetRoomName) &&
            !string.Equals(targetRoomName, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            AbstractRoom targetRoom = world.GetAbstractRoom(targetRoomName);
            if (targetRoom == null)
            {
                global::DryCycle.Plugin.Logger?.LogWarning(
                    "WorldTopology live sync could not resolve room '" + targetRoomName + "'.");
                return;
            }
            targetRoomIndex = targetRoom.index;
        }

        if (sourceRoom.connections == null || source.NodeIndex >= sourceRoom.connections.Length)
        {
            int oldLength = sourceRoom.connections?.Length ?? 0;
            int nextLength = Math.Max(source.NodeIndex + 1, oldLength);
            int[] next = new int[nextLength];
            for (int i = 0; i < next.Length; i++) next[i] = -1;
            if (sourceRoom.connections != null)
                Array.Copy(sourceRoom.connections, next, sourceRoom.connections.Length);
            sourceRoom.connections = next;
        }

        int previousTarget = sourceRoom.connections[source.NodeIndex];
        sourceRoom.connections[source.NodeIndex] = targetRoomIndex;

        // ShortcutGraphics decides whether a RoomExit should show dots, a shelter/gate symbol, or
        // nothing at all when GenerateSprites() runs. Updating AbstractRoom.connections alone fixes
        // traversal but leaves an already realized/current room displaying the stale entrance symbol.
        // Rebuild only cameras that are actually showing the edited source room, and only when the
        // room-level target changed. NewRoom() is the vanilla lightweight presentation refresh: it
        // does not reload room geometry, shortcut paths, creatures, physics or AI.
        if (previousTarget != targetRoomIndex)
            RefreshRealizedShortcutGraphics(sourceRoom);
    }

    private static void RefreshRealizedShortcutGraphics(AbstractRoom sourceRoom)
    {
        global::Room realizedRoom = sourceRoom?.realizedRoom;
        if (realizedRoom?.game?.cameras == null) return;

        for (int i = 0; i < realizedRoom.game.cameras.Length; i++)
        {
            RoomCamera camera = realizedRoom.game.cameras[i];
            if (camera?.room != realizedRoom || camera.shortcutGraphics == null) continue;

            try
            {
                camera.shortcutGraphics.NewRoom();
            }
            catch (Exception error)
            {
                // A live authoring refresh must never destabilize the room if another mod has
                // temporarily replaced shortcut presentation state. Gameplay topology is already
                // synchronized above, so log and let the next normal camera-room refresh recover.
                global::DryCycle.Plugin.Logger?.LogWarning(
                    "WorldTopology could not refresh shortcut graphics for " +
                    (sourceRoom.name ?? "?") + ": " + error.Message);
            }
        }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
