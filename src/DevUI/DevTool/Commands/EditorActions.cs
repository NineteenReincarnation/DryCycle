using System;
using System.Collections.Generic;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Factories;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Preview;
using DryCycle.DevUI.DevTool.World;
using DryCycle.TemperatureSystem;
using DryCycle.Weather.Spatial;
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
                return SaveMapWorkspace(map);

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

    /// <summary>
    /// Map is one workspace even though its editable state is backed by several files. Keep every
    /// save entry point (toolbar button, Ctrl/Cmd+S, future automation) on this same core path so a
    /// successful map-config save can never leave world.txt or DryCycle-owned world data dirty.
    /// </summary>
    private static bool SaveMapWorkspace(MapPage map)
    {
        if (map == null) return false;

        bool ok = true;
        map.SaveMapConfig();

        string region = map.world?.name ?? string.Empty;

        if (WorldTextRegistry.Dirty)
        {
            if (WorldTextRegistry.EnsureLoaded(region))
                ok &= WorldTextRegistry.Save();
            else
            {
                ok = false;
                Plugin.Logger?.LogWarning(
                    "DevTool Map save could not persist world.txt: " +
                    (WorldTextRegistry.LoadError ?? "world.txt is unavailable."));
            }
        }

        WorldTopologyRegistry.EnsureLoaded();
        if (WorldTopologyRegistry.Dirty)
            ok &= WorldTopologyRegistry.Save();

        if (TemperatureSetsLoader.Dirty)
            ok &= TemperatureSetsLoader.Save();

        if (WeatherSpatialRegistry.Dirty)
            ok &= WeatherSpatialRegistry.Save();

        return ok;
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
        if ((target.pos - newPosition).sqrMagnitude <= 0.000001f) return false;

        NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        target.pos = newPosition;
        TryRefresh(session, target);
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
            NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
            target.pos += delta;
            TryRefresh(session, target);
            moved++;
        }

        if (moved == 0) return false;
        RefreshLegacyObjectFallback(session);

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

        NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        if (!ObjectInspectorRegistry.TrySetValue(target, key, value))
            return false;

        TryRefresh(session, target);
        PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, target);
        if (before != null && before.SameAs(after))
            return false;

        if (PlacedObjectHistoryEntry.TryCreate("Change " + key, before, after, out PlacedObjectHistoryEntry entry))
            session.History.Push(entry);
        return true;
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
            NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
            if (!ObjectInspectorRegistry.TrySetValue(target, key, value)) continue;
            TryRefresh(session, target);
            changed++;
        }

        if (changed == 0) return false;

        PlacedObjectsStateSnapshot after = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        if (before != null && after != null &&
            string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            return false;

        RefreshLegacyObjectFallback(session);
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
            target,
            "Legacy button",
            () => LegacyDevInterfaceBridge.ClickButton(session.Owner, target, path));
    }

    public static bool SetLegacySlider(EditorSession session, PlacedObject target, string path, float factor)
    {
        return ExecuteLegacyControl(
            session,
            target,
            "Legacy slider",
            () => LegacyDevInterfaceBridge.SetSlider(session.Owner, target, path, factor));
    }

    public static bool ResetLegacySlider(EditorSession session, PlacedObject target, string path)
    {
        return ExecuteLegacyControl(
            session,
            target,
            "Legacy slider reset",
            () => LegacyDevInterfaceBridge.ResetSlider(session.Owner, target, path));
    }

    public static bool SetLegacyText(EditorSession session, PlacedObject target, string path, string value)
    {
        return ExecuteLegacyControl(
            session,
            target,
            "Legacy text",
            () => LegacyDevInterfaceBridge.SetText(session.Owner, target, path, value));
    }

    public static bool SetLegacyDirection(EditorSession session, PlacedObject target, string path, float x, float y)
    {
        return ExecuteLegacyControl(
            session,
            target,
            "Legacy direction",
            () => LegacyDevInterfaceBridge.SetDirection(session.Owner, target, path, x, y));
    }

    public static bool SetLegacyColor(
        EditorSession session,
        PlacedObject target,
        string path,
        float r,
        float g,
        float b,
        float a)
    {
        return ExecuteLegacyControl(
            session,
            target,
            "Legacy color",
            () => LegacyDevInterfaceBridge.SetColor(session.Owner, target, path, r, g, b, a));
    }

    public static bool MutateObject(EditorSession session, PlacedObject target, string label, Action mutation)
    {
        if (session?.RoomSettings == null || target == null || mutation == null) return false;
        NativeObjectRuntimeReconciler.PrepareForMutation(session, target);
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        mutation();
        TryRefresh(session, target);
        PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, target);
        if (PlacedObjectHistoryEntry.TryCreate(label, before, after, out PlacedObjectHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    public static PlacedObject CreateObject(EditorSession session, PlacedObject.Type type, Vector2 worldPosition)
    {
        if (session?.Owner == null || session.RoomSettings?.placedObjects == null || type == null) return null;

        if (!NativePlacedObjectFactory.TryCreate(session, type, worldPosition, out PlacedObject created) || created == null)
            return null;

        TryRefresh(session, created);
        PlacedObjectState after = PlacedObjectState.Capture(session.RoomSettings, created);
        session.History.Push(new DelegateHistoryEntry(
            "Create " + (type.value ?? "object"),
            s => RemoveExact(s, created),
            s => after?.Restore(s) ?? false));
        session.Selection.SelectOnly(created);

        // Transitional compatibility only. Native creation no longer depends on ObjectsPage; this
        // refresh merely reconciles the still-retained vanilla representation/gizmo backend until
        // the Native Gizmo Engine takes ownership of world-space editing.
        RefreshLegacyObjectFallback(session);
        return created;
    }

    public static bool DeleteObject(EditorSession session, PlacedObject target)
    {
        if (session?.RoomSettings?.placedObjects == null || target == null) return false;
        PlacedObjectState before = PlacedObjectState.Capture(session.RoomSettings, target);
        NativeObjectRuntimeReconciler.RemoveRuntime(session, target);
        if (!session.RoomSettings.placedObjects.Remove(target)) return false;

        session.Selection.Toggle(target);
        RefreshLegacyObjectFallback(session);
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
            if (selected[i] == null) continue;
            NativeObjectRuntimeReconciler.RemoveRuntime(session, selected[i]);
            if (live.Remove(selected[i])) removed++;
        }
        if (removed == 0) return false;

        session.Selection.Clear();
        RefreshLegacyObjectFallback(session);
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
        if (session.ToolMode != EditorToolMode.Objects)
            session.SetToolMode(EditorToolMode.Objects);

        PlacedObjectsStateSnapshot before = PlacedObjectsStateSnapshot.Capture(session.RoomSettings);
        List<PlacedObject> originals = new(session.Selection.PlacedObjects);
        List<PlacedObject> copies = new(originals.Count);
        Vector2 offset = new(20f, 20f);

        for (int i = 0; i < originals.Count; i++)
        {
            PlacedObject source = originals[i];
            if (source?.type == null) continue;

            if (!NativePlacedObjectFactory.TryCreate(session, source.type, source.pos + offset, out PlacedObject copy) ||
                copy == null)
                continue;

            CopyObjectState(session, source, copy, offset);
            copies.Add(copy);
        }

        if (copies.Count == 0) return false;

        RefreshLegacyObjectFallback(session);
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

    private static bool ExecuteLegacyControl(
        EditorSession session,
        PlacedObject target,
        string label,
        Func<bool> action)
    {
        if (session?.Owner == null || target == null || action == null) return false;

        IEditorStateSnapshot before =
            SinglePlacedObjectStateSnapshot.Capture(session.RoomSettings, target);
        bool succeeded = LegacyObjectSandbox.Run(session, target, action);
        if (!succeeded) return false;

        // A legacy button may only open a custom sub-panel and leave the model unchanged. Mark the
        // selected member anyway so the rebuilt inspector recaptures the sandbox control tree.
        EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
        ObjectPresentationChangeHintHub.MarkMember(session, target);

        if (before == null) return true;
        IEditorStateSnapshot after = before.CaptureCurrent(session);
        if (SnapshotHistoryEntry.TryCreate(label, before, after, out SnapshotHistoryEntry entry))
            session.History.Push(entry);
        return true;
    }

    private static void CopyObjectState(EditorSession session, PlacedObject source, PlacedObject target, Vector2 offset)
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

        TryRefresh(session, target);
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
            if (!removed)
                NativeObjectRuntimeReconciler.RemoveRuntime(session, target);
            session.RoomSettings.placedObjects.RemoveAt(i);
            removed = true;
        }
        session.Selection.RemoveMissing(session.RoomSettings.placedObjects);
        RefreshLegacyObjectFallback(session);
        return removed;
    }

    private static void RefreshLegacyObjectFallback(EditorSession session) =>
        NativeLegacyPresentationInvalidation.RefreshCurrentObjectFallback(session);

    private static void TryRefresh(EditorSession session, PlacedObject target) =>
        NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);
}
