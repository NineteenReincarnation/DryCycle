using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.World;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

internal static class MapEditorActions
{
    internal static void SelectRoom(EditorSession session, int roomIndex)
    {
        MapEditorState state = MapEditorStateHub.Get(session);
        if (state == null) return;

        // Selection belongs to the detached presentation boundary. Validate against the published
        // immutable map snapshot rather than walking MapPage.subNodes.
        int next = ContainsPublishedRoom(roomIndex) ? roomIndex : -1;
        if (state.SelectedRoomIndex == next) return;
        state.SelectedRoomIndex = next;
    }

    internal static bool SwitchCurrentRoom(EditorSession session, int roomIndex)
    {
        global::DevInterface.DevUI owner = session?.Owner;
        RainWorldGame game = owner?.game;
        global::World world = game?.world;
        if (owner == null || game == null || world == null)
            return false;

        AbstractRoom target = world.GetAbstractRoom(roomIndex);
        if (target == null)
            return false;

        if (target.realizedRoom == null)
            world.ActivateRoom(target);

        global::Room nextRoom = target.realizedRoom;
        if (nextRoom == null)
            return false;

        // Current Room is navigation state, not author data: never push it into map history.
        // Keep DevUI and the game's primary room camera on the same realized room.
        if (!ReferenceEquals(owner.room, nextRoom))
        {
            if (game.cameras != null && game.cameras.Length > 0 && game.cameras[0] != null)
                game.cameras[0].ChangeRoom(nextRoom, 0);
            owner.room = nextRoom;
        }

        return true;
    }

    internal static bool SetRoomPosition(EditorSession session, int roomIndex, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Vector2) return false;
        Vector2 next = new(value.X, value.Y);
        return Mutate(
            session,
            "Move map room",
            roomIndex,
            () => NativeMapAuthoringStateHub.SetDevPosition(session, roomIndex, next));
    }

    internal static bool SetRoomLayer(EditorSession session, int roomIndex, int layer)
    {
        return Mutate(
            session,
            "Change map layer",
            roomIndex,
            () => NativeMapAuthoringStateHub.SetLayer(session, roomIndex, layer));
    }

    internal static bool SetRoomSubregion(EditorSession session, int roomIndex, string subregion)
    {
        return Mutate(
            session,
            "Change room subregion",
            roomIndex,
            () => NativeMapAuthoringStateHub.SetSubregion(session, roomIndex, subregion));
    }

    internal static bool SetRoomAttraction(
        EditorSession session,
        int roomIndex,
        string creatureId,
        string attraction)
    {
        global::World world = session?.World;
        if (world == null || string.IsNullOrWhiteSpace(creatureId))
            return false;

        WorldRoomAttractionRegistry.TryGetExplicit(
            session,
            roomIndex,
            creatureId,
            out string before);

        if (!WorldRoomAttractionRegistry.SetRoomOverride(
                session,
                roomIndex,
                creatureId,
                attraction))
            return false;

        WorldRoomAttractionRegistry.TryGetExplicit(
            session,
            roomIndex,
            creatureId,
            out string after);

        string label = "Change room attraction: " + creatureId;
        session.History.Push(new DelegateHistoryEntry(
            label,
            undo: current => RestoreRoomAttraction(
                current,
                world,
                roomIndex,
                creatureId,
                before),
            redo: current => RestoreRoomAttraction(
                current,
                world,
                roomIndex,
                creatureId,
                after)));
        return true;
    }

    private static bool RestoreRoomAttraction(
        EditorSession session,
        global::World expectedWorld,
        int roomIndex,
        string creatureId,
        string value)
    {
        if (session?.World == null || !ReferenceEquals(session.World, expectedWorld))
            return false;

        if (WorldRoomAttractionRegistry.SetRoomOverride(
                session,
                roomIndex,
                creatureId,
                value))
            return true;

        bool hasCurrent = WorldRoomAttractionRegistry.TryGetExplicit(
            session,
            roomIndex,
            creatureId,
            out string current);
        if (value == null)
            return !hasCurrent;
        return hasCurrent && string.Equals(current, value, StringComparison.Ordinal);
    }

    private static bool Mutate(EditorSession session, string label, int roomIndex, Func<bool> mutation)
    {
        if (session?.World == null || mutation == null) return false;

        NativeMapRoomAuthoringSnapshot before = NativeMapAuthoringStateHub.Capture(session, roomIndex);
        if (before == null || !mutation()) return false;

        // Keep an optional live MapPage synchronized, but never require one for Native edits.
        NativeMapAuthoringStateHub.SynchronizeLegacyAfterMutation(session, roomIndex);

        NativeMapRoomAuthoringSnapshot after = NativeMapAuthoringStateHub.Capture(session, roomIndex);
        bool historyPushed = SnapshotHistoryEntry.TryCreate(
            label,
            before,
            after,
            out SnapshotHistoryEntry entry);
        if (historyPushed)
            session.History.Push(entry);
        else
            EditorRevisionHub.Mark(session, EditorRevisionKind.Map);

        return true;
    }

    private static bool ContainsPublishedRoom(int roomIndex)
    {
        EditorMapRoomSnapshot[] rooms = MapEditorPresentationHub.Current?.Rooms;
        if (rooms == null) return false;
        for (int i = 0; i < rooms.Length; i++)
        {
            if (rooms[i]?.RoomIndex == roomIndex) return true;
        }
        return false;
    }
}
