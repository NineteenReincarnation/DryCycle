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
using DryCycle.DevUI.DevTool.World;
using ImGuiNET;
using RWCustom;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Exact shortcut-mouth resolver for the World Map.
///
/// MapTex tells us where RoomExit / CreatureHole entrance pixels are, but it does not encode which
/// RoomExit pixel belongs to which abstract node. Pairing those pixels by scan order is therefore
/// fundamentally unsafe. Rain World resolves the identity by following the authored shortcut tunnel
/// from its visible entrance to the shortCut=2/3/5 terminal tile and indexing that terminal in the
/// room's node table. This plugin mirrors that lightweight part of ShortcutMapper directly from the
/// room .txt data, without realizing every room.
/// </summary>
[BepInPlugin(PluginId, PluginName, PluginVersion)]
[BepInDependency(WorldMapPresentationCorrectnessPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency(WorldMapPipeLayerPlugin.PluginId, BepInDependency.DependencyFlags.HardDependency)]
public sealed class WorldMapExactShortcutPlugin : BaseUnityPlugin
{
    public const string PluginId = "DryCycle.DevTool.RWImGui.WorldMap.ExactShortcuts";
    public const string PluginName = "DryCycle DevTool World Map Exact Shortcuts";
    public const string PluginVersion = BridgePlugin.PluginVersion;

    private void OnEnable() => WorldMapExactShortcuts.Enable(Logger);
    private void OnDisable() => WorldMapExactShortcuts.Disable();
}

internal static class WorldMapExactShortcuts
{
    private const int RoomsPerFrame = 12;
    private const int StructurePollFrames = 120;
    private const int FilePollFrames = 240;
    private const int ShortcutGuard = 1000;

    private delegate void OrigDrawCanvas(EditorMapPresentationSnapshot snapshot);
    private delegate void HookDrawCanvas(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot);

    private delegate bool OrigTryGetExitMouth(
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker);
    private delegate bool HookTryGetExitMouth(
        OrigTryGetExitMouth orig,
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker);

    private delegate WorldMapShortcutPresentation.ShortcutMarker[] OrigGetCreatureHoles(int roomIndex);
    private delegate WorldMapShortcutPresentation.ShortcutMarker[] HookGetCreatureHoles(
        OrigGetCreatureHoles orig,
        int roomIndex);

    private static readonly HookDrawCanvas DrawCanvasHookDelegate = DrawCanvasHook;
    private static readonly HookTryGetExitMouth TryGetExitMouthHookDelegate = TryGetExitMouthHook;
    private static readonly HookGetCreatureHoles GetCreatureHolesHookDelegate = GetCreatureHolesHook;

    private sealed class Entry
    {
        internal int RoomIndex;
        internal AbstractRoom Room;
        internal string FilePath = string.Empty;
        internal DateTime FileWriteUtc;
        internal int NextPollFrame;
        internal bool Ready;
        internal bool FromRealizedRoom;
        internal readonly Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker> ExitMouths = new();
        internal WorldMapShortcutPresentation.ShortcutMarker[] CreatureHoles =
            Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
    }

    private readonly struct TileInfo
    {
        internal TileInfo(bool shortcutEntrance, int shortcut)
        {
            ShortcutEntrance = shortcutEntrance;
            Shortcut = shortcut;
        }

        internal bool ShortcutEntrance { get; }
        internal int Shortcut { get; }
    }

    private static ManualLogSource log;
    private static IDisposable drawCanvasHook;
    private static IDisposable exitMouthHook;
    private static IDisposable creatureHoleHook;
    private static FieldInfo selectedConnectionIdField;

    private static readonly Dictionary<int, Entry> entries = new();
    private static readonly List<int> roomOrder = new();
    private static string region = string.Empty;
    private static int lastSubNodeCount = -1;
    private static int nextStructurePollFrame;
    private static int backgroundCursor;
    private static int lastUpdateFrame = -1;
    private static bool enabled;

    internal static void Enable(ManualLogSource logger)
    {
        if (enabled) return;
        log = logger;

        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            Type mapType = typeof(WorldMapView);
            Type shortcutType = typeof(WorldMapShortcutPresentation);

            MethodInfo drawCanvas = mapType.GetMethod(
                "DrawCanvas",
                flags,
                null,
                new[] { typeof(EditorMapPresentationSnapshot) },
                null);
            MethodInfo tryGetExit = shortcutType.GetMethod(
                "TryGetExitMouth",
                flags,
                null,
                new[]
                {
                    typeof(int),
                    typeof(int),
                    typeof(WorldMapShortcutPresentation.ShortcutMarker).MakeByRefType()
                },
                null);
            MethodInfo getCreatureHoles = shortcutType.GetMethod(
                "GetCreatureHoles",
                flags,
                null,
                new[] { typeof(int) },
                null);

            selectedConnectionIdField = mapType.GetField("selectedConnectionId", flags);

            if (drawCanvas == null || tryGetExit == null || getCreatureHoles == null ||
                selectedConnectionIdField == null)
                throw new MissingMemberException("World Map exact shortcut targets were not found.");

            Type hookType = Type.GetType("MonoMod.RuntimeDetour.Hook, MonoMod.RuntimeDetour", throwOnError: false);
            if (hookType == null)
                throw new TypeLoadException("MonoMod.RuntimeDetour.Hook is unavailable.");
            ConstructorInfo constructor = hookType.GetConstructor(new[] { typeof(MethodBase), typeof(Delegate) });
            if (constructor == null)
                throw new MissingMethodException("MonoMod.RuntimeDetour.Hook(MethodBase, Delegate) is unavailable.");

            drawCanvasHook = constructor.Invoke(new object[] { drawCanvas, DrawCanvasHookDelegate }) as IDisposable;
            exitMouthHook = constructor.Invoke(new object[] { tryGetExit, TryGetExitMouthHookDelegate }) as IDisposable;
            creatureHoleHook = constructor.Invoke(new object[] { getCreatureHoles, GetCreatureHolesHookDelegate }) as IDisposable;

            if (drawCanvasHook == null || exitMouthHook == null || creatureHoleHook == null)
                throw new InvalidOperationException("One or more exact shortcut hooks were not created.");

            enabled = true;
            log?.LogInfo("World Map exact shortcut resolver enabled.");
        }
        catch (Exception error)
        {
            Disable();
            logger?.LogWarning("World Map exact shortcut resolver could not attach: " + Unwrap(error).Message);
        }
    }

    internal static void Disable()
    {
        DisposeHook(ref creatureHoleHook);
        DisposeHook(ref exitMouthHook);
        DisposeHook(ref drawCanvasHook);
        selectedConnectionIdField = null;
        entries.Clear();
        roomOrder.Clear();
        region = string.Empty;
        lastSubNodeCount = -1;
        nextStructurePollFrame = 0;
        backgroundCursor = 0;
        lastUpdateFrame = -1;
        enabled = false;
        log = null;
    }

    private static void DrawCanvasHook(OrigDrawCanvas orig, EditorMapPresentationSnapshot snapshot)
    {
        if (enabled && snapshot?.Available == true)
            UpdateExactCache(DevToolRuntime.ActiveSession, snapshot.SelectedRoomIndex);

        orig(snapshot);

        if (enabled && snapshot?.Available == true)
            HandleConnectionDeleteShortcut(snapshot);
    }

    private static bool TryGetExitMouthHook(
        OrigTryGetExitMouth orig,
        int roomIndex,
        int nodeIndex,
        out WorldMapShortcutPresentation.ShortcutMarker marker)
    {
        marker = default;
        if (enabled && entries.TryGetValue(roomIndex, out Entry entry) && entry.Ready)
            return entry.ExitMouths.TryGetValue(nodeIndex, out marker);
        return orig(roomIndex, nodeIndex, out marker);
    }

    private static WorldMapShortcutPresentation.ShortcutMarker[] GetCreatureHolesHook(
        OrigGetCreatureHoles orig,
        int roomIndex)
    {
        if (enabled && entries.TryGetValue(roomIndex, out Entry entry) && entry.Ready)
            return entry.CreatureHoles ?? Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        return orig(roomIndex);
    }

    private static void UpdateExactCache(EditorSession session, int selectedRoomIndex)
    {
        if (lastUpdateFrame == Time.frameCount) return;
        lastUpdateFrame = Time.frameCount;

        if (session?.Owner?.activePage is not MapPage page || page.world == null)
        {
            ClearForNoMap();
            return;
        }

        string nextRegion = page.world.name ?? string.Empty;
        bool regionChanged = !string.Equals(region, nextRegion, StringComparison.OrdinalIgnoreCase);
        if (regionChanged)
        {
            entries.Clear();
            roomOrder.Clear();
            region = nextRegion;
            lastSubNodeCount = -1;
            nextStructurePollFrame = 0;
            backgroundCursor = 0;
        }

        if (regionChanged || entries.Count == 0 || page.subNodes.Count != lastSubNodeCount ||
            Time.frameCount >= nextStructurePollFrame)
            SynchronizeStructure(page);

        int budget = RoomsPerFrame;
        int currentRoom = session.Room?.abstractRoom?.index ?? -1;
        if (RefreshRoom(currentRoom, page.world, force: true)) budget--;
        if (selectedRoomIndex != currentRoom && budget > 0 &&
            RefreshRoom(selectedRoomIndex, page.world, force: true))
            budget--;

        int count = roomOrder.Count;
        if (count == 0 || budget <= 0) return;

        int visited = 0;
        while (budget > 0 && visited < count)
        {
            if (backgroundCursor >= count) backgroundCursor = 0;
            int roomIndex = roomOrder[backgroundCursor++];
            visited++;
            if (roomIndex == currentRoom || roomIndex == selectedRoomIndex) continue;
            if (RefreshRoom(roomIndex, page.world, force: false)) budget--;
        }
    }

    private static void SynchronizeStructure(MapPage page)
    {
        HashSet<int> alive = new();
        roomOrder.Clear();

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            alive.Add(room.index);
            roomOrder.Add(room.index);

            if (!entries.TryGetValue(room.index, out Entry entry))
            {
                entry = new Entry { RoomIndex = room.index };
                entries.Add(room.index, entry);
            }
            entry.Room = room;
        }

        if (entries.Count != alive.Count)
        {
            List<int> stale = new();
            foreach (int roomIndex in entries.Keys)
                if (!alive.Contains(roomIndex)) stale.Add(roomIndex);
            for (int i = 0; i < stale.Count; i++) entries.Remove(stale[i]);
        }

        if (backgroundCursor >= roomOrder.Count) backgroundCursor = 0;
        lastSubNodeCount = page.subNodes.Count;
        nextStructurePollFrame = Time.frameCount + StructurePollFrames;
    }

    private static bool RefreshRoom(int roomIndex, global::World world, bool force)
    {
        if (roomIndex < 0 || !entries.TryGetValue(roomIndex, out Entry entry) || entry.Room == null)
            return false;

        global::Room realized = entry.Room.realizedRoom;
        if (realized?.shortcuts != null && realized.shortcuts.Length > 0)
        {
            if (!entry.Ready || !entry.FromRealizedRoom || force)
                BuildFromRealized(entry, realized);
            return true;
        }

        if (entry.FromRealizedRoom)
        {
            entry.Ready = false;
            entry.FromRealizedRoom = false;
            entry.NextPollFrame = 0;
        }

        if (entry.Ready && !force && Time.frameCount < entry.NextPollFrame)
            return false;

        string roomName = WorldLoader.RoomNameManipulator(entry.Room.FileName, world?.game);
        string path = WorldLoader.FindRoomFile(roomName, includeRootDirectory: false, ".txt", showWarning: false);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            entry.NextPollFrame = Time.frameCount + FilePollFrames;
            return false;
        }

        DateTime writeTime;
        try { writeTime = File.GetLastWriteTimeUtc(path); }
        catch { writeTime = DateTime.MinValue; }

        if (entry.Ready && string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase) &&
            entry.FileWriteUtc == writeTime)
        {
            entry.NextPollFrame = Time.frameCount + FilePollFrames + Math.Abs(roomIndex % 37);
            return false;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            RoomPreprocessor.VersionFix(ref lines);
            if (!TryParseRoomShortcuts(lines, out Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker> exits,
                    out WorldMapShortcutPresentation.ShortcutMarker[] creatureHoles))
            {
                entry.NextPollFrame = Time.frameCount + FilePollFrames;
                return false;
            }

            entry.ExitMouths.Clear();
            foreach (KeyValuePair<int, WorldMapShortcutPresentation.ShortcutMarker> pair in exits)
                entry.ExitMouths[pair.Key] = pair.Value;
            entry.CreatureHoles = creatureHoles;
            entry.FilePath = path;
            entry.FileWriteUtc = writeTime;
            entry.Ready = true;
            entry.FromRealizedRoom = false;
            entry.NextPollFrame = Time.frameCount + FilePollFrames + Math.Abs(roomIndex % 37);
            return true;
        }
        catch (Exception error)
        {
            entry.NextPollFrame = Time.frameCount + FilePollFrames;
            log?.LogDebug("Exact shortcut parse failed for " + (entry.Room.name ?? roomIndex.ToString()) + ": " + error.Message);
            return false;
        }
    }

    private static void BuildFromRealized(Entry entry, global::Room room)
    {
        entry.ExitMouths.Clear();
        List<WorldMapShortcutPresentation.ShortcutMarker> holes = new();
        ShortcutData[] shortcuts = room.shortcuts ?? Array.Empty<ShortcutData>();
        for (int i = 0; i < shortcuts.Length; i++)
        {
            ShortcutData shortcut = shortcuts[i];
            WorldMapShortcutPresentation.ShortcutMarker marker = new(
                shortcut.StartTile.x + 0.5f,
                shortcut.StartTile.y + 0.5f,
                shortcut.destNode);
            if (shortcut.shortCutType == ShortcutData.Type.RoomExit && shortcut.destNode >= 0)
                entry.ExitMouths[shortcut.destNode] = marker;
            else if (shortcut.shortCutType == ShortcutData.Type.CreatureHole)
                holes.Add(marker);
        }

        entry.CreatureHoles = holes.ToArray();
        entry.Ready = true;
        entry.FromRealizedRoom = true;
        entry.NextPollFrame = Time.frameCount + 30;
    }

    private static bool TryParseRoomShortcuts(
        string[] lines,
        out Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker> exits,
        out WorldMapShortcutPresentation.ShortcutMarker[] creatureHoles)
    {
        exits = new Dictionary<int, WorldMapShortcutPresentation.ShortcutMarker>();
        creatureHoles = Array.Empty<WorldMapShortcutPresentation.ShortcutMarker>();
        if (lines == null || lines.Length <= 11 || string.IsNullOrWhiteSpace(lines[1])) return false;

        string[] dimensions = lines[1].Split('|')[0].Split('*');
        if (dimensions.Length < 2 ||
            !int.TryParse(dimensions[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(dimensions[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height) ||
            width <= 0 || height <= 0)
            return false;

        TileInfo[,] tiles = new TileInfo[width, height];
        string[] encodedTiles = (lines[11] ?? string.Empty).Split('|');
        int tileIndex = 0;
        for (int x = 0; x < width; x++)
        {
            for (int y = height - 1; y >= 0; y--)
            {
                if (tileIndex >= encodedTiles.Length - 1) return false;
                string token = encodedTiles[tileIndex++];
                if (string.IsNullOrEmpty(token)) continue;
                string[] parts = token.Split(',');
                if (parts.Length == 0) continue;

                int terrain = 0;
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out terrain);
                int shortcut = 0;
                for (int p = 1; p < parts.Length; p++)
                {
                    switch (parts[p])
                    {
                        case "3":
                            if (shortcut < 1) shortcut = 1;
                            break;
                        case "4": shortcut = 2; break;
                        case "5": shortcut = 3; break;
                        case "9": shortcut = 4; break;
                        case "12": shortcut = 5; break;
                    }
                }
                tiles[x, y] = new TileInfo(terrain == 4, shortcut);
            }
        }

        List<IntVector2> nodeTiles = BuildNodeIndex(tiles, width, height);
        Dictionary<long, int> nodeByTile = new(nodeTiles.Count);
        for (int i = 0; i < nodeTiles.Count; i++)
            nodeByTile[TileKey(nodeTiles[i])] = i;

        List<WorldMapShortcutPresentation.ShortcutMarker> holes = new();
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                if (!tiles[x, y].ShortcutEntrance) continue;
                IntVector2 start = new(x, y);
                if (!TryTraceShortcut(tiles, width, height, start, nodeByTile, out int terminalType, out int nodeIndex))
                    continue;

                WorldMapShortcutPresentation.ShortcutMarker marker = new(
                    start.x + 0.5f,
                    start.y + 0.5f,
                    nodeIndex);

                if (terminalType == 2 && nodeIndex >= 0)
                    exits[nodeIndex] = marker;
                else if (terminalType == 3)
                    holes.Add(marker);
            }
        }

        creatureHoles = holes.ToArray();
        return true;
    }

    private static List<IntVector2> BuildNodeIndex(TileInfo[,] tiles, int width, int height)
    {
        List<IntVector2> nodes = new();
        int[] terminalTypes = { 2, 3, 5 };
        for (int typeIndex = 0; typeIndex < terminalTypes.Length; typeIndex++)
        {
            int type = terminalTypes[typeIndex];
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    if (tiles[x, y].Shortcut == type)
                        nodes.Add(new IntVector2(x, y));
                }
            }
        }
        return nodes;
    }

    private static bool TryTraceShortcut(
        TileInfo[,] tiles,
        int width,
        int height,
        IntVector2 start,
        Dictionary<long, int> nodeByTile,
        out int terminalType,
        out int nodeIndex)
    {
        terminalType = 0;
        nodeIndex = -1;
        IntVector2 pos = start;
        IntVector2 last = start;

        for (int step = 0; step < ShortcutGuard; step++)
        {
            IntVector2 previous = pos;
            pos = NextShortcutPosition(tiles, width, height, pos, last);
            if (pos.x == previous.x && pos.y == previous.y) return false;
            last = previous;

            if (!Inside(pos, width, height)) return false;
            TileInfo tile = tiles[pos.x, pos.y];
            if (tile.ShortcutEntrance) return false; // Normal shortcut; not a map endpoint.

            if (tile.Shortcut <= 1) continue;
            terminalType = tile.Shortcut;
            nodeByTile.TryGetValue(TileKey(pos), out nodeIndex);
            return true;
        }

        return false;
    }

    private static IntVector2 NextShortcutPosition(
        TileInfo[,] tiles,
        int width,
        int height,
        IntVector2 pos,
        IntVector2 last)
    {
        IntVector2 direction = pos - last;
        IntVector2 forward = pos + direction;
        if ((direction.x != 0 || direction.y != 0) &&
            Inside(forward, width, height) &&
            tiles[forward.x, forward.y].Shortcut != 0)
            return forward;

        IntVector2 original = pos;
        for (int i = 0; i < Custom.fourDirections.Length; i++)
        {
            IntVector2 candidateDirection = Custom.fourDirections[i];
            if (candidateDirection.x == -direction.x && candidateDirection.y == -direction.y)
                continue;

            IntVector2 candidate = pos + candidateDirection;
            if (!Inside(candidate, width, height) || tiles[candidate.x, candidate.y].Shortcut == 0)
                continue;
            pos = candidate;
            break;
        }

        if (pos.x == last.x && pos.y == last.y)
            pos -= direction;
        if (pos.x == original.x && pos.y == original.y && direction.x == 0 && direction.y == 0)
            return original;
        return pos;
    }

    private static void HandleConnectionDeleteShortcut(EditorMapPresentationSnapshot snapshot)
    {
        EditorSession session = DevToolRuntime.ActiveSession;
        if (session?.ToolMode != EditorToolMode.Map || snapshot == null) return;

        ImGuiIOPtr io = ImGui.GetIO();
        if (io.WantTextInput) return;
        bool requested = ImGui.IsKeyPressed(ImGuiKey.X) || ImGui.IsKeyPressed(ImGuiKey.Delete);
        if (!requested) return;

        string selected = selectedConnectionIdField?.GetValue(null) as string ?? string.Empty;
        if (string.IsNullOrEmpty(selected)) return; // Delete may already have been handled by WorldMapView.

        EditorMapConnectionSnapshot connection = FindConnection(snapshot, selected);
        if (connection == null || connection.Ambiguous ||
            connection.FromNodeIndex < 0 || connection.ToNodeIndex < 0)
            return;

        EditorMapRoomSnapshot roomA = FindRoom(snapshot, connection.FromRoomIndex);
        EditorMapRoomSnapshot roomB = FindRoom(snapshot, connection.ToRoomIndex);
        if (roomA == null || roomB == null) return;

        WorldTopologyCommandQueue.Enqueue(new WorldTopologyCommand(
            WorldTopologyCommandKind.DeleteConnection,
            region: snapshot.RegionName,
            edgeId: ExplicitEdgeId(connection.ConnectionId),
            roomA: roomA.Name,
            nodeA: connection.FromNodeIndex,
            roomB: roomB.Name,
            nodeB: connection.ToNodeIndex));
        selectedConnectionIdField.SetValue(null, string.Empty);
    }

    private static EditorMapConnectionSnapshot FindConnection(EditorMapPresentationSnapshot snapshot, string id)
    {
        EditorMapConnectionSnapshot[] connections = snapshot?.Connections ?? Array.Empty<EditorMapConnectionSnapshot>();
        for (int i = 0; i < connections.Length; i++)
        {
            EditorMapConnectionSnapshot connection = connections[i];
            if (connection != null && string.Equals(connection.ConnectionId, id, StringComparison.Ordinal))
                return connection;
        }
        return null;
    }

    private static EditorMapRoomSnapshot FindRoom(EditorMapPresentationSnapshot snapshot, int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = snapshot?.Rooms ?? Array.Empty<EditorMapRoomSnapshot>();
        for (int i = 0; i < rooms.Length; i++)
            if (rooms[i]?.RoomIndex == roomIndex) return rooms[i];
        return null;
    }

    private static string ExplicitEdgeId(string connectionId)
    {
        const string prefix = "explicit:";
        return connectionId != null && connectionId.StartsWith(prefix, StringComparison.Ordinal)
            ? connectionId.Substring(prefix.Length)
            : string.Empty;
    }

    private static bool Inside(IntVector2 tile, int width, int height) =>
        tile.x >= 0 && tile.x < width && tile.y >= 0 && tile.y < height;

    private static long TileKey(IntVector2 tile) => ((long)(uint)tile.x << 32) | (uint)tile.y;

    private static void ClearForNoMap()
    {
        entries.Clear();
        roomOrder.Clear();
        region = string.Empty;
        lastSubNodeCount = -1;
        nextStructurePollFrame = 0;
        backgroundCursor = 0;
    }

    private static void DisposeHook(ref IDisposable hook)
    {
        try { hook?.Dispose(); }
        catch { }
        hook = null;
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException invocation && invocation.InnerException != null)
            error = invocation.InnerException;
        return error;
    }
}
