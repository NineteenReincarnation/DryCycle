using System;
using DevInterface;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Commands;

public static class EditorActions
{
    public static bool Save(EditorSession session)
    {
        if (session?.Owner == null) return false;
        try
        {
            if (session.Owner.activePage is MapPage map)
            {
                map.SaveMapConfig();
                return true;
            }

            if (session.Owner.activePage is RelationshipPage)
            {
                RelationshipPage.LogAllChangedRelationships();
                return true;
            }

            RoomSettings settings = session.RoomSettings;
            if (settings == null) return false;
            settings.Save();
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool save failed: " + error.Message);
            return false;
        }
    }

    public static bool Undo(EditorSession session) => session?.History.Undo(session) ?? false;
    public static bool Redo(EditorSession session) => session?.History.Redo(session) ?? false;

    public static bool SetObjectPosition(EditorSession session, PlacedObject target, Vector2 newPosition)
    {
        if (session?.RoomSettings == null || target == null) return false;
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        target.pos = newPosition;
        TryRefresh(target);
        PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, target);
        if (PlacedObjectHistoryEntry.TryCreate("Move " + (target.type?.value ?? "object"), before, after, out PlacedObjectHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static bool MutateObject(EditorSession session, PlacedObject target, string label, Action mutation)
    {
        if (session?.RoomSettings == null || target == null || mutation == null) return false;
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        mutation();
        TryRefresh(target);
        PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, target);
        if (PlacedObjectHistoryEntry.TryCreate(label, before, after, out PlacedObjectHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static PlacedObject CreateObject(EditorSession session, PlacedObject.Type type, Vector2 worldPosition)
    {
        if (session?.Owner == null || session.RoomSettings?.placedObjects == null || type == null) return null;

        int beforeCount = session.RoomSettings.placedObjects.Count;
        ObjectsPage page = session.Owner.activePage as ObjectsPage;
        bool temporary = page == null;
        if (temporary)
            page = new ObjectsPage(session.Owner, "DevTool_CompatibilityObjects", null, "Objects");

        try
        {
            page.CreateObjRep(type, null);
            if (session.RoomSettings.placedObjects.Count <= beforeCount) return null;

            PlacedObject created = session.RoomSettings.placedObjects[session.RoomSettings.placedObjects.Count - 1];
            created.pos = worldPosition;
            TryRefresh(created);
            PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, created);
            session.History.Push(new DelegateHistoryEntry(
                "Create " + (type.value ?? "object"),
                s => RemoveExact(s, created),
                s => after?.Restore(s) ?? false));
            session.Selection.SelectOnly(created);
            session.Owner.activePage?.Refresh();
            return created;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool object creation failed: " + error.Message);
            return null;
        }
        finally
        {
            if (temporary)
                page.ClearSprites();
        }
    }

    public static bool DeleteObject(EditorSession session, PlacedObject target)
    {
        if (session?.RoomSettings?.placedObjects == null || target == null) return false;
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        if (!session.RoomSettings.placedObjects.Remove(target)) return false;

        session.Selection.Toggle(target);
        session.Owner.activePage?.Refresh();
        session.History.Push(new DelegateHistoryEntry(
            "Delete " + (target.type?.value ?? "object"),
            s => before?.Restore(s) ?? false,
            s => RemoveExact(s, target)));
        return true;
    }

    private static bool RemoveExact(EditorSession session, PlacedObject target)
    {
        if (session?.RoomSettings?.placedObjects == null || target == null) return false;
        bool removed = false;
        for (int i = session.RoomSettings.placedObjects.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(session.RoomSettings.placedObjects[i], target)) continue;
            session.RoomSettings.placedObjects.RemoveAt(i);
            removed = true;
        }
        session.Selection.RemoveMissing(session.RoomSettings.placedObjects);
        session.Owner.activePage?.Refresh();
        return removed;
    }

    private static void TryRefresh(PlacedObject target)
    {
        try { target?.data?.RefreshLiveVisuals(); }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool live visual refresh failed: " + error.Message);
        }
    }
}
