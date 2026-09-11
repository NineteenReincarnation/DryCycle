using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Map;

public sealed class EditorMapRoomSnapshot
{
    public int RoomIndex { get; init; }
    public string Name { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public int Layer { get; init; }
    public string Subregion { get; init; } = string.Empty;
    public bool OffScreenDen { get; init; }
    public bool Disabled { get; init; }
    public bool CurrentRoom { get; init; }
    public bool Selected { get; init; }
}

public sealed class EditorMapConnectionSnapshot
{
    public int FromRoomIndex { get; init; }
    public int ToRoomIndex { get; init; }
}

public sealed class EditorMapPresentationSnapshot
{
    public static readonly EditorMapPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public string RegionName { get; init; } = string.Empty;
    public int SelectedRoomIndex { get; init; } = -1;
    public EditorMapRoomSnapshot[] Rooms { get; init; } = Array.Empty<EditorMapRoomSnapshot>();
    public EditorMapConnectionSnapshot[] Connections { get; init; } = Array.Empty<EditorMapConnectionSnapshot>();
}

internal sealed class MapEditorState
{
    internal int SelectedRoomIndex = -1;
}

internal static class MapEditorStateHub
{
    private static ConditionalWeakTable<EditorSession, MapEditorState> states = new();

    internal static MapEditorState Get(EditorSession session) =>
        session == null ? null : states.GetValue(session, _ => new MapEditorState());

    internal static void Reset() => states = new ConditionalWeakTable<EditorSession, MapEditorState>();
}

public static class MapEditorPresentationHub
{
    private static volatile EditorMapPresentationSnapshot current = EditorMapPresentationSnapshot.Empty;

    public static EditorMapPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.ToolMode != EditorToolMode.Map || session.Owner?.activePage is not MapPage page || page.world == null)
        {
            current = EditorMapPresentationSnapshot.Empty;
            return;
        }

        MapEditorState state = MapEditorStateHub.Get(session);
        List<EditorMapRoomSnapshot> rooms = new();
        HashSet<int> roomIndices = new();
        HashSet<string> disabled = new(StringComparer.Ordinal);
        if (page.world.DisabledMapRooms != null)
        {
            for (int i = 0; i < page.world.DisabledMapRooms.Count; i++)
            {
                string name = page.world.DisabledMapRooms[i];
                if (!string.IsNullOrEmpty(name)) disabled.Add(name);
            }
        }

        int currentRoomIndex = session.Room?.abstractRoom?.index ?? -1;

        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is not RoomPanel panel || panel.roomRep?.room == null) continue;
            AbstractRoom room = panel.roomRep.room;
            roomIndices.Add(room.index);
            rooms.Add(new EditorMapRoomSnapshot
            {
                RoomIndex = room.index,
                Name = room.name ?? string.Empty,
                X = panel.devPos.x,
                Y = panel.devPos.y,
                Layer = panel.layer,
                Subregion = room.subregionName ?? string.Empty,
                OffScreenDen = room.offScreenDen,
                Disabled = disabled.Contains(room.name ?? string.Empty),
                CurrentRoom = room.index == currentRoomIndex,
                Selected = room.index == state.SelectedRoomIndex
            });
        }

        if (state.SelectedRoomIndex >= 0 && !roomIndices.Contains(state.SelectedRoomIndex))
            state.SelectedRoomIndex = -1;

        List<EditorMapConnectionSnapshot> connections = new();
        HashSet<long> seen = new();
        for (int i = 0; i < rooms.Count; i++)
        {
            AbstractRoom room = page.world.GetAbstractRoom(rooms[i].RoomIndex);
            if (room?.connections == null) continue;

            for (int c = 0; c < room.connections.Length; c++)
            {
                int other = room.connections[c];
                if (other < 0 || !roomIndices.Contains(other)) continue;
                int a = Math.Min(room.index, other);
                int b = Math.Max(room.index, other);
                long key = ((long)(uint)a << 32) | (uint)b;
                if (!seen.Add(key)) continue;
                connections.Add(new EditorMapConnectionSnapshot
                {
                    FromRoomIndex = a,
                    ToRoomIndex = b
                });
            }
        }

        // CreatureVis owns Futile nodes directly rather than normal DevUINodes, so the
        // generic legacy-control hider cannot reach them. Hide only while the rebuilt map
        // workspace is active. Vanilla CreatureVis.Update restores its own visibility on the
        // next frame after switching back to Vanilla UI.
        if (EditorInputRouter.FrontendAttached && !EditorUiModeState.UseVanilla &&
            page.creatureVisualizations != null)
        {
            for (int i = 0; i < page.creatureVisualizations.Count; i++)
            {
                MapPage.CreatureVis vis = page.creatureVisualizations[i];
                if (vis == null) continue;
                if (vis.label != null) vis.label.isVisible = false;
                if (vis.label2 != null) vis.label2.isVisible = false;
                if (vis.sprite != null) vis.sprite.isVisible = false;
                if (vis.sprite2 != null) vis.sprite2.isVisible = false;
            }
        }

        current = new EditorMapPresentationSnapshot
        {
            Available = true,
            RegionName = page.world.name ?? string.Empty,
            SelectedRoomIndex = state.SelectedRoomIndex,
            Rooms = rooms.ToArray(),
            Connections = connections.ToArray()
        };
    }

    internal static void Clear() => current = EditorMapPresentationSnapshot.Empty;
}

public enum MapEditorCommandKind
{
    SelectRoom,
    SetRoomPosition,
    SetRoomLayer,
    SetRoomSubregion
}

public readonly struct MapEditorCommand
{
    public MapEditorCommand(
        MapEditorCommandKind kind,
        int roomIndex = -1,
        string text = null,
        EditorPropertyValue value = default)
    {
        Kind = kind;
        RoomIndex = roomIndex;
        Text = text;
        Value = value;
    }

    public MapEditorCommandKind Kind { get; }
    public int RoomIndex { get; }
    public string Text { get; }
    public EditorPropertyValue Value { get; }
}

public static class MapEditorCommandQueue
{
    private static readonly ConcurrentQueue<MapEditorCommand> queue = new();

    public static void Enqueue(MapEditorCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        while (queue.TryDequeue(out MapEditorCommand command))
        {
            try
            {
                switch (command.Kind)
                {
                    case MapEditorCommandKind.SelectRoom:
                        MapEditorActions.SelectRoom(session, command.RoomIndex);
                        break;
                    case MapEditorCommandKind.SetRoomPosition:
                        MapEditorActions.SetRoomPosition(session, command.RoomIndex, command.Value);
                        break;
                    case MapEditorCommandKind.SetRoomLayer:
                        MapEditorActions.SetRoomLayer(session, command.RoomIndex, command.Value.Integer);
                        break;
                    case MapEditorCommandKind.SetRoomSubregion:
                        MapEditorActions.SetRoomSubregion(session, command.RoomIndex, command.Text);
                        break;
                }
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool map command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }
}
