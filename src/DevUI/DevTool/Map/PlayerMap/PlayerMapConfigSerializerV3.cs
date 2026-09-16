using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map.PlayerMap;

/// <summary>
/// Single authoritative serializer for the rebuilt Player Map workspace.
///
/// It preserves Rain World's map config contract while removing vanilla SaveMapConfig side effects:
/// no live RoomPanel, Def_Mat, or migration spline is translated during save. The complete file is
/// built from an immutable serialization snapshot and atomically replaced once.
///
/// Exact Connection records are generated from DryCycle's node-to-node topology and static room
/// shortcut bake. Repeated pipes between the same room pair therefore remain distinct and never use
/// AbstractRoom.ExitIndex/Array.IndexOf to guess the target node.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(PlayerMapRuntimePlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class PlayerMapConfigSerializerV3Plugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.PlayerMap.ConfigSerializerV3";
    public const string PluginName = "DryCycle Player Map Config Serializer V3";
    public const string PluginVersion = global::DryCycle.Plugin.Version;

    private void OnEnable() => PlayerMapConfigSerializerV3.Enable(Logger);
    private void OnDisable() => PlayerMapConfigSerializerV3.Disable();
}

internal static class PlayerMapConfigSerializerV3
{
    private delegate bool OrigSave(MapPage page, PlayerMapSessionState state, out string error);
    private delegate bool HookSave(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error);

    private sealed class RoomRecord
    {
        internal RoomPanel Panel;
        internal AbstractRoom Room;
        internal Vector2 Canonical;
        internal Vector2 Dev;
        internal bool Disabled;
    }

    private sealed class EndpointRoomData
    {
        internal RoomMapSource Source;
        internal RoomMapBake Bake;
    }

    private sealed class ConnectionRecord
    {
        internal int ARoomIndex;
        internal int ANode;
        internal string ARoom = string.Empty;
        internal int BRoomIndex;
        internal int BNode;
        internal string BRoom = string.Empty;
        internal Vector2 APosition;
        internal Vector2 BPosition;
        internal int ADirection;
        internal int BDirection;
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
            MethodInfo target = typeof(PlayerMapConfigSerializer).GetMethod(
                "Save",
                flags,
                null,
                new[] { typeof(MapPage), typeof(PlayerMapSessionState), typeof(string).MakeByRefType() },
                null);
            if (target == null)
                throw new MissingMethodException("PlayerMapConfigSerializer.Save was not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            ConstructorInfo constructor = hookType?.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            saveHook = constructor.Invoke(new object[] { target, SaveHookDelegate }) as IDisposable;
            if (saveHook == null)
                throw new InvalidOperationException("Player Map config serializer V3 hook was not created.");

            enabled = true;
            log?.LogInfo("Player Map deterministic config serializer V3 enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("Player Map config serializer V3 could not attach: " + Unwrap(error).Message);
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

    private static bool SaveHook(OrigSave orig, MapPage page, PlayerMapSessionState state, out string error) =>
        Save(page, state, out error);

    private static bool Save(MapPage page, PlayerMapSessionState state, out string error)
    {
        error = null;
        if (page?.world == null || state == null)
        {
            error = "MapPage or Player Map state is unavailable.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(page.filePath))
        {
            error = "MapPage.filePath is empty.";
            return false;
        }

        try
        {
            List<RoomRecord> rooms = CaptureRooms(page, state, out Vector2 canonAverage, out Vector2 devAverage);
            if (rooms.Count == 0)
            {
                error = "No rooms are available for map-config serialization.";
                return false;
            }

            EditorSession session = DevToolSessionHub.Current;
            if (session != null && ReferenceEquals(session.Owner?.activePage, page))
                MapEditorPresentationHub.Publish(session);

            if (!BuildConnectionRecords(page, rooms, out List<ConnectionRecord> connections, out error))
                return false;

            Dictionary<string, string> roomLines = BuildRoomLines(rooms, canonAverage, devAverage);
            List<string> defLines = BuildDefLines(state, canonAverage);
            List<string> connectionLines = BuildConnectionLines(connections);

            bool replaceStreams = page.world?.voidSpawnWorldAI?.worldMigrationStreams != null;
            List<string> streamLines = replaceStreams
                ? BuildStreamLines(page.world.voidSpawnWorldAI.worldMigrationStreams, canonAverage)
                : new List<string>();

            List<string> source = File.Exists(page.filePath)
                ? new List<string>(File.ReadAllLines(page.filePath))
                : new List<string>();
            List<string> output = new(
                source.Count + roomLines.Count + defLines.Count + connectionLines.Count + streamLines.Count + 2);
            HashSet<string> writtenRooms = new(StringComparer.OrdinalIgnoreCase);
            int lastRoomOutputIndex = -1;

            for (int i = 0; i < source.Count; i++)
            {
                string line = source[i] ?? string.Empty;
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("Def_Mat:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) ||
                    (replaceStreams &&
                     (trimmed.StartsWith("SpawnMigrationStream:", StringComparison.OrdinalIgnoreCase) ||
                      trimmed.StartsWith("SpawnMigrationStreamMidpoint:", StringComparison.OrdinalIgnoreCase))))
                    continue;

                if (TryRoomRecordName(line, out string roomName) && roomLines.TryGetValue(roomName, out string replacement))
                {
                    output.Add(replacement);
                    writtenRooms.Add(roomName);
                    lastRoomOutputIndex = output.Count - 1;
                    continue;
                }

                output.Add(line);
            }

            List<string> missingRooms = new();
            foreach (KeyValuePair<string, string> pair in roomLines)
                if (!writtenRooms.Contains(pair.Key)) missingRooms.Add(pair.Value);
            missingRooms.Sort(StringComparer.Ordinal);

            int insertion = lastRoomOutputIndex >= 0 ? lastRoomOutputIndex + 1 : 0;
            InsertBlock(output, ref insertion, missingRooms);
            InsertBlock(output, ref insertion, defLines);
            InsertBlock(output, ref insertion, connectionLines);
            if (replaceStreams && streamLines.Count > 0)
            {
                output.Insert(insertion++, string.Empty);
                InsertBlock(output, ref insertion, streamLines);
            }

            AtomicWriteAllLines(page.filePath, output);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static List<RoomRecord> CaptureRooms(
        MapPage page,
        PlayerMapSessionState state,
        out Vector2 canonAverage,
        out Vector2 devAverage)
    {
        List<RoomRecord> records = new();
        canonAverage = Vector2.zero;
        devAverage = Vector2.zero;

        HashSet<string> disabled = new(StringComparer.OrdinalIgnoreCase);
        if (page.world?.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrWhiteSpace(name)) disabled.Add(name);
            }
        }

        if (page.subNodes == null) return records;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            if (!state.Rooms.TryGetValue(room.index, out PlayerMapRoomState roomState)) continue;
            Vector2 canonical = PlayerMapWorkspaceRuntime.Effective(roomState, panel);
            records.Add(new RoomRecord
            {
                Panel = panel,
                Room = room,
                Canonical = canonical,
                Dev = panel.devPos,
                Disabled = disabled.Contains(room.name ?? string.Empty)
            });
            // Vanilla computes both averages over all RoomPanels, including hidden map rooms.
            canonAverage += canonical;
            devAverage += panel.devPos;
        }

        if (records.Count > 0)
        {
            canonAverage /= records.Count;
            devAverage /= records.Count;
        }
        return records;
    }

    private static Dictionary<string, string> BuildRoomLines(
        List<RoomRecord> rooms,
        Vector2 canonAverage,
        Vector2 devAverage)
    {
        Dictionary<string, string> lines = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rooms.Count; i++)
        {
            RoomRecord item = rooms[i];
            if (item.Disabled) continue;
            Vector2 canonical = item.Canonical - canonAverage;
            Vector2 dev = item.Dev - devAverage;
            string name = item.Room.name ?? string.Empty;
            string subregion = item.Room.subregionName ?? string.Empty;
            lines[name] = name + ": " +
                          F(canonical.x) + "><" + F(canonical.y) + "><" +
                          F(dev.x) + "><" + F(dev.y) + "><" +
                          Mathf.Clamp(item.Panel.layer, 0, 2).ToString(CultureInfo.InvariantCulture) + "><" +
                          subregion + "><" + item.Room.size.x.ToString(CultureInfo.InvariantCulture) + "><" +
                          item.Room.size.y.ToString(CultureInfo.InvariantCulture);
        }
        return lines;
    }

    private static List<string> BuildDefLines(PlayerMapSessionState state, Vector2 canonAverage)
    {
        List<string> result = new(state.DefaultMaterials.Count);
        for (int i = 0; i < state.DefaultMaterials.Count; i++)
        {
            PlayerMapDefMaterialState item = state.DefaultMaterials[i];
            // Match vanilla semantics without mutating live handles: geometry is centered with Canon
            // rooms, while the tiny editor panel position remains an independent UI coordinate.
            Vector2 a = item.A - canonAverage;
            Vector2 b = item.B - canonAverage;
            result.Add("Def_Mat: " + F(a.x) + "," + F(a.y) + "," +
                       F(b.x) + "," + F(b.y) + "," +
                       F(item.PanelPosition.x) + "," + F(item.PanelPosition.y) + "," +
                       (item.Air ? "1" : "0"));
        }
        return result;
    }

    private static bool BuildConnectionRecords(
        MapPage page,
        List<RoomRecord> roomRecords,
        out List<ConnectionRecord> records,
        out string error)
    {
        records = new List<ConnectionRecord>();
        error = null;

        EditorMapPresentationSnapshot map = MapEditorPresentationHub.Current;
        if (map?.Available != true ||
            !string.Equals(map.RegionName ?? string.Empty, page.world?.name ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            error = "Exact connection snapshot is unavailable for the current region.";
            return false;
        }

        Dictionary<int, RoomRecord> roomByIndex = new();
        for (int i = 0; i < roomRecords.Count; i++) roomByIndex[roomRecords[i].Room.index] = roomRecords[i];
        Dictionary<int, EndpointRoomData> endpointData = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        EditorMapConnectionSnapshot[] connections = map.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();

        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection == null ||
                !roomByIndex.TryGetValue(connection.FromRoomIndex, out RoomRecord fromRoom) ||
                !roomByIndex.TryGetValue(connection.ToRoomIndex, out RoomRecord toRoom) ||
                fromRoom.Disabled || toRoom.Disabled)
                continue;

            if (connection.Ambiguous || connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            {
                error = "Cannot serialize ambiguous repeated connection " +
                        (fromRoom.Room.name ?? connection.FromRoomIndex.ToString(CultureInfo.InvariantCulture)) + ":" +
                        connection.FromNodeIndex + " -> " +
                        (toRoom.Room.name ?? connection.ToRoomIndex.ToString(CultureInfo.InvariantCulture)) + ":" +
                        connection.ToNodeIndex + ". Resolve its exact target node first.";
                return false;
            }

            int aRoomIndex = connection.FromRoomIndex;
            int aNode = connection.FromNodeIndex;
            RoomRecord aRoom = fromRoom;
            int bRoomIndex = connection.ToRoomIndex;
            int bNode = connection.ToNodeIndex;
            RoomRecord bRoom = toRoom;
            if (aRoomIndex > bRoomIndex || (aRoomIndex == bRoomIndex && aNode > bNode))
            {
                Swap(ref aRoomIndex, ref bRoomIndex);
                Swap(ref aNode, ref bNode);
                RoomRecord temp = aRoom;
                aRoom = bRoom;
                bRoom = temp;
            }

            string key = aRoomIndex.ToString(CultureInfo.InvariantCulture) + ":" + aNode + "|" +
                         bRoomIndex.ToString(CultureInfo.InvariantCulture) + ":" + bNode;
            if (!seen.Add(key)) continue;

            if (!TryEndpoint(aRoom, aNode, endpointData, out Vector2 aPos, out int aDirection, out error) ||
                !TryEndpoint(bRoom, bNode, endpointData, out Vector2 bPos, out int bDirection, out error))
                return false;

            records.Add(new ConnectionRecord
            {
                ARoomIndex = aRoomIndex,
                ANode = aNode,
                ARoom = aRoom.Room.name ?? string.Empty,
                BRoomIndex = bRoomIndex,
                BNode = bNode,
                BRoom = bRoom.Room.name ?? string.Empty,
                APosition = aPos,
                BPosition = bPos,
                ADirection = aDirection,
                BDirection = bDirection
            });
        }

        records.Sort((x, y) =>
        {
            int value = x.ARoomIndex.CompareTo(y.ARoomIndex);
            if (value != 0) return value;
            value = x.BRoomIndex.CompareTo(y.BRoomIndex);
            if (value != 0) return value;
            value = x.ANode.CompareTo(y.ANode);
            return value != 0 ? value : x.BNode.CompareTo(y.BNode);
        });
        return true;
    }

    private static bool TryEndpoint(
        RoomRecord room,
        int nodeIndex,
        Dictionary<int, EndpointRoomData> cache,
        out Vector2 nodePosition,
        out int direction,
        out string error)
    {
        nodePosition = default;
        direction = 0;
        error = null;
        if (!cache.TryGetValue(room.Room.index, out EndpointRoomData data))
        {
            if (!RoomMapSourceLoader.TryLoad(
                    room.Room.name ?? string.Empty,
                    out RoomMapSource source,
                    out string sourcePath,
                    out string loadError))
            {
                error = (room.Room.name ?? room.Room.index.ToString(CultureInfo.InvariantCulture)) +
                        ": static room source could not be loaded for Connection metadata: " + loadError;
                return false;
            }
            RoomMapBake bake = RoomMapSemanticCompiler.Compile(
                room.Room.index,
                room.Room.name ?? string.Empty,
                source,
                sourcePath);
            data = new EndpointRoomData { Source = source, Bake = bake };
            cache.Add(room.Room.index, data);
        }

        if (!data.Bake.TryGetNodeAnchor(nodeIndex, out RoomMapNodeAnchorSnapshot anchor) ||
            anchor.Kind != RoomMapPixelKind.RoomExit)
        {
            error = (room.Room.name ?? room.Room.index.ToString(CultureInfo.InvariantCulture)) + ":" + nodeIndex +
                    " does not resolve to a static RoomExit anchor.";
            return false;
        }

        // Room.LocalCoordinateOfNode(Exit) returns ShortcutLeadingToNode(node).startCoord.
        // ShortcutMapper constructs that coordinate from the visible ShortcutEntrance tile, not the
        // terminal marker. Connection metadata therefore records the integer entrance/start tile.
        int startX = Mathf.FloorToInt(anchor.EntranceX);
        int startY = Mathf.FloorToInt(anchor.EntranceY);
        nodePosition = new Vector2(startX, startY);
        direction = ExitDirection(data.Source, startX, startY);
        return true;
    }

    private static int ExitDirection(RoomMapSource source, int x, int y)
    {
        int resultX = 0;
        int resultY = 0;
        int[] dx = { -1, 0, 1, 0 };
        int[] dy = { 0, -1, 0, 1 };
        for (int i = 0; i < 4; i++)
        {
            int nx = x + dx[i];
            int ny = y + dy[i];
            if (!source.Inside(nx, ny)) continue;
            if (source.Tile(nx, ny).Terrain == 1) continue;
            resultX = dx[i];
            resultY = dy[i];
            break;
        }

        // Same mapping used by RoomRepresentation.CreateMapTexture:
        // left=0, down=1, right=2, up=3. A degenerate (0,0) falls through to 3, like vanilla.
        if (resultX != 0) return resultX == -1 ? 0 : 2;
        return resultY == -1 ? 1 : 3;
    }

    private static List<string> BuildConnectionLines(List<ConnectionRecord> records)
    {
        List<string> lines = new(records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            ConnectionRecord item = records[i];
            lines.Add("Connection: " + item.ARoom + "," + item.BRoom + "," +
                      F(item.APosition.x) + "," + F(item.APosition.y) + "," +
                      F(item.BPosition.x) + "," + F(item.BPosition.y) + "," +
                      item.ADirection.ToString(CultureInfo.InvariantCulture) + "," +
                      item.BDirection.ToString(CultureInfo.InvariantCulture));
        }
        return lines;
    }

    private static List<string> BuildStreamLines(
        List<WorldSpawnMigrationStream> streams,
        Vector2 canonAverage)
    {
        List<string> result = new();
        if (streams == null) return result;

        for (int i = 0; i < streams.Count; i++)
        {
            WorldSpawnMigrationStream source = streams[i];
            if (source?.spline == null) continue;

            BezierSpline.Midpoint[] midpoints = new BezierSpline.Midpoint[source.spline.midpoints.Count];
            for (int m = 0; m < midpoints.Length; m++)
            {
                BezierSpline.Midpoint midpoint = source.spline.midpoints[m];
                midpoints[m] = new BezierSpline.Midpoint(
                    midpoint.pos - canonAverage,
                    midpoint.PerpendicularAngle,
                    midpoint.length1,
                    midpoint.length2);
            }

            BezierSpline spline = new(
                source.spline.posA - canonAverage,
                source.spline.handleA - canonAverage,
                source.spline.posB - canonAverage,
                source.spline.handleB - canonAverage,
                midpoints);
            WorldSpawnMigrationStream snapshot = new(spline, source.name)
            {
                nextStreamName = source.nextStreamName,
                layers = source.layers == null ? new[] { true, true, true } : (bool[])source.layers.Clone(),
                width = source.width,
                rate = source.rate,
                destRoom = source.destRoom
            };

            if (result.Count > 0) result.Add(string.Empty);
            result.Add("SpawnMigrationStream: " + snapshot.Serialize());
            List<string> mids = snapshot.SerializeMidpoints();
            for (int m = 0; m < mids.Count; m++)
                result.Add("SpawnMigrationStreamMidpoint: " + mids[m]);
        }
        return result;
    }

    private static void InsertBlock(List<string> output, ref int insertion, List<string> block)
    {
        if (block == null || block.Count == 0) return;
        output.InsertRange(insertion, block);
        insertion += block.Count;
    }

    private static bool TryRoomRecordName(string line, out string roomName)
    {
        roomName = null;
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
        roomName = key;
        return true;
    }

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void AtomicWriteAllLines(string target, IReadOnlyList<string> lines)
    {
        string directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = target + ".drycycle.tmp";
        File.WriteAllLines(temp, ToArray(lines));
        PlayerMapConfigSerializer.AtomicReplaceSingle(temp, target);
    }

    private static string[] ToArray(IReadOnlyList<string> lines)
    {
        string[] result = new string[lines.Count];
        for (int i = 0; i < lines.Count; i++) result[i] = lines[i] ?? string.Empty;
        return result;
    }

    private static void Swap(ref int a, ref int b)
    {
        int value = a;
        a = b;
        b = value;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
