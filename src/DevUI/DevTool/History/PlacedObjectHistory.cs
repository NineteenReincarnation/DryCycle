using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.History;

/// <summary>
/// Identity-preserving snapshot migrated from the former DevUI shortcut runtime. The same
/// PlacedObject and Data instances are restored so POM/RegionKit objects that retain object
/// references do not break across Undo/Redo.
/// </summary>
public sealed class PlacedObjectState
{
    private readonly RoomSettings settings;
    private readonly PlacedObject target;
    private readonly int index;
    private readonly PlacedObject.Type type;
    private readonly Vector2 pos;
    private readonly bool active;
    private readonly bool deactivatedByWarpFilter;
    private readonly bool save;
    private readonly string[] unrecognizedAttributes;
    private readonly PlacedObject.Data dataReference;
    private readonly string dataSerialized;
    private readonly BuiltinObjectAuthoringStateSnapshot authoringState;
    private readonly string fingerprint;

    private PlacedObjectState(RoomSettings settings, PlacedObject target)
    {
        this.settings = settings;
        this.target = target;
        index = settings?.placedObjects?.IndexOf(target) ?? -1;
        type = target?.type;
        pos = target?.pos ?? Vector2.zero;
        active = target?.active ?? false;
        deactivatedByWarpFilter = target?.deactivatedByWarpFilter ?? false;
        save = target?.save ?? false;
        unrecognizedAttributes = Clone(target?.unrecognizedAttributes);
        dataReference = target?.data;
        dataSerialized = target?.data?.ToString() ?? string.Empty;
        authoringState = BuiltinObjectAuthoringStateSnapshot.Capture(target?.data);
        fingerprint = BuildFingerprint();
    }

    public PlacedObject Target => target;
    public int Index => index;
    internal string Fingerprint => fingerprint;

    public static PlacedObjectState Capture(RoomSettings settings, PlacedObject target)
    {
        if (settings?.placedObjects == null || target == null) return null;
        return new PlacedObjectState(settings, target);
    }

    public bool SameAs(PlacedObjectState other) =>
        other != null && ReferenceEquals(target, other.target) &&
        string.Equals(fingerprint, other.fingerprint, StringComparison.Ordinal);

    public bool Restore(EditorSession session)
    {
        RoomSettings current = session?.RoomSettings;
        if (!ReferenceEquals(settings, current) || current?.placedObjects == null || target == null)
            return false;

        try
        {
            int previousIndex = current.placedObjects.IndexOf(target);
            bool previouslyPresent = previousIndex >= 0;

            for (int i = current.placedObjects.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(current.placedObjects[i], target))
                    current.placedObjects.RemoveAt(i);
            }

            if (index >= 0)
            {
                if (!RestoreDetachedFields(current)) return false;
                current.placedObjects.Insert(Mathf.Clamp(index, 0, current.placedObjects.Count), target);
            }

            if (index >= 0)
                NativeObjectRuntimeReconciler.RefreshAfterMutation(session, target);
            else
            {
                NativeObjectRuntimeReconciler.RemoveRuntime(session, target);
                NativeObjectRuntimeReconciler.RefreshAfterRemoval(session, target);
            }
            HeadlessObjectCompatibilityHost.Invalidate(session, target);

            int finalIndex = current.placedObjects.IndexOf(target);
            bool shouldBePresent = index >= 0;
            bool membershipOrOrderChanged =
                previouslyPresent != shouldBePresent ||
                (shouldBePresent && previousIndex != finalIndex);

            if (membershipOrOrderChanged)
                ObjectPresentationChangeHintHub.MarkCollection(session);
            else if (shouldBePresent && finalIndex >= 0)
                ObjectPresentationChangeHintHub.MarkMember(session, target);

            RefreshCompatibilityPage(session);
            return true;
        }
        catch (Exception error)
        {
            Plugin.Logger?.LogWarning("DevTool placed-object restore failed: " + error.Message);
            return false;
        }
    }

    internal bool RestoreDetachedFields(RoomSettings current)
    {
        if (!ReferenceEquals(settings, current) || target == null) return false;

        target.type = type;
        target.pos = pos;
        target.active = active;
        target.deactivatedByWarpFilter = deactivatedByWarpFilter;
        target.save = save;
        target.unrecognizedAttributes = Clone(unrecognizedAttributes);
        target.data = dataReference;

        if (dataReference != null)
        {
            dataReference.owner = target;
            authoringState?.PrepareSerializedRestore(dataReference);
            dataReference.FromString(dataSerialized);
            authoringState?.Restore(dataReference);
            try { dataReference.RefreshLiveVisuals(); }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool placed-object live refresh failed: " + error.Message);
            }
        }
        return true;
    }

    /// <summary>
    /// Object history is shared by the whole Room document. Undoing an object edit while Room,
    /// Sound or Triggers is visible must not rebuild that unrelated hidden vanilla page. When the
    /// Objects backend itself is active we still call Refresh so the quiescence hook can perform its
    /// minimal world-handle refresh; other migrated pages are merely marked stale until legacy UI
    /// owns presentation again. Unknown/custom pages fail closed to their ordinary Refresh path.
    /// </summary>
    internal static void RefreshCompatibilityPage(EditorSession session)
    {
        // Native Objects has no materialized page to invalidate. When explicit Vanilla/Legacy owns
        // an ObjectsPage, the compatibility bridge refreshes it; unrelated legacy pages keep their
        // existing deferred-refresh behavior.
        if (session?.ToolMode == EditorToolMode.Objects)
        {
            NativeLegacyPresentationInvalidation.RefreshCurrentObjectFallback(session);
            return;
        }

        global::DevInterface.Page page = session?.Owner?.activePage;
        if (page == null || LegacyDevUiQuiescenceController.TryDeferRefresh(session))
            return;
        page.Refresh();
    }

    private string BuildFingerprint()
    {
        StringBuilder text = new();
        text.Append(target == null ? 0 : RuntimeHelpers.GetHashCode(target)).Append('|')
            .Append(index).Append('|')
            .Append(type?.value ?? string.Empty).Append('|')
            .Append(pos.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(pos.y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(active ? '1' : '0').Append(deactivatedByWarpFilter ? '1' : '0').Append(save ? '1' : '0').Append('|')
            .Append(dataReference == null ? 0 : RuntimeHelpers.GetHashCode(dataReference)).Append('|')
            .Append(dataSerialized).Append('|')
            .Append(authoringState?.Fingerprint ?? string.Empty).Append('|');

        if (unrecognizedAttributes != null)
        {
            for (int i = 0; i < unrecognizedAttributes.Length; i++)
                text.Append(unrecognizedAttributes[i] ?? string.Empty).Append('\u001f');
        }
        return text.ToString();
    }

    private static string[] Clone(string[] source)
    {
        if (source == null) return null;
        string[] result = new string[source.Length];
        Array.Copy(source, result, source.Length);
        return result;
    }
}

public sealed class PlacedObjectHistoryEntry : IEditorHistoryEntry
{
    private readonly PlacedObjectState before;
    private readonly PlacedObjectState after;

    public PlacedObjectHistoryEntry(string label, PlacedObjectState before, PlacedObjectState after)
    {
        Label = string.IsNullOrEmpty(label) ? "Edit object" : label;
        this.before = before;
        this.after = after;
    }

    public string Label { get; }
    public bool Undo(EditorSession session) => before?.Restore(session) ?? false;
    public bool Redo(EditorSession session) => after?.Restore(session) ?? false;

    public static bool TryCreate(string label, PlacedObjectState before, PlacedObjectState after, out PlacedObjectHistoryEntry entry)
    {
        entry = null;
        if (before == null || after == null || before.SameAs(after)) return false;
        entry = new PlacedObjectHistoryEntry(label, before, after);
        return true;
    }
}

/// <summary>
/// Presentation selection paired with collection-changing object history. Selection is intentionally
/// not part of generic model snapshots: scalar/property Undo should not unexpectedly jump the user's
/// current selection. Delete/Duplicate, however, mutate selection as part of the command and must
/// restore it together with collection membership.
/// </summary>
internal sealed class PlacedObjectSelectionState
{
    private readonly PlacedObject[] targets;

    private PlacedObjectSelectionState(PlacedObject[] targets)
    {
        this.targets = targets ?? Array.Empty<PlacedObject>();
    }

    internal static PlacedObjectSelectionState Capture(EditorSession session)
    {
        IReadOnlyList<PlacedObject> selected = session?.Selection?.PlacedObjects;
        if (selected == null || selected.Count == 0)
            return new PlacedObjectSelectionState(Array.Empty<PlacedObject>());

        PlacedObject[] copy = new PlacedObject[selected.Count];
        for (int i = 0; i < selected.Count; i++)
            copy[i] = selected[i];
        return new PlacedObjectSelectionState(copy);
    }

    internal void Restore(EditorSession session)
    {
        EditorSelection selection = session?.Selection;
        List<PlacedObject> live = session?.RoomSettings?.placedObjects;
        if (selection == null)
            return;

        selection.Clear();
        if (live == null || targets.Length == 0)
            return;

        int missing = 0;
        for (int i = 0; i < targets.Length; i++)
        {
            PlacedObject target = targets[i];
            if (ContainsReference(live, target))
                selection.Toggle(target);
            else
                missing++;
        }

        if (missing > 0)
        {
            Plugin.Logger?.LogWarning(
                "DevTool object-history selection restore skipped " + missing +
                " object(s) that were not present after model restore.");
        }
    }

    private static bool ContainsReference(List<PlacedObject> live, PlacedObject target)
    {
        if (target == null) return false;
        for (int i = 0; i < live.Count; i++)
            if (ReferenceEquals(live[i], target))
                return true;
        return false;
    }
}

/// <summary>
/// Atomic model + selection history for collection-changing native object commands. Model restore is
/// authoritative; selection restoration runs only after the matching object identities are back in
/// the RoomSettings collection, preventing an Undo from restoring objects while leaving Inspector
/// and gizmo presentation detached from them.
/// </summary>
internal sealed class PlacedObjectCollectionHistoryEntry : IEditorHistoryEntry
{
    private readonly PlacedObjectsStateSnapshot before;
    private readonly PlacedObjectsStateSnapshot after;
    private readonly PlacedObjectSelectionState beforeSelection;
    private readonly PlacedObjectSelectionState afterSelection;

    private PlacedObjectCollectionHistoryEntry(
        string label,
        PlacedObjectsStateSnapshot before,
        PlacedObjectsStateSnapshot after,
        PlacedObjectSelectionState beforeSelection,
        PlacedObjectSelectionState afterSelection)
    {
        Label = string.IsNullOrEmpty(label) ? "Edit objects" : label;
        this.before = before;
        this.after = after;
        this.beforeSelection = beforeSelection;
        this.afterSelection = afterSelection;
    }

    public string Label { get; }

    public bool Undo(EditorSession session) =>
        Restore(session, before, beforeSelection);

    public bool Redo(EditorSession session) =>
        Restore(session, after, afterSelection);

    internal static bool TryCreate(
        string label,
        PlacedObjectsStateSnapshot before,
        PlacedObjectsStateSnapshot after,
        PlacedObjectSelectionState beforeSelection,
        PlacedObjectSelectionState afterSelection,
        out PlacedObjectCollectionHistoryEntry entry)
    {
        entry = null;
        if (before == null ||
            after == null ||
            string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
            return false;

        entry = new PlacedObjectCollectionHistoryEntry(
            label,
            before,
            after,
            beforeSelection,
            afterSelection);
        return true;
    }

    private static bool Restore(
        EditorSession session,
        PlacedObjectsStateSnapshot model,
        PlacedObjectSelectionState selection)
    {
        if (model == null || !model.Restore(session))
            return false;

        selection?.Restore(session);
        return true;
    }
}
