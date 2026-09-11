using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Map;

internal static class MapEditorActions
{
    internal static void SelectRoom(EditorSession session, int roomIndex)
    {
        MapEditorState state = MapEditorStateHub.Get(session);
        if (state == null) return;
        state.SelectedRoomIndex = FindRoomPanel(session, roomIndex) != null ? roomIndex : -1;
    }

    internal static bool SetRoomPosition(EditorSession session, int roomIndex, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Vector2) return false;
        return Mutate(session, "Move map room", roomIndex, panel =>
        {
            panel.devPos = new Vector2(value.X, value.Y);
            return true;
        });
    }

    internal static bool SetRoomLayer(EditorSession session, int roomIndex, int layer)
    {
        return Mutate(session, "Change map layer", roomIndex, panel =>
        {
            panel.layer = Mathf.Clamp(layer, 0, 2);
            return true;
        });
    }

    internal static bool SetRoomSubregion(EditorSession session, int roomIndex, string subregion)
    {
        return Mutate(session, "Change room subregion", roomIndex, panel =>
        {
            if (panel.roomRep?.room == null) return false;
            panel.roomRep.room.subregionName = string.IsNullOrWhiteSpace(subregion) ? null : subregion.Trim();
            return true;
        });
    }

    private static bool Mutate(EditorSession session, string label, int roomIndex, Func<RoomPanel, bool> mutation)
    {
        if (session?.Owner?.activePage is not MapPage page || mutation == null) return false;
        RoomPanel panel = FindRoomPanel(page, roomIndex);
        if (panel == null) return false;

        MapStateSnapshot before = MapStateSnapshot.Capture(page);
        if (before == null || !mutation(panel)) return false;

        try
        {
            panel.Refresh();
            page.Refresh();
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool map refresh failed: " + error.Message);
        }

        MapStateSnapshot after = MapStateSnapshot.Capture(page);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static RoomPanel FindRoomPanel(EditorSession session, int roomIndex) =>
        session?.Owner?.activePage is MapPage page ? FindRoomPanel(page, roomIndex) : null;

    private static RoomPanel FindRoomPanel(MapPage page, int roomIndex)
    {
        if (page?.subNodes == null) return null;
        for (int i = 0; i < page.subNodes.Count; i++)
        {
            if (page.subNodes[i] is RoomPanel panel && panel.roomRep?.room?.index == roomIndex)
                return panel;
        }
        return null;
    }
}
