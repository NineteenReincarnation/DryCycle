using System;
using System.IO;

namespace DryCycle.DevUI.DevTool.World;

/// <summary>
/// Owns the editable lossless world.txt document for the region currently being authored.
/// Unknown sections and mod directives remain untouched because WorldDocument only regenerates
/// room/spawner lines that were actually changed by the editor.
/// </summary>
internal static class WorldTextRegistry
{
    private static WorldDocument document;
    private static int revision;

    internal static string LoadedRegion { get; private set; } = string.Empty;
    internal static string LoadedPath { get; private set; } = string.Empty;
    internal static string LoadError { get; private set; }
    internal static bool Dirty => document?.Dirty == true || WorldCreatureAuthoringHooks.AdditionalDirty;
    internal static int Revision => revision;

    internal static bool EnsureLoaded(string region)
    {
        // This method sits on several immediate-mode and map-topology hot paths. In the stable case
        // the caller already passes the active region id, so avoid Trim/ToUpperInvariant entirely.
        if (document != null &&
            !string.IsNullOrWhiteSpace(region) &&
            string.Equals(LoadedRegion, region, StringComparison.OrdinalIgnoreCase))
            return true;

        string normalized = NormalizeRegion(region);
        if (normalized.Length == 0) return false;

        if (document != null &&
            string.Equals(LoadedRegion, normalized, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Dirty)
        {
            LoadError = "Cannot switch world.txt documents while the current document has unsaved changes.";
            return false;
        }

        return Reload(normalized);
    }

    internal static bool Reload(string region)
    {
        if (Dirty) return false;

        string normalized = NormalizeRegion(region);
        LoadedRegion = normalized;
        LoadedPath = string.Empty;
        LoadError = null;
        document = null;
        BumpRevision();

        if (normalized.Length == 0)
        {
            LoadError = "Region id is empty.";
            return false;
        }

        try
        {
            string relativePath =
                "World" + Path.DirectorySeparatorChar +
                normalized + Path.DirectorySeparatorChar +
                "world_" + normalized + ".txt";
            string path = WorldAuthoringPathResolver.ResolveExistingSource(relativePath);

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                string readPath = WorldAuthoringPathResolver.ResolveReadPath(relativePath);
                LoadError = WorldAuthoringPathResolver.IsMergedCachePath(readPath)
                    ? "world_" + normalized + ".txt resolves only to generated mergedmods data; no lossless writable mod source was found."
                    : "world_" + normalized + ".txt could not be resolved to a writable source file.";
                return false;
            }

            LoadedPath = path;
            document = WorldDocument.Parse(File.ReadAllText(path), path);
            WorldConnectionSyntax.SynchronizeRegion(normalized, document);
            return true;
        }
        catch (Exception error)
        {
            LoadError = error.Message;
            document = null;
            global::DryCycle.Plugin.Logger?.LogWarning("WorldText load failed: " + error);
            return false;
        }
    }

    internal static bool HasRoom(string region, string roomName)
    {
        return EnsureLoaded(region) &&
               document != null &&
               document.TryGetRoom(roomName, out _);
    }

    internal static bool TryGetConnection(
        string region,
        string roomName,
        int exitIndex,
        out string destination)
    {
        destination = string.Empty;
        if (!EnsureLoaded(region) || document == null) return false;
        return document.TryGetConnection(roomName, exitIndex, out destination);
    }

    internal static bool TryGetConnectionEndpoint(
        string region,
        string roomName,
        int exitIndex,
        out string destinationRoom,
        out int destinationNode)
    {
        destinationRoom = string.Empty;
        destinationNode = -1;
        if (!TryGetConnection(region, roomName, exitIndex, out string token)) return false;
        if (!WorldConnectionSyntax.TryParseDestination(token, out destinationRoom, out destinationNode))
            return false;

        // Vanilla world.txt stores only the destination room for ordinary links. When the opposite
        // room contains one and only one reciprocal route back to this source Exit, that reciprocal
        // slot is the exact target node. Resolve it here so the editor never turns a perfectly
        // deterministic old-format link into "Room:?" merely because it lacks the extended <n>
        // prefix. Repeated room-to-room links intentionally remain unresolved for manual mapping.
        if (destinationNode < 0 &&
            !string.Equals(destinationRoom, "DISCONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            int reciprocal = FindUniqueReciprocalNode(roomName, exitIndex, destinationRoom);
            if (reciprocal >= 0) destinationNode = reciprocal;
        }

        return true;
    }

    private static int FindUniqueReciprocalNode(
        string sourceRoom,
        int sourceNode,
        string destinationRoom)
    {
        if (document == null ||
            string.IsNullOrWhiteSpace(sourceRoom) ||
            string.IsNullOrWhiteSpace(destinationRoom) ||
            !document.TryGetRoom(sourceRoom, out WorldRoomRecord source) ||
            !document.TryGetRoom(destinationRoom, out WorldRoomRecord target))
            return -1;

        // A plain reciprocal token cannot identify which source Exit it points to if the source
        // room itself has multiple links to the same destination room. In that case the mapping is
        // genuinely ambiguous and must stay editable as such in the UI.
        int sourceMultiplicity = 0;
        for (int i = 0; i < source.Connections.Count; i++)
        {
            if (!WorldConnectionSyntax.TryParseDestination(
                    source.Connections[i],
                    out string sourceDestination,
                    out _))
                continue;
            if (string.Equals(sourceDestination, destinationRoom, StringComparison.OrdinalIgnoreCase))
                sourceMultiplicity++;
        }

        int match = -1;
        for (int i = 0; i < target.Connections.Count; i++)
        {
            if (!WorldConnectionSyntax.TryParseDestination(
                    target.Connections[i],
                    out string backRoom,
                    out int backTargetNode))
                continue;
            if (!string.Equals(backRoom, sourceRoom, StringComparison.OrdinalIgnoreCase))
                continue;

            // Extended syntax can disambiguate repeated links explicitly. Plain vanilla syntax is
            // safe only when this source room has a single route to the destination room.
            if (backTargetNode >= 0)
            {
                if (backTargetNode != sourceNode) continue;
            }
            else if (sourceMultiplicity != 1)
            {
                continue;
            }

            if (match >= 0) return -1;
            match = i;
        }

        return match;
    }

    internal static bool TrySetConnection(
        string region,
        string roomName,
        int exitIndex,
        string destinationRoom,
        out string error)
    {
        error = null;
        if (!EnsureLoaded(region) || document == null)
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }

        if (!document.TryGetRoom(roomName, out _))
        {
            error = "Room '" + roomName + "' does not exist in the editable ROOMS section.";
            return false;
        }

        string storedDestination = WorldConnectionSyntax.FormatForSource(
            region,
            roomName,
            exitIndex,
            destinationRoom);
        bool changed = document.TrySetConnection(roomName, exitIndex, storedDestination);

        WorldConnectionSyntax.SynchronizeRoute(region, roomName, exitIndex, storedDestination);
        WorldTopologyLiveTraversalFix.OnConnectionChanged(region, roomName, exitIndex, storedDestination);
        if (changed)
        {
            BumpRevision();
            WorldTopologyRuntime.NotifyTopologyChanged();
        }
        return true;
    }

    internal static WorldCreatureSpawnRecord[] GetCreatureSpawns(string region, string roomName)
    {
        if (!EnsureLoaded(region) || document == null)
            return Array.Empty<WorldCreatureSpawnRecord>();
        return document.GetCreatureSpawns(roomName);
    }

    internal static bool TryAddCreatureSpawn(
        string region,
        string roomName,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline,
        out int spawnId,
        out string error)
    {
        spawnId = -1;
        error = null;
        if (!EnsureLoaded(region) || document == null)
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        if (!document.TryGetRoom(roomName, out _))
        {
            error = "Room '" + roomName + "' does not exist in the editable ROOMS section.";
            return false;
        }
        if (denNode < 0)
        {
            error = "A valid creature den node is required.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(creature))
        {
            error = "Creature id is empty.";
            return false;
        }

        if (!document.TryAddCreatureSpawn(
                roomName,
                denNode,
                creature,
                amount,
                spawnData,
                timelineFilter,
                excludeTimeline,
                out spawnId))
        {
            error = "Could not add the creature spawner to world.txt.";
            return false;
        }

        WorldCreatureAuthoringHooks.OnCreatureAdded(region, roomName);
        return true;
    }

    internal static bool TryUpdateCreatureSpawn(
        string region,
        int spawnId,
        int denNode,
        string creature,
        int amount,
        string spawnData,
        string timelineFilter,
        bool excludeTimeline,
        out string error)
    {
        error = null;
        if (!EnsureLoaded(region) || document == null)
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        string roomName = WorldCreatureAuthoringHooks.FindSpawnRoom(region, spawnId);
        if (!document.TryUpdateCreatureSpawn(
                spawnId,
                denNode,
                creature,
                amount,
                spawnData,
                timelineFilter,
                excludeTimeline))
        {
            error = "Could not update the creature spawner.";
            return false;
        }

        WorldCreatureAuthoringHooks.OnCreatureEdited(region, roomName);
        return true;
    }

    internal static bool TryDeleteCreatureSpawn(string region, int spawnId, out string error)
    {
        error = null;
        if (!EnsureLoaded(region) || document == null)
        {
            error = LoadError ?? "world.txt is unavailable.";
            return false;
        }
        string roomName = WorldCreatureAuthoringHooks.FindSpawnRoom(region, spawnId);
        if (!document.TryDeleteCreatureSpawn(spawnId))
        {
            error = "Could not find the creature spawner to delete.";
            return false;
        }

        WorldCreatureAuthoringHooks.OnCreatureEdited(region, roomName);
        return true;
    }

    internal static bool Save()
    {
        if (document == null || string.IsNullOrWhiteSpace(LoadedPath)) return false;

        string region = LoadedRegion;
        if (document.Dirty)
        {
            try
            {
                WorldAuthoringPathResolver.AtomicWriteAllText(
                    LoadedPath,
                    document.Serialize());
                document.MarkSaved(LoadedPath);

                WorldConnectionSyntax.SynchronizeRegion(region, document);
                WorldTopologyRuntime.NotifyTopologyChanged();
                LoadError = null;
            }
            catch (Exception error)
            {
                LoadError = error.Message;
                global::DryCycle.Plugin.Logger?.LogError("WorldText save failed: " + error);
                return false;
            }
        }
        else
        {
            WorldConnectionSyntax.SynchronizeRegion(region, document);
        }

        if (!WorldCreatureAuthoringHooks.SaveAdditional(region))
        {
            LoadError = "Lineage save failed.";
            return false;
        }

        LoadError = null;
        return true;
    }

    internal static void Clear()
    {
        if (Dirty) return;
        if (document != null || LoadedRegion.Length > 0 || LoadedPath.Length > 0)
            BumpRevision();
        document = null;
        LoadedRegion = string.Empty;
        LoadedPath = string.Empty;
        LoadError = null;
    }

    private static void BumpRevision()
    {
        unchecked
        {
            revision++;
            if (revision == int.MinValue) revision = 1;
        }
    }

    private static string NormalizeRegion(string region) =>
        string.IsNullOrWhiteSpace(region) ? string.Empty : region.Trim().ToUpperInvariant();
}
