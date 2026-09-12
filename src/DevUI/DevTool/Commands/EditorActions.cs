using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Preview;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Commands;

public static class EditorActions
{
    public static bool Save(EditorSession session)
    {
        EffectPreviewRuntime.EndForPersistentOperation("save");
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

    public static bool Undo(EditorSession session)
    {
        EffectPreviewRuntime.EndForPersistentOperation("undo");
        return session?.History.Undo(session) ?? false;
    }

    public static bool Redo(EditorSession session)
    {
        EffectPreviewRuntime.EndForPersistentOperation("redo");
        return session?.History.Redo(session) ?? false;
    }

    public static bool PlaceObjectAtCursor(EditorSession session, bool keepPlacementMode)
    {
        if (session?.Owner == null || !session.PlacementActive || session.ToolMode != EditorToolMode.Objects)
            return false;

        RoomCamera camera = session.Owner.game?.cameras != null && session.Owner.game.cameras.Length > 0
            ? session.Owner.game.cameras[0]
            : null;
        if (camera == null) return false;

        string typeName = session.PlacementType;
        Vector2 worldPosition = camera.pos + session.Owner.mousePos;
        PlacedObject created = CreateObject(session, new PlacedObject.Type(typeName, false), worldPosition);
        if (created == null) return false;

        if (!keepPlacementMode)
            session.CancelPlacement();
        return true;
    }

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

    public static bool SetSelectionPrimaryPosition(EditorSession session, Vector2 newPrimaryPosition)
    {
        if (session?.RoomSettings?.placedObjects == null || session.Selection.Count == 0) return false;

        PlacedObject primary = session.Selection.PrimaryPlacedObject;
        if (primary == null) return false;

        Vector2 delta = newPrimaryPosition - primary.pos;
        if (delta.sqrMagnitude <= 0.000001f) return false;

        PlacedObjectsStateSnapshot before = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        List<PlacedObject> targets = new(session.Selection.PlacedObjects);
        int moved = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            PlacedObject target = targets[i];
            if (target == null || !session.RoomSettings.placedObjects.Contains(target)) continue;
            target.pos += delta;
            TryRefresh(target);
            moved++;
        }

        if (moved == 0) return false;
        session.Owner.activePage?.Refresh();

        PlacedObjectsStateSnapshot after = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                moved == 1 ? "Move object" : "Move " + moved + " objects",
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static bool SetObjectProperty(
        EditorSession session,
        PlacedObject target,
        string key,
        EditorPropertyValue value)
    {
        if (session?.RoomSettings == null || target == null || string.IsNullOrEmpty(key)) return false;

        bool accepted = false;
        MutateObject(
            session,
            target,
            "Change " + key,
            () => accepted = ObjectInspectorRegistry.TrySetValue(target, key, value));
        return accepted;
    }

    public static bool SetSelectionProperty(EditorSession session, string key, EditorPropertyValue value)
    {
        if (session?.RoomSettings?.placedObjects == null || session.Selection.Count == 0 || string.IsNullOrEmpty(key))
            return false;

        PlacedObjectsStateSnapshot before = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        List<PlacedObject> targets = new(session.Selection.PlacedObjects);
        int changed = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            PlacedObject target = targets[i];
            if (target == null || !session.RoomSettings.placedObjects.Contains(target)) continue;
            if (!ObjectInspectorRegistry.TrySetValue(target, key, value)) continue;
            TryRefresh(target);
            changed++;
        }

        if (changed == 0) return false;
        session.Owner.activePage?.Refresh();

        PlacedObjectsStateSnapshot after = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                changed == 1 ? "Change " + key : "Change " + key + " on " + changed + " objects",
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static bool InvokeLegacyButton(EditorSession session, PlacedObject target, string path)
    {
        return ExecuteLegacyControl(
            session,
            "Legacy button",
            () => LegacyDevInterfaceBridge.ClickButton(session.Owner, target, path));
    }

    public static bool SetLegacySlider(EditorSession session, PlacedObject target, string path, float factor)
    {
        return ExecuteLegacyControl(
            session,
            "Legacy slider",
            () => LegacyDevInterfaceBridge.SetSlider(session.Owner, target, path, factor));
    }

    public static bool ResetLegacySlider(EditorSession session, PlacedObject target, string path)
    {
        return ExecuteLegacyControl(
            session,
            "Legacy slider reset",
            () => LegacyDevInterfaceBridge.ResetSlider(session.Owner, target, path));
    }

    public static bool SetLegacyText(EditorSession session, PlacedObject target, string path, string value)
    {
        return ExecuteLegacyControl(
            session,
            "Legacy text",
            () => LegacyDevInterfaceBridge.SetText(session.Owner, target, path, value));
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

    public static bool DeleteSelection(EditorSession session)
    {
        List<PlacedObject> live = session?.RoomSettings?.placedObjects;
        if (live == null || session.Selection.Count == 0) return false;

        PlacedObjectsStateSnapshot before = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        List<PlacedObject> selected = new(session.Selection.PlacedObjects);
        int removed = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            if (selected[i] != null && live.Remove(selected[i])) removed++;
        }
        if (removed == 0) return false;

        session.Selection.Clear();
        session.Owner.activePage?.Refresh();
        PlacedObjectsStateSnapshot after = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                removed == 1 ? "Delete object" : "Delete " + removed + " objects",
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static bool DuplicateSelection(EditorSession session)
    {
        List<PlacedObject> live = session?.RoomSettings?.placedObjects;
        if (session?.Owner == null || live == null || session.Selection.Count == 0) return false;

        ObjectsPage page = session.Owner.activePage as ObjectsPage;
        if (page == null)
        {
            session.SetToolMode(EditorToolMode.Objects);
            page = session.Owner.activePage as ObjectsPage;
        }
        if (page == null) return false;

        PlacedObjectsStateSnapshot before = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        List<PlacedObject> originals = new(session.Selection.PlacedObjects);
        List<PlacedObject> copies = new(originals.Count);

        for (int i = 0; i < originals.Count; i++)
        {
            PlacedObject source = originals[i];
            if (source?.type == null) continue;

            int count = live.Count;
            page.CreateObjRep(source.type, null);
            if (live.Count <= count) continue;

            PlacedObject copy = live[live.Count - 1];
            CopyObjectState(source, copy, new Vector2(20f, 20f));
            copies.Add(copy);
        }

        if (copies.Count == 0) return false;

        page.Refresh();
        session.Selection.Clear();
        for (int i = 0; i < copies.Count; i++) session.Selection.Toggle(copies[i]);

        PlacedObjectsStateSnapshot after = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        if (SnapshotHistoryEntry.TryCreate(
                copies.Count == 1 ? "Duplicate object" : "Duplicate " + copies.Count + " objects",
                before,
                after,
                out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static bool ExecuteLegacyControl(EditorSession session, string label, Func<bool> action)
    {
        if (session?.Owner == null || action == null) return false;

        IEditorStateSnapshot before = LegacySnapshotFactory.CaptureForPointer(session);
        bool succeeded = action();
        if (!succeeded || before == null) return succeeded;

        IEditorStateSnapshot after = before.CaptureCurrent(session);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static void CopyObjectState(PlacedObject source, PlacedObject target, Vector2 offset)
    {
        target.pos = source.pos + offset;
        target.active = source.active;
        target.deactivatedByWarpFilter = source.deactivatedByWarpFilter;
        target.save = source.save;
        target.unrecognizedAttributes = Clone(source.unrecognizedAttributes);

        if (source.data != null && target.data != null)
        {
            try
            {
                target.data.FromString(source.data.ToString());
                target.data.owner = target;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool duplicate data copy failed: " + error.Message);
            }
        }

        TryRefresh(target);
    }

    private static string[] Clone(string[] source)
    {
        if (source == null) return null;
        string[] copy = new string[source.Length];
        Array.Copy(source, copy, source.Length);
        return copy;
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
