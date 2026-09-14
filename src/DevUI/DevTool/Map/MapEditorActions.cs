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

        int next = FindRoomPanel(session, roomIndex) != null ? roomIndex : -1;
        if (state.SelectedRoomIndex == next) return;

        // Selection is an explicit presentation key in MapEditorPresentationHub. Do not dirty the
        // map model revision: the hub can update only the old/new room flags and retain the room-node
        // cache plus the complete connection graph.
        state.SelectedRoomIndex = next;
    }

    internal static bool SetRoomPosition(EditorSession session, int roomIndex, EditorPropertyValue value)
    {
        if (value.Kind != EditorPropertyKind.Vector2) return false;
        return Mutate(session, "Move map room", roomIndex, panel =>
        {
            Vector2 next = new(value.X, value.Y);
            if ((panel.devPos - next).sqrMagnitude <= 0.000001f) return false;
            panel.devPos = next;
            return true;
        });
    }

    internal static bool SetRoomLayer(EditorSession session, int roomIndex, int layer)
    {
        return Mutate(session, "Change map layer", roomIndex, panel =>
        {
            int next = Mathf.Clamp(layer, 0, 2);
            if (panel.layer == next) return false;
            panel.layer = next;
            return true;
        });
    }

    internal static bool SetRoomSubregion(EditorSession session, int roomIndex, string subregion)
    {
        return Mutate(session, "Change room subregion", roomIndex, panel =>
        {
            if (panel.roomRep?.room == null) return false;
            string next = string.IsNullOrWhiteSpace(subregion) ? null : subregion.Trim();
            if (string.Equals(panel.roomRep.room.subregionName, next, StringComparison.Ordinal))
                return false;
            panel.roomRep.room.subregionName = next;
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
        bool historyPushed = SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry);
        if (historyPushed)
        {
            // History.Revision is the authoritative dirty source. CorePresentation will invalidate
            // Shell + active Map once for the whole DevUI update.
            session.History.Push(entry);
        }
        else
        {
            // A successful compatibility mutation that is outside MapStateSnapshot remains possible
            // for third-party panel subclasses. Preserve one narrow fallback invalidation for that
            // case; built-in position/layer/subregion no-ops are filtered before this point.
            EditorRevisionHub.Mark(session, EditorRevisionKind.Map);
        }
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
