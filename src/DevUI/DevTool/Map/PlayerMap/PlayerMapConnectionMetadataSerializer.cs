using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Rebuilds map_XX.txt Connection: metadata from the rebuilt semantic room bake and the unified
/// exact world topology. Vanilla used RoomRepresentation.nodePositions/exitDirections, which can be
/// stale or ambiguous for repeated room-to-room pipes. This serializer keys every endpoint by
/// (room,node), recovers its authored shortcut mouth directly from room text, and writes one stable
/// metadata record per exact edge.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapConnectionMetadataSerializerPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.ConnectionMetadata";
    public const string PluginName = "DryCycle Player Map Connection Metadata";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapConnectionMetadataSerializer.Enable(Logger);
    private void OnDisable() => PlayerMapConnectionMetadataSerializer.Disable();
}

internal static class PlayerMapConnectionMetadataSerializer
{
    private delegate bool OrigSave(MapPage page, PlayerMapSessionState state, out string error);
    private delegate bool HookSave(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error);

    private sealed class RoomEndpointSource
    {
        internal string Name = string.Empty;
        internal RoomMapBake Bake;
        internal RoomMapSource Source;
    }

    private readonly struct ConnectionRecord
    {
        internal ConnectionRecord(string sortKey, string line)
        {
            SortKey = sortKey;
            Line = line;
        }

        internal string SortKey { get; }
        internal string Line { get; }
    }

    private static readonly HookSave SaveHookDelegate = SaveHook;
    private static IDisposable saveHook;
    private static ManualLogSource log;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            MethodInfo save = typeof(PlayerMapConfigSerializer).GetMethod(
                "Save",
                flags,
                null,
                new[]
                {
                    typeof(MapPage),
                    typeof(PlayerMapSessionState),
                    typeof(string).MakeByRefType()
                },
                null);
            if (save == null)
                throw new MissingMethodException("PlayerMapConfigSerializer.Save was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            saveHook = constructor.Invoke(new object[] { save, SaveHookDelegate }) as IDisposable;
            if (saveHook == null)
                throw new InvalidOperationException("Player Map connection-metadata hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map exact Connection metadata serializer enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map connection metadata serializer could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        try { saveHook?.Dispose(); }
        catch { }
        saveHook = null;
        enabled = false;
        log = null;
    }

    private static bool SaveHook(
        OrigSave orig,
        MapPage page,
        PlayerMapSessionState state,
        out string error)
    {
        if (!enabled)
            return orig(page, state, out error);

        string path = page?.filePath ?? string.Empty;
        bool existed = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        string[] original = Array.Empty<string>();
        if (existed)
        {
            try { original = File.ReadAllLines(path); }
            catch (Exception readError)
            {
                error = "Could not snapshot map config before save: " + readError.Message;
                return false;
            }
        }

        if (!orig(page, state, out error))
            return false;

        if (TryRewriteConnections(page, out string connectionError))
        {
            error = null;
            return true;
        }

        try
        {
            if (existed)
                AtomicWrite(path, original);
            else if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception rollbackError)
        {
            error = connectionError + " Rollback also failed: " + rollbackError.Message;
            log?.LogError(error);
            return false;
        }

        error = connectionError;
        return false;
    }

    private static bool TryRewriteConnections(MapPage page, out string error)
    {
        error = null;
        if (page?.world == null || string.IsNullOrWhiteSpace(page.filePath) || !File.Exists(page.filePath))
        {
            error = "Map config is unavailable while rebuilding Connection metadata.";
            return false;
        }

        EditorSession session = DevToolSessionHub.Current;
        if (session != null)
            MapEditorPresentationHub.Publish(session);
        EditorMapPresentationSnapshot snapshot = MapEditorPresentationHub.Current;
        if (snapshot?.Available != true ||
            !string.Equals(snapshot.RegionName ?? string.Empty, page.world.name ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            error = "Current exact topology snapshot is unavailable; Connection metadata was not saved.";
            return false;
        }

        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrWhiteSpace(name)) disabled.Add(name);
            }
        }

        Dictionary<int, RoomEndpointSource> sources = new();
        List<ConnectionRecord> records = new();
        HashSet<string> edgeKeys = new(StringComparer.Ordinal);
        EditorMapConnectionSnapshot[] connections = snapshot.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null) continue;

            AbstractRoom roomA = page.world.GetAbstractRoom(connection.FromRoomIndex);
            AbstractRoom roomB = page.world.GetAbstractRoom(connection.ToRoomIndex);
            if (roomA == null || roomB == null) continue;
            if (disabled.Contains(roomA.name ?? string.Empty) || disabled.Contains(roomB.name ?? string.Empty))
                continue;

            if (connection.Ambiguous || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            {
                error = "Connection " + (connection.ConnectionId ?? "<unnamed>") +
                        " is ambiguous. Exact target nodes are required before map metadata can be saved.";
                return false;
            }

            if (!TryGetSource(roomA, sources, out RoomEndpointSource sourceA, out error) ||
                !TryGetSource(roomB, sources, out RoomEndpointSource sourceB, out error))
                return false;

            if (!TryEndpoint(sourceA, connection.FromNodeIndex, out int ax, out int ay, out int adir, out error) ||
                !TryEndpoint(sourceB, connection.ToNodeIndex, out int bx, out int by, out int bdir, out error))
                return false;

            string endpointA = EndpointKey(roomA.name, connection.FromNodeIndex);
            string endpointB = EndpointKey(roomB.name, connection.ToNodeIndex);
            string low = string.Compare(endpointA, endpointB, StringComparison.OrdinalIgnoreCase) <= 0 ? endpointA : endpointB;
            string high = string.Equals(low, endpointA, StringComparison.OrdinalIgnoreCase) ? endpointB : endpointA;
            string edgeKey = low + "|" + high;
            if (!edgeKeys.Add(edgeKey)) continue;

            bool swap = string.Compare(endpointA, endpointB, StringComparison.OrdinalIgnoreCase) > 0;
            string firstRoom = swap ? roomB.name : roomA.name;
            string secondRoom = swap ? roomA.name : roomB.name;
            int firstX = swap ? bx : ax;
            int firstY = swap ? by : ay;
            int secondX = swap ? ax : bx;
            int secondY = swap ? ay : by;
            int firstDir = swap ? bdir : adir;
            int secondDir = swap ? adir : bdir;

            string line = "Connection: " + firstRoom + "," + secondRoom + "," +
                          firstX.ToString(CultureInfo.InvariantCulture) + "," +
                          firstY.ToString(CultureInfo.InvariantCulture) + "," +
                          secondX.ToString(CultureInfo.InvariantCulture) + "," +
                          secondY.ToString(CultureInfo.InvariantCulture) + "," +
                          firstDir.ToString(CultureInfo.InvariantCulture) + "," +
                          secondDir.ToString(CultureInfo.InvariantCulture);
            records.Add(new ConnectionRecord(edgeKey, line));
        }

        records.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.Ordinal));
        string[] current = File.ReadAllLines(page.filePath);
        List<string> output = new(current.Length + records.Count);
        int insertion = -1;

        for (int i = 0; i < current.Length; i++)
        {
            string line = current[i] ?? string.Empty;
            if (line.TrimStart().StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                continue;

            output.Add(line);
            if (line.TrimStart().StartsWith("Def_Mat:", StringComparison.OrdinalIgnoreCase) || IsRoomRecord(line))
                insertion = output.Count;
        }

        if (insertion < 0) insertion = 0;
        if (records.Count > 0)
        {
            string[] connectionLines = new string[records.Count];
            for (int i = 0; i < records.Count; i++) connectionLines[i] = records[i].Line;
            output.InsertRange(insertion, connectionLines);
        }

        AtomicWrite(page.filePath, output.ToArray());
        return true;
    }

    private static bool TryGetSource(
        AbstractRoom room,
        Dictionary<int, RoomEndpointSource> cache,
        out RoomEndpointSource source,
        out string error)
    {
        error = null;
        if (cache.TryGetValue(room.index, out source)) return true;

        if (!RoomMapBakeCache.TryGetReady(room.index, out RoomMapBake bake) || bake == null)
        {
            RoomMapBakeSnapshot status = RoomMapBakeCache.GetSnapshot(room.index);
            error = (room.name ?? room.index.ToString()) + ": room bake is " + status.Status +
                    (string.IsNullOrWhiteSpace(status.Error) ? "." : " (" + status.Error + ")");
            source = null;
            return false;
        }

        if (!RoomMapSourceLoader.TryLoad(room.name ?? string.Empty, out RoomMapSource roomSource, out _, out string loadError))
        {
            error = (room.name ?? room.index.ToString()) + ": could not load shortcut source (" + loadError + ").";
            source = null;
            return false;
        }

        source = new RoomEndpointSource
        {
            Name = room.name ?? string.Empty,
            Bake = bake,
            Source = roomSource
        };
        cache.Add(room.index, source);
        return true;
    }

    private static bool TryEndpoint(
        RoomEndpointSource source,
        int nodeIndex,
        out int x,
        out int y,
        out int direction,
        out string error)
    {
        x = 0;
        y = 0;
        direction = -1;
        error = null;

        if (source?.Bake == null || !source.Bake.TryGetNodeAnchor(nodeIndex, out RoomMapNodeAnchorSnapshot anchor) ||
            anchor.Kind != RoomMapPixelKind.RoomExit)
        {
            error = (source?.Name ?? "?") + ":" + nodeIndex + " has no exact RoomExit anchor.";
            return false;
        }

        x = Mathf.FloorToInt(anchor.EntranceX);
        y = Mathf.FloorToInt(anchor.EntranceY);
        if (!source.Source.Inside(x, y))
        {
            error = source.Name + ":" + nodeIndex + " shortcut mouth is outside room bounds.";
            return false;
        }

        // Rain World Custom.fourDirections / ShorcutEntranceHoleDirection order:
        // left, down, right, up. The first adjacent non-solid tile is the saved map direction.
        int[] dx = { -1, 0, 1, 0 };
        int[] dy = { 0, -1, 0, 1 };
        for (int i = 0; i < 4; i++)
        {
            int nx = x + dx[i];
            int ny = y + dy[i];
            if (!source.Source.Inside(nx, ny)) continue;
            if (source.Source.Tile(nx, ny).Terrain == 1) continue;
            direction = i;
            return true;
        }

        error = source.Name + ":" + nodeIndex + " has no non-solid shortcut entrance direction.";
        return false;
    }

    private static string EndpointKey(string room, int node) =>
        (room ?? string.Empty).Trim().ToUpperInvariant() + ":" + node.ToString(CultureInfo.InvariantCulture);

    private static bool IsRoomRecord(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        int colon = line.IndexOf(':');
        if (colon <= 0) return false;
        string key = line.Substring(0, colon).Trim();
        if (key.Equals("Def_Mat", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("SpawnMigrationStream", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("SpawnMigrationStreamMidpoint", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Room_Attr", StringComparison.OrdinalIgnoreCase))
            return false;
        return line.IndexOf("><", StringComparison.Ordinal) >= 0;
    }

    private static void AtomicWrite(string target, string[] lines)
    {
        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temp = target + ".connections.drycycle.tmp";
        File.WriteAllLines(temp, lines ?? Array.Empty<string>());
        PlayerMapConfigSerializer.AtomicReplaceSingle(temp, target);
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
