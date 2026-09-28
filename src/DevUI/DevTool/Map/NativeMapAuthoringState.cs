using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

/// <summary>
/// Detached authoring values that vanilla stores on RoomPanel rather than on AbstractRoom.
/// DryCycle owns these values while the Native Map workspace is active; a RoomPanel, when present,
/// is only an import/export compatibility surface.
/// </summary>
internal readonly struct NativeMapRoomAuthoringValue
{
    internal NativeMapRoomAuthoringValue(Vector2 mapPosition, Vector2 devPosition, int layer)
    {
        MapPosition = mapPosition;
        DevPosition = devPosition;
        Layer = layer;
    }

    internal Vector2 MapPosition { get; }
    internal Vector2 DevPosition { get; }
    internal int Layer { get; }
}

internal static class NativeMapAuthoringStateHub
{
    private sealed class RoomState
    {
        internal string Name = string.Empty;
        internal Vector2 MapPosition;
        internal Vector2 InheritedDevPosition;
        internal Vector2 DevPosition;
        internal bool HasDevPositionOverride;
        internal int Layer = MapRoomLayer.Default;

        // Last values observed/written through the optional vanilla adapter. They let the bounded
        // compatibility audit distinguish a real third-party RoomPanel mutation from our own mirror.
        internal bool LegacyMirrorValid;
        internal WeakReference<RoomPanel> LegacyPanel;
        internal Vector2 LegacyMapPosition;
        internal Vector2 LegacyDevPosition;
        internal int LegacyLayer;
        internal bool PendingLegacyPush;
    }

    private sealed class WorldState
    {
        internal readonly Dictionary<int, RoomState> Rooms = new();
        internal bool Initialized;
        internal bool PositionOverridesDirty;
        internal long Revision;
    }

    private static ConditionalWeakTable<global::World, WorldState> states = new();

    internal static long GetRevision(EditorSession session)
    {
        global::World world = session?.World;
        if (world == null) return 0L;
        WorldState state = Ensure(session);
        return state?.Revision ?? 0L;
    }

    internal static bool TryGet(
        EditorSession session,
        int roomIndex,
        out NativeMapRoomAuthoringValue value)
    {
        value = default;
        WorldState state = Ensure(session);
        if (state == null || !state.Rooms.TryGetValue(roomIndex, out RoomState room))
            return false;

        value = new NativeMapRoomAuthoringValue(room.MapPosition, room.DevPosition, room.Layer);
        return true;
    }

    internal static bool SetDevPosition(EditorSession session, int roomIndex, Vector2 position)
    {
        WorldState state = Ensure(session);
        if (state == null || !state.Rooms.TryGetValue(roomIndex, out RoomState room))
            return false;
        bool nextOverride =
            (room.InheritedDevPosition - position).sqrMagnitude > 0.000001f;
        if ((room.DevPosition - position).sqrMagnitude <= 0.000001f &&
            room.HasDevPositionOverride == nextOverride)
            return false;

        room.DevPosition = nextOverride
            ? position
            : room.InheritedDevPosition;
        room.HasDevPositionOverride = nextOverride;
        room.PendingLegacyPush = true;
        state.PositionOverridesDirty = true;
        unchecked { state.Revision++; }
        return true;
    }

    internal static bool SetLayer(EditorSession session, int roomIndex, int layer)
    {
        MapRoomLayer.Validate(layer);
        WorldState state = Ensure(session);
        if (state == null || !state.Rooms.TryGetValue(roomIndex, out RoomState room))
            return false;

        if (room.Layer == layer) return false;

        room.Layer = layer;
        room.PendingLegacyPush = true;
        unchecked { state.Revision++; }
        return true;
    }

    internal static bool SetSubregion(EditorSession session, int roomIndex, string subregion)
    {
        global::World world = session?.World;
        AbstractRoom room = world?.GetAbstractRoom(roomIndex);
        if (room == null) return false;

        string next = string.IsNullOrWhiteSpace(subregion) ? null : subregion.Trim();
        if (string.Equals(room.subregionName, next, StringComparison.Ordinal))
            return false;

        room.subregionName = next;
        WorldState state = Ensure(session);
        if (state != null) unchecked { state.Revision++; }
        return true;
    }

    /// <summary>
    /// Mirrors one Native room into a live legacy MapPage when it exists. This is a compatibility
    /// adapter only; Native edits never require a RoomPanel to succeed.
    /// </summary>
    internal static void MirrorRoomToLegacy(EditorSession session, int roomIndex)
    {
        WorldState state = Ensure(session);
        if (state == null || !state.Rooms.TryGetValue(roomIndex, out RoomState room))
            return;
        if (session?.Owner?.activePage is not MapPage page)
            return;

        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null) return;

        panel.pos = room.MapPosition;
        panel.devPos = room.DevPosition;
        panel.layer = room.Layer;
        RememberLegacyMirror(room, panel);
        room.PendingLegacyPush = false;
    }

    internal static void SynchronizeLegacyAfterMutation(EditorSession session, int roomIndex)
    {
        MirrorRoomToLegacy(session, roomIndex);

        // Native Map owns the edit. The legacy page is refreshed only when it is actually visible;
        // otherwise mark it stale and let the quiescence boundary materialize it on demand.
        if (session?.Owner?.activePage is not MapPage page)
            return;
        if (DryCycle.DevUI.DevTool.Compatibility.LegacyDevUiQuiescenceController.TryDeferRefresh(session))
            return;

        RoomPanel panel = FindRoomPanel(page, roomIndex);
        try
        {
            panel?.Refresh();
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool legacy Map mirror refresh failed: " + error);
        }
    }

    /// <summary>
    /// Low-frequency compatibility audit. Native state is authoritative, but third-party code can
    /// still mutate a live RoomPanel directly. When that happens after our last mirror, import the
    /// external edge once and publish it through the Native revision system.
    /// </summary>
    internal static void AuditLegacy(EditorSession session)
    {
        WorldState state = Ensure(session);
        if (state == null || session?.Owner?.activePage is not MapPage page || page.subNodes == null)
            return;

        bool changed = false;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null)
                continue;

            int roomIndex = panel.roomRep.room.index;
            if (!state.Rooms.TryGetValue(roomIndex, out RoomState room))
                continue;

            bool samePanel = room.LegacyPanel != null && room.LegacyPanel.TryGetTarget(out RoomPanel previous) && ReferenceEquals(previous, panel);
            if (room.PendingLegacyPush || !samePanel)
            {
                // A recreated page is a new projection, not a new authoring source. Reopening it
                // must not import stale disk values over retained layer/position edits or undo.
                panel.pos = room.MapPosition;
                panel.devPos = room.DevPosition;
                panel.layer = room.Layer;
                RememberLegacyMirror(room, panel);
                room.PendingLegacyPush = false;
                continue;
            }

            if (!room.LegacyMirrorValid)
            {
                ImportPanel(room, panel);
                continue;
            }

            bool externalChange =
                (panel.pos - room.LegacyMapPosition).sqrMagnitude > 0.000001f ||
                (panel.devPos - room.LegacyDevPosition).sqrMagnitude > 0.000001f ||
                panel.layer != room.LegacyLayer;
            if (!externalChange) continue;

            room.MapPosition = panel.pos;
            room.InheritedDevPosition = panel.pos;
            if (!room.HasDevPositionOverride)
                room.DevPosition = room.InheritedDevPosition;
            room.Layer = Mathf.Clamp(panel.layer, 0, 2);
            RememberLegacyMirror(room, panel);
            changed = true;
        }

        if (changed) unchecked { state.Revision++; }
    }

    internal static NativeMapRoomAuthoringSnapshot Capture(EditorSession session, int roomIndex)
    {
        if (!TryGet(session, roomIndex, out NativeMapRoomAuthoringValue value))
            return null;

        AbstractRoom room = session?.World?.GetAbstractRoom(roomIndex);
        if (room == null) return null;

        WorldState state = Ensure(session);
        bool hasOverride =
            state != null &&
            state.Rooms.TryGetValue(roomIndex, out RoomState roomState) &&
            roomState.HasDevPositionOverride;

        return new NativeMapRoomAuthoringSnapshot(
            session.World,
            roomIndex,
            room.name ?? string.Empty,
            value.MapPosition,
            value.DevPosition,
            hasOverride,
            value.Layer,
            room.subregionName);
    }

    internal static bool Restore(
        EditorSession session,
        global::World expectedWorld,
        int roomIndex,
        Vector2 mapPosition,
        Vector2 devPosition,
        bool hasDevPositionOverride,
        int layer,
        string subregion)
    {
        if (session?.World == null || !ReferenceEquals(session.World, expectedWorld))
            return false;
        MapRoomLayer.Validate(layer);

        WorldState state = Ensure(session);
        if (state == null || !state.Rooms.TryGetValue(roomIndex, out RoomState room))
            return false;

        AbstractRoom abstractRoom = expectedWorld.GetAbstractRoom(roomIndex);
        if (abstractRoom == null) return false;

        room.MapPosition = mapPosition;
        room.HasDevPositionOverride = hasDevPositionOverride;
        room.DevPosition = hasDevPositionOverride
            ? devPosition
            : room.InheritedDevPosition;
        room.Layer = layer;
        room.PendingLegacyPush = true;
        state.PositionOverridesDirty = true;
        abstractRoom.subregionName = subregion;
        unchecked { state.Revision++; }

        MirrorRoomToLegacy(session, roomIndex);
        EditorRevisionHub.Mark(session, EditorRevisionKind.Map);
        return true;
    }

    internal static void Reset() =>
        states = new ConditionalWeakTable<global::World, WorldState>();

    private static WorldState Ensure(EditorSession session)
    {
        global::World world = session?.World;
        if (world == null) return null;

        WorldState state = states.GetValue(world, _ => new WorldState());
        if (state.Initialized) return state;

        InitializeDefaults(world, state);
        LoadMapConfig(world, state);

        MapPage page =
            session?.Owner?.activePage is MapPage livePage &&
            ReferenceEquals(livePage.world, world)
                ? livePage
                : null;
        if (page != null)
            ImportLegacyPage(page, state);

        LoadDevPositionOverrides(world, state);
        if (page != null)
            ApplyEffectiveOverridesToLegacyPage(page, state);

        state.Initialized = true;
        return state;
    }

    private static void InitializeDefaults(global::World world, WorldState state)
    {
        Vector2 next = new(100f, 500f);
        int end = world.firstRoomIndex + world.NumberOfRooms;
        for (int roomIndex = world.firstRoomIndex; roomIndex < end; roomIndex++)
        {
            AbstractRoom room = world.GetAbstractRoom(roomIndex);
            if (room == null) continue;

            if (room.offScreenDen)
                next = new Vector2(100f, 650f);

            state.Rooms[roomIndex] = new RoomState
            {
                Name = room.name ?? string.Empty,
                MapPosition = next,
                InheritedDevPosition = next,
                DevPosition = next,
                Layer = MapRoomLayer.Default
            };

            next.x += 110f;
            if (next.x > 1200f)
            {
                next.x = 100f;
                next.y -= 50f;
            }
        }
    }

    private static void LoadMapConfig(global::World world, WorldState state)
    {
        string path = ResolveMapConfigPath(world);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<int, RoomState> pair in state.Rooms)
            if (!string.IsNullOrWhiteSpace(pair.Value.Name))
                byName[pair.Value.Name] = pair.Key;

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int separator = line.IndexOf(": ", StringComparison.Ordinal);
                if (separator <= 0) continue;

                string roomName = line.Substring(0, separator);
                if (!byName.TryGetValue(roomName, out int roomIndex) ||
                    !state.Rooms.TryGetValue(roomIndex, out RoomState room))
                    continue;

                string[] fields = line.Substring(separator + 2)
                    .Split(new[] { "><" }, StringSplitOptions.None);
                if (fields.Length < 2 ||
                    !TryFloat(fields[0], out float mapX) ||
                    !TryFloat(fields[1], out float mapY))
                    continue;

                room.MapPosition = new Vector2(mapX, mapY);
                if (fields.Length >= 4 &&
                    TryFloat(fields[2], out float devX) &&
                    TryFloat(fields[3], out float devY))
                    room.InheritedDevPosition = new Vector2(devX, devY);
                else
                    room.InheritedDevPosition = room.MapPosition;

                room.DevPosition = room.InheritedDevPosition;
                room.HasDevPositionOverride = false;

                if (fields.Length >= 5 &&
                    int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int layer))
                    room.Layer = Mathf.Clamp(layer, 0, 2);
            }
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool Native Map config import failed: " + error.Message);
        }
    }

    private static string ResolveMapConfigPath(global::World world)
    {
        if (world?.game == null || string.IsNullOrWhiteSpace(world.name))
            return null;

        string mapName = WorldLoader.MapNameManipulator(world.name, world.game);
        if (world.game.IsStorySession && world.game.TimelinePoint != null)
        {
            string story = AssetManager.ResolveFilePath(
                "World" + Path.DirectorySeparatorChar + world.name + Path.DirectorySeparatorChar +
                "map_" + mapName + "-" + world.game.TimelinePoint.value + ".txt");
            if (File.Exists(story)) return story;
        }

        return AssetManager.ResolveFilePath(
            "World" + Path.DirectorySeparatorChar + world.name + Path.DirectorySeparatorChar +
            "map_" + mapName + ".txt");
    }

    private static void ImportLegacyPage(MapPage page, WorldState state)
    {
        if (page?.subNodes == null) return;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null)
                continue;
            if (!state.Rooms.TryGetValue(panel.roomRep.room.index, out RoomState room))
                continue;
            ImportPanel(room, panel);
        }
    }

    private static void ImportPanel(RoomState room, RoomPanel panel)
    {
        // The live vanilla panel is the strongest source for the untouched developer layout:
        // panel.pos is exactly where the original Map page is currently drawing the room.
        room.InheritedDevPosition = panel.pos;
        room.DevPosition = room.InheritedDevPosition;
        room.HasDevPositionOverride = false;
        room.Layer = Mathf.Clamp(panel.layer, 0, 2);
        room.PendingLegacyPush = false;
        RememberLegacyMirror(room, panel);
    }

    private const string PositionOverrideHeader = "# DryCycle DevTool map position overrides v1";

    internal static bool SaveDevPositionOverrides(EditorSession session)
    {
        WorldState state = Ensure(session);
        global::World world = session?.World;
        if (state == null || world == null || !state.PositionOverridesDirty)
            return true;

        if (!TryResolvePositionOverridePath(world, out string path, out string error))
        {
            Plugin.Logger?.LogWarning(
                "DevTool Map position overrides were not saved: " +
                (error ?? "authoring path is unavailable."));
            return false;
        }

        try
        {
            List<string> lines = new() { PositionOverrideHeader };
            List<RoomState> ordered = new();
            foreach (RoomState room in state.Rooms.Values)
            {
                if (room.HasDevPositionOverride)
                    ordered.Add(room);
            }

            ordered.Sort((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            for (int i = 0; i < ordered.Count; i++)
            {
                RoomState room = ordered[i];
                lines.Add(
                    room.Name + ": " +
                    room.DevPosition.x.ToString("R", CultureInfo.InvariantCulture) +
                    "><" +
                    room.DevPosition.y.ToString("R", CultureInfo.InvariantCulture));
            }

            WorldAuthoringPathResolver.AtomicWriteAllLines(path, lines);
            state.PositionOverridesDirty = false;
            return true;
        }
        catch (Exception saveError)
        {
            Plugin.Logger?.LogWarning(
                "DevTool Map position override save failed: " + saveError);
            return false;
        }
    }

    internal static IDisposable BeginVanillaMapSaveProjection(
        EditorSession session,
        MapPage page)
    {
        WorldState state = Ensure(session);
        if (state == null || page?.subNodes == null)
            return EmptyDisposable.Instance;

        List<LegacySaveRestore> restore = new();
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel ||
                panel.roomRep?.room == null ||
                !state.Rooms.TryGetValue(panel.roomRep.room.index, out RoomState room))
                continue;

            restore.Add(new LegacySaveRestore(
                panel,
                room,
                panel.pos,
                panel.devPos,
                panel.layer));

            // Vanilla map config remains the inherited source. DryCycle-only room placement is
            // serialized separately and must never overwrite the original developer layout.
            panel.devPos = room.InheritedDevPosition;
            panel.layer = room.Layer;
        }

        return new LegacySaveProjection(restore);
    }

    private static void LoadDevPositionOverrides(global::World world, WorldState state)
    {
        if (!TryResolvePositionOverridePath(world, out string path, out _) ||
            string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path))
            return;

        Dictionary<string, RoomState> byName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (RoomState room in state.Rooms.Values)
        {
            if (!string.IsNullOrWhiteSpace(room.Name))
                byName[room.Name] = room;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i]?.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int separator = line.IndexOf(": ", StringComparison.Ordinal);
                if (separator <= 0)
                    continue;

                string roomName = line.Substring(0, separator);
                if (!byName.TryGetValue(roomName, out RoomState room))
                    continue;

                string[] fields = line.Substring(separator + 2)
                    .Split(new[] { "><" }, StringSplitOptions.None);
                if (fields.Length < 2 ||
                    !TryFloat(fields[0], out float x) ||
                    !TryFloat(fields[1], out float y))
                    continue;

                room.DevPosition = new Vector2(x, y);
                room.HasDevPositionOverride = true;
            }

            state.PositionOverridesDirty = false;
        }
        catch (Exception loadError)
        {
            Plugin.Logger?.LogWarning(
                "DevTool Map position override load failed: " + loadError.Message);
        }
    }

    private static void ApplyEffectiveOverridesToLegacyPage(
        MapPage page,
        WorldState state)
    {
        if (page?.subNodes == null)
            return;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel ||
                panel.roomRep?.room == null ||
                !state.Rooms.TryGetValue(panel.roomRep.room.index, out RoomState room) ||
                !room.HasDevPositionOverride)
                continue;

            panel.devPos = room.DevPosition;
            RememberLegacyMirror(room, panel);
        }
    }

    private static bool TryResolvePositionOverridePath(
        global::World world,
        out string path,
        out string error)
    {
        path = string.Empty;
        error = null;

        string resolvedMapPath = ResolveMapConfigPath(world);
        if (string.IsNullOrWhiteSpace(resolvedMapPath))
        {
            error = "vanilla map config path is unavailable.";
            return false;
        }

        if (!WorldAuthoringPathResolver.TryResolveMapConfigSource(
                world.name,
                resolvedMapPath,
                out string sourcePath,
                out error))
            return false;

        string directory = Path.GetDirectoryName(sourcePath);
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem))
        {
            error = "map config source path is invalid.";
            return false;
        }

        path = Path.Combine(directory, stem + ".drycycle-layout.txt");
        return true;
    }

    private readonly struct LegacySaveRestore
    {
        internal LegacySaveRestore(
            RoomPanel panel,
            RoomState room,
            Vector2 position,
            Vector2 devPosition,
            int layer)
        {
            Panel = panel;
            Room = room;
            Position = position;
            DevPosition = devPosition;
            Layer = layer;
        }

        internal RoomPanel Panel { get; }
        internal RoomState Room { get; }
        internal Vector2 Position { get; }
        internal Vector2 DevPosition { get; }
        internal int Layer { get; }
    }

    private sealed class LegacySaveProjection : IDisposable
    {
        private List<LegacySaveRestore> restore;

        internal LegacySaveProjection(List<LegacySaveRestore> restore)
        {
            this.restore = restore;
        }

        public void Dispose()
        {
            List<LegacySaveRestore> current = restore;
            restore = null;
            if (current == null)
                return;

            for (int i = 0; i < current.Count; i++)
            {
                LegacySaveRestore item = current[i];
                if (item.Panel == null)
                    continue;

                item.Panel.pos = item.Position;
                item.Panel.devPos = item.DevPosition;
                item.Panel.layer = item.Layer;
                RememberLegacyMirror(item.Room, item.Panel);
            }
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        internal static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }

    private static void RememberLegacyMirror(RoomState room, RoomPanel panel)
    {
        room.LegacyMapPosition = panel.pos;
        room.LegacyDevPosition = panel.devPos;
        room.LegacyLayer = panel.layer;
        room.LegacyMirrorValid = true;
        room.LegacyPanel = new WeakReference<RoomPanel>(panel);
    }

    private static RoomPanel FindRoomPanel(MapPage page, int roomIndex)
    {
        if (page?.subNodes == null) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel &&
                panel.roomRep?.room?.index == roomIndex)
                return panel;
        }
        return null;
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

internal sealed class NativeMapRoomAuthoringSnapshot : IEditorStateSnapshot
{
    private readonly global::World world;
    private readonly int roomIndex;
    private readonly string roomName;
    private readonly Vector2 mapPosition;
    private readonly Vector2 devPosition;
    private readonly bool hasDevPositionOverride;
    private readonly int layer;
    private readonly string subregion;

    internal NativeMapRoomAuthoringSnapshot(
        global::World world,
        int roomIndex,
        string roomName,
        Vector2 mapPosition,
        Vector2 devPosition,
        bool hasDevPositionOverride,
        int layer,
        string subregion)
    {
        this.world = world;
        this.roomIndex = roomIndex;
        this.roomName = roomName ?? string.Empty;
        this.mapPosition = mapPosition;
        this.devPosition = devPosition;
        this.hasDevPositionOverride = hasDevPositionOverride;
        this.layer = layer;
        this.subregion = subregion;
        Fingerprint =
            mapPosition.x.ToString("R", CultureInfo.InvariantCulture) + "," +
            mapPosition.y.ToString("R", CultureInfo.InvariantCulture) + "|" +
            devPosition.x.ToString("R", CultureInfo.InvariantCulture) + "," +
            devPosition.y.ToString("R", CultureInfo.InvariantCulture) + "|" +
            (hasDevPositionOverride ? "1" : "0") + "|" +
            layer.ToString(CultureInfo.InvariantCulture) + "|" + (subregion ?? string.Empty);
    }

    public string Kind =>
        "NativeMapRoom:" + (world == null ? 0 : RuntimeHelpers.GetHashCode(world)) + ":" + roomName;

    public string Fingerprint { get; }

    public IEditorStateSnapshot CaptureCurrent(EditorSession session) =>
        NativeMapAuthoringStateHub.Capture(session, roomIndex);

    public bool Restore(EditorSession session) =>
        NativeMapAuthoringStateHub.Restore(
            session,
            world,
            roomIndex,
            mapPosition,
            devPosition,
            hasDevPositionOverride,
            layer,
            subregion);
}
