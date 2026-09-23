using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Factories;
using DryCycle.DevUI.DevTool.Objects;

namespace DryCycle.DevUI.DevTool.Core;

public static partial class EditorPresentationHub
{
    /// <summary>
    /// Attempts to update the expensive Objects payload without recapturing every object and every
    /// inspector adapter. This path is entered only after the caller has established stable session,
    /// room, page and collection identity.
    /// </summary>
    private static bool TryCaptureObjectPartial(
        EditorSession session,
        List<PlacedObject> live,
        int selectionCount,
        PlacedObject primarySelection,
        long selectionRevision,
        ObjectPresentationChangeHint hint,
        bool modelChanged,
        bool selectionChanged,
        bool legacyVisibilityChanged,
        out EditorObjectSnapshot[] scene,
        out EditorInspectorSnapshot inspector)
    {
        scene = current.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
        inspector = current.Inspector ?? new EditorInspectorSnapshot();

        if (live == null || scene.Length != live.Count)
            return false;

        if (modelChanged)
        {
            if (!hint.HasChanges || hint.Full || hint.Collection)
                return false;

            if (hint.AllMembers)
            {
                scene = CaptureObjectScene(session, live);
                inspector = CaptureObjectInspector(session, live, selectionCount, primarySelection);
            }
            else if (hint.Member != null)
            {
                int index = IndexOfReference(live, hint.Member);
                if (index < 0 || index >= scene.Length)
                    return false;

                EditorObjectSnapshot[] next = (EditorObjectSnapshot[])scene.Clone();
                next[index] = CaptureObjectRow(session, hint.Member, index);
                scene = next;

                // A member edit affects Inspector only when that object participates in the current
                // selection. Editing an unselected object's world handle must not rerun reflection-
                // backed property capture for an unrelated selected object.
                if (session.Selection.Contains(hint.Member))
                    inspector = CaptureObjectInspector(session, live, selectionCount, primarySelection);
            }
        }

        if (selectionChanged)
        {
            scene = PatchObjectSelection(session, live, scene);
            inspector = CaptureObjectInspector(session, live, selectionCount, primarySelection);
        }
        else if (legacyVisibilityChanged)
        {
            // Legacy visibility is Inspector presentation state only. Scene rows remain identical.
            inspector = CaptureObjectInspector(session, live, selectionCount, primarySelection);
        }

        return true;
    }

    private static EditorObjectSnapshot[] CaptureObjectScene(EditorSession session, List<PlacedObject> live)
    {
        if (live == null || live.Count == 0)
            return Array.Empty<EditorObjectSnapshot>();

        EditorObjectSnapshot[] result = new EditorObjectSnapshot[live.Count];
        for (int i = 0; i < live.Count; i++)
            result[i] = CaptureObjectRow(session, live[i], i);
        return result;
    }

    private static EditorObjectSnapshot CaptureObjectRow(EditorSession session, PlacedObject item, int index)
    {
        string typeName = item?.type?.value ?? "Unknown";
        ObjectCatalog.TryGet(typeName, out ObjectDescriptor descriptor);
        return new EditorObjectSnapshot
        {
            Index = index,
            StableId = ObjectPresentationIdentity.Get(item),
            Type = typeName,
            DisplayName = descriptor?.DisplayName ?? typeName,
            Category = descriptor?.Category ?? "Unsorted",
            Source = descriptor?.Source ?? "Unknown Source",
            PresentationKind = descriptor?.PresentationKind ?? ObjectPresentationKind.Point,
            Importance = descriptor?.Importance ?? 0,
            X = item?.pos.x ?? 0f,
            Y = item?.pos.y ?? 0f,
            Selected = session?.Selection.Contains(item) == true
        };
    }

    private static EditorObjectSnapshot[] PatchObjectSelection(
        EditorSession session,
        List<PlacedObject> live,
        EditorObjectSnapshot[] source)
    {
        if (live == null || source == null || source.Length != live.Count)
            return CaptureObjectScene(session, live);

        EditorObjectSnapshot[] next = null;
        for (int i = 0; i < source.Length; i++)
        {
            EditorObjectSnapshot row = source[i];
            bool selected = session.Selection.Contains(live[i]);
            if (row != null && row.Selected == selected)
                continue;

            next ??= (EditorObjectSnapshot[])source.Clone();
            PlacedObject item = live[i];
            next[i] = row == null
                ? CaptureObjectRow(session, item, i)
                : new EditorObjectSnapshot
                {
                    Index = row.Index,
                    StableId = row.StableId,
                    Type = row.Type,
                    DisplayName = row.DisplayName,
                    Category = row.Category,
                    Source = row.Source,
                    PresentationKind = row.PresentationKind,
                    Importance = row.Importance,
                    X = row.X,
                    Y = row.Y,
                    Selected = selected
                };
        }
        return next ?? source;
    }

    private static EditorInspectorSnapshot CaptureObjectInspector(
        EditorSession session,
        List<PlacedObject> live,
        int selectionCount,
        PlacedObject primarySelection)
    {
        PlacedObject selected = primarySelection;
        int selectedIndex = selected != null && live != null ? IndexOfReference(live, selected) : -1;

        EditorPropertySnapshot[] properties;
        string[] mixedPropertyKeys;
        if (selectionCount > 1)
            properties = MultiSelectionInspector.Capture(session.Selection.PlacedObjects, out mixedPropertyKeys);
        else
        {
            properties = ObjectInspectorRegistry.Capture(selected);
            mixedPropertyKeys = Array.Empty<string>();
        }

        bool singleSelection = selectionCount == 1 && selected != null && selectedIndex >= 0;
        bool externalObject = singleSelection &&
                              selected.type != null &&
                              !GameDefinedExtEnumCatalog.Contains(typeof(PlacedObject.Type), selected.type.value);

        LegacyControlSnapshot[] legacyControls = Array.Empty<LegacyControlSnapshot>();
        if (singleSelection)
        {
            if (session.LegacyUiVisible && session.Owner?.activePage is global::DevInterface.ObjectsPage)
            {
                legacyControls = LegacyDevInterfaceBridge.Capture(session.Owner, selected);
                LegacyObjectSandbox.Release(session);
            }
            else if (externalObject)
            {
                // Unknown third-party objects receive only one selected-object legacy representation.
                // Builtin/game-defined objects stay purely native unless the user explicitly opens
                // full Legacy UI.
                legacyControls = LegacyObjectSandbox.Capture(session, selected);
            }
            else
            {
                LegacyObjectSandbox.Release(session);
            }
        }
        else
        {
            LegacyObjectSandbox.Release(session);
        }

        return new EditorInspectorSnapshot
        {
            // Multi-selection has a real inspector payload (shared properties, group transform and
            // selection actions). HasSelection therefore means "one or more", while features that
            // require a concrete target continue to use singleSelection below.
            HasSelection = selectionCount > 0,
            ObjectIndex = selectedIndex,
            ObjectStableId = singleSelection ? ObjectPresentationIdentity.Get(selected) : 0L,
            SelectionCount = selectionCount,
            Type = selectionCount > 1 ? selectionCount + " Objects" : selected?.type?.value ?? string.Empty,
            X = selected?.pos.x ?? 0f,
            Y = selected?.pos.y ?? 0f,
            DataType = selectionCount > 1 ? "Shared properties" : selected?.data?.GetType().FullName ?? string.Empty,
            LegacyUiAvailable = singleSelection,
            LegacyUiVisible = session.LegacyUiVisible,
            Properties = properties,
            ObjectGizmo = singleSelection
                ? NativeObjectGizmoPresentation.Capture(selected, selectedIndex, properties)
                : EditorObjectGizmoSnapshot.Empty,
            MixedPropertyKeys = mixedPropertyKeys,
            LegacyControls = legacyControls
        };
    }

    private static int IndexOfReference(List<PlacedObject> values, PlacedObject target)
    {
        if (values == null || target == null) return -1;
        for (int i = 0; i < values.Count; i++)
            if (ReferenceEquals(values[i], target)) return i;
        return -1;
    }

    private static PlacedObject ResolveDraggedPlacedObject(global::DevInterface.DevUINode node)
    {
        global::DevInterface.DevUINode currentNode = node;
        while (currentNode != null)
        {
            if (currentNode is global::DevInterface.PlacedObjectRepresentation representation &&
                representation.pObj != null)
                return representation.pObj;
            currentNode = currentNode.parentNode;
        }
        return null;
    }
}
