using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Commands;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Objects;
using UnityEngine;

namespace DryCycle.DevUI.DevTool.Core;

public sealed class EditorObjectSnapshot
{
    public int Index { get; init; }
    public string Type { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public bool Selected { get; init; }
}

public sealed class EditorObjectTypeSnapshot
{
    public string Type { get; init; }
    public string DisplayName { get; init; }
    public string Category { get; init; }
    public string Source { get; init; }
    public string[] Tags { get; init; }
}

public sealed class EditorInspectorSnapshot
{
    public bool HasSelection { get; init; }
    public int ObjectIndex { get; init; } = -1;
    public int SelectionCount { get; init; }
    public string Type { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public string DataType { get; init; } = string.Empty;
    public bool LegacyUiAvailable { get; init; }
    public bool LegacyUiVisible { get; init; }
    public EditorPropertySnapshot[] Properties { get; init; } = Array.Empty<EditorPropertySnapshot>();
    public EditorObjectGizmoSnapshot ObjectGizmo { get; init; } = EditorObjectGizmoSnapshot.Empty;
    public string[] MixedPropertyKeys { get; init; } = Array.Empty<string>();
    public LegacyControlSnapshot[] LegacyControls { get; init; } = Array.Empty<LegacyControlSnapshot>();
}

public sealed class EditorPresentationSnapshot
{
    public static readonly EditorPresentationSnapshot Empty = new();

    public bool Available { get; init; }
    public bool Hydrated { get; init; }
    public bool DevToolsActive { get; init; }
    public string Document { get; init; } = string.Empty;
    public string RoomName { get; init; } = string.Empty;
    public EditorToolMode ToolMode { get; init; }
    public bool FocusMode { get; init; }
    public bool BrowserOpen { get; init; }
    public bool InspectorOpen { get; init; }
    public bool CanUndo { get; init; }
    public bool CanRedo { get; init; }
    public string UndoLabel { get; init; } = string.Empty;
    public string RedoLabel { get; init; } = string.Empty;
    public bool PlacementActive { get; init; }
    public string PlacementType { get; init; } = string.Empty;
    public EditorObjectSnapshot[] SceneObjects { get; init; } = Array.Empty<EditorObjectSnapshot>();
    public EditorObjectTypeSnapshot[] ObjectLibrary { get; init; } = Array.Empty<EditorObjectTypeSnapshot>();
    public EditorInspectorSnapshot Inspector { get; init; } = new();
}

public static partial class EditorPresentationHub
{
    private static volatile EditorPresentationSnapshot current = EditorPresentationSnapshot.Empty;
    private static EditorObjectTypeSnapshot[] libraryCache = Array.Empty<EditorObjectTypeSnapshot>();
    private static int libraryTypeCount = -1;

    private static EditorSession observedSession;
    private static global::Room observedRoom;
    private static global::DevInterface.Page observedPage;
    private static long observedShellRevision;
    private static long observedObjectRevision;
    private static long observedHistoryRevision;
    private static long observedSelectionRevision;
    private static bool observedShellOnly;
    private static EditorToolMode observedToolMode;
    private static bool observedFocusMode;
    private static bool observedBrowserOpen;
    private static bool observedInspectorOpen;
    private static bool observedLegacyUiVisible;
    private static bool observedPlacementActive;
    private static string observedPlacementType = string.Empty;
    private static int observedObjectCount = -1;
    private static int observedSelectionCount = -1;
    private static PlacedObject observedPrimarySelection;

    public static EditorPresentationSnapshot Current => current;
    internal static DevToolPresentationOutcome LastOutcome { get; private set; } = DevToolPresentationOutcome.FullRebuild;

    internal static void Publish(EditorSession session, bool shellOnly = false)
    {
        if (session?.Owner == null)
        {
            Clear();
            return;
        }

        // History can change through keyboard shortcuts and legacy transaction completion without
        // passing through EditorUiCommandQueue. Detect that authoritative revision before reading
        // presentation revisions. Known history snapshots may also have supplied a semantic Object
        // hint; unknown history remains safe because absence of a hint forces a full object capture.
        long historyRevision = session.History.Revision;
        if (ReferenceEquals(observedSession, session) &&
            observedHistoryRevision != 0L &&
            observedHistoryRevision != historyRevision)
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
            EditorRevisionHub.MarkWorkspace(session);
        }

        bool objectWorkspace = !shellOnly && session.ToolMode == EditorToolMode.Objects;
        if (objectWorkspace)
        {
            PlacedObject draggedObject = ResolveDraggedPlacedObject(session.Owner.draggedNode);
            if (draggedObject == null && session.Owner.activePage is global::DevInterface.ObjectsPage objectsPage)
                draggedObject = objectsPage.draggedObject?.pObj;

            if (draggedObject != null)
            {
                // A live world handle is a trusted one-member writer even though the legacy
                // transaction recorder is intentionally active around the gesture.
                EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
                ObjectPresentationChangeHintHub.MarkMember(session, draggedObject);
            }
            else if (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session) || session.Owner.draggedNode != null)
            {
                EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
                ObjectPresentationChangeHintHub.MarkFull(session);
            }
        }

        long shellRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Shell);
        long objectRevision = objectWorkspace
            ? EditorRevisionHub.Get(session, EditorRevisionKind.Objects)
            : 0L;
        long selectionRevision = objectWorkspace ? session.Selection.Revision : 0L;

        List<PlacedObject> live = objectWorkspace ? session.RoomSettings?.placedObjects : null;
        int objectCount = live?.Count ?? 0;
        int selectionCount = objectWorkspace ? session.Selection.Count : 0;
        PlacedObject primarySelection = objectWorkspace ? session.Selection.PrimaryPlacedObject : null;
        int typeCount = objectWorkspace ? ExtEnum<PlacedObject.Type>.values.Count : libraryTypeCount;
        bool libraryStale = objectWorkspace && (libraryTypeCount != typeCount || libraryCache.Length == 0);
        string placementType = session.PlacementType ?? string.Empty;

        bool sameIdentity =
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedRoom, session.Room) &&
            ReferenceEquals(observedPage, session.Owner.activePage) &&
            current.Available;

        if (!libraryStale &&
            sameIdentity &&
            observedShellOnly == shellOnly &&
            observedShellRevision == shellRevision &&
            observedObjectRevision == objectRevision &&
            observedHistoryRevision == historyRevision &&
            observedSelectionRevision == selectionRevision &&
            observedToolMode == session.ToolMode &&
            observedFocusMode == session.FocusMode &&
            observedBrowserOpen == session.BrowserOpen &&
            observedInspectorOpen == session.InspectorOpen &&
            observedLegacyUiVisible == session.LegacyUiVisible &&
            observedPlacementActive == session.PlacementActive &&
            string.Equals(observedPlacementType, placementType, StringComparison.Ordinal) &&
            observedObjectCount == objectCount &&
            observedSelectionCount == selectionCount &&
            ReferenceEquals(observedPrimarySelection, primarySelection))
        {
            LastOutcome = DevToolPresentationOutcome.CacheHit;
            return;
        }

        EditorObjectSnapshot[] scene = Array.Empty<EditorObjectSnapshot>();
        EditorObjectTypeSnapshot[] objectLibrary = Array.Empty<EditorObjectTypeSnapshot>();
        EditorInspectorSnapshot inspector = new();
        bool objectFullCapture = false;
        bool objectPartialCapture = false;

        if (objectWorkspace)
        {
            bool stableObjectContext =
                current.Available &&
                current.Hydrated &&
                current.ToolMode == EditorToolMode.Objects &&
                sameIdentity &&
                observedObjectCount == objectCount;

            bool modelChanged = observedObjectRevision != objectRevision;
            bool selectionChanged =
                observedSelectionRevision != selectionRevision ||
                observedSelectionCount != selectionCount ||
                !ReferenceEquals(observedPrimarySelection, primarySelection);
            bool legacyVisibilityChanged = observedLegacyUiVisible != session.LegacyUiVisible;

            bool objectPayloadStable =
                stableObjectContext &&
                !modelChanged &&
                !selectionChanged &&
                !legacyVisibilityChanged;

            if (objectPayloadStable)
            {
                scene = current.SceneObjects ?? Array.Empty<EditorObjectSnapshot>();
                inspector = current.Inspector ?? new EditorInspectorSnapshot();
            }
            else
            {
                ObjectPresentationChangeHint hint = modelChanged
                    ? ObjectPresentationChangeHintHub.Consume(session)
                    : default;

                if (stableObjectContext &&
                    TryCaptureObjectPartial(
                        session,
                        live,
                        selectionCount,
                        primarySelection,
                        selectionRevision,
                        hint,
                        modelChanged,
                        selectionChanged,
                        legacyVisibilityChanged,
                        out scene,
                        out inspector))
                {
                    objectPartialCapture = true;
                }
                else
                {
                    scene = CaptureObjectScene(session, live);
                    inspector = CaptureObjectInspector(session, live, selectionCount, primarySelection);
                    objectFullCapture = true;
                }
            }

            if (libraryStale)
                RebuildLibraryCache(typeCount);
            objectLibrary = libraryCache;
        }

        current = new EditorPresentationSnapshot
        {
            Available = true,
            Hydrated = !shellOnly,
            DevToolsActive = session.Owner.game?.devToolsActive == true,
            Document = session.DocumentKey.ToString(),
            RoomName = session.Room?.abstractRoom?.name ?? session.RoomSettings?.name ?? string.Empty,
            ToolMode = session.ToolMode,
            FocusMode = session.FocusMode,
            BrowserOpen = session.BrowserOpen,
            InspectorOpen = session.InspectorOpen,
            CanUndo = session.History.CanUndo,
            CanRedo = session.History.CanRedo,
            UndoLabel = session.History.UndoLabel ?? string.Empty,
            RedoLabel = session.History.RedoLabel ?? string.Empty,
            PlacementActive = session.PlacementActive,
            PlacementType = placementType,
            SceneObjects = scene,
            ObjectLibrary = objectLibrary,
            Inspector = inspector
        };

        observedSession = session;
        observedRoom = session.Room;
        observedPage = session.Owner.activePage;
        observedShellRevision = shellRevision;
        observedObjectRevision = objectRevision;
        observedHistoryRevision = historyRevision;
        observedSelectionRevision = selectionRevision;
        observedShellOnly = shellOnly;
        observedToolMode = session.ToolMode;
        observedFocusMode = session.FocusMode;
        observedBrowserOpen = session.BrowserOpen;
        observedInspectorOpen = session.InspectorOpen;
        observedLegacyUiVisible = session.LegacyUiVisible;
        observedPlacementActive = session.PlacementActive;
        observedPlacementType = placementType;
        observedObjectCount = objectCount;
        observedSelectionCount = selectionCount;
        observedPrimarySelection = primarySelection;

        if (objectWorkspace)
        {
            LastOutcome = objectFullCapture
                ? DevToolPresentationOutcome.FullRebuild
                : DevToolPresentationOutcome.PartialRebuild;
        }
        else
        {
            // Core shell snapshots are intentionally light; once a session already exists, replacing
            // shell metadata is a partial rebuild even when no Objects payload is active.
            LastOutcome = sameIdentity || objectPartialCapture
                ? DevToolPresentationOutcome.PartialRebuild
                : DevToolPresentationOutcome.FullRebuild;
        }
    }

    internal static void Clear()
    {
        ObjectPresentationChangeHintHub.Clear(observedSession);
        current = EditorPresentationSnapshot.Empty;
        observedSession = null;
        observedRoom = null;
        observedPage = null;
        observedShellRevision = 0L;
        observedObjectRevision = 0L;
        observedHistoryRevision = 0L;
        observedSelectionRevision = 0L;
        observedShellOnly = false;
        observedToolMode = EditorToolMode.Room;
        observedFocusMode = false;
        observedBrowserOpen = false;
        observedInspectorOpen = false;
        observedLegacyUiVisible = false;
        observedPlacementActive = false;
        observedPlacementType = string.Empty;
        observedObjectCount = -1;
        observedSelectionCount = -1;
        observedPrimarySelection = null;
        LastOutcome = DevToolPresentationOutcome.FullRebuild;
    }

    internal static void InvalidateObjectLibrary()
    {
        libraryTypeCount = -1;
        libraryCache = Array.Empty<EditorObjectTypeSnapshot>();
    }

    private static void RebuildLibraryCache(int typeCount)
    {
        IReadOnlyList<ObjectDescriptor> descriptors = ObjectCatalog.GetAll();
        EditorObjectTypeSnapshot[] next = new EditorObjectTypeSnapshot[descriptors.Count];
        for (int i = 0; i < descriptors.Count; i++)
        {
            ObjectDescriptor descriptor = descriptors[i];
            string[] tags = new string[descriptor.Tags.Count];
            for (int t = 0; t < tags.Length; t++) tags[t] = descriptor.Tags[t];
            next[i] = new EditorObjectTypeSnapshot
            {
                Type = descriptor.Type?.value ?? string.Empty,
                DisplayName = descriptor.DisplayName,
                Category = descriptor.Category,
                Source = descriptor.Source,
                Tags = tags
            };
        }
        libraryCache = next;
        libraryTypeCount = typeCount;
    }
}

public enum EditorUiCommandKind
{
    Save,
    Undo,
    Redo,
    ToggleFocus,
    ToggleBrowser,
    ToggleInspector,
    ToggleLegacyUi,
    SetToolMode,
    SelectObject,
    ToggleObjectSelection,
    SelectObjectRange,
    DeleteObject,
    DeleteSelection,
    DuplicateSelection,
    CreateObject,
    BeginPlacement,
    PlaceObjectAtCursor,
    CancelPlacement,
    SetObjectPosition,
    SetSelectionPosition,
    SnapSelectionToGrid,
    AlignSelection,
    DistributeSelection,
    SetObjectProperty,
    SetSelectionProperty,
    InvokeLegacyButton,
    SetLegacySlider,
    ResetLegacySlider,
    SetLegacyText,
    SetLegacyDirection,
    SetLegacyColor
}

public readonly struct EditorUiCommand
{
    public EditorUiCommand(
        EditorUiCommandKind kind,
        int index = -1,
        int secondaryIndex = -1,
        string text = null,
        float x = 0f,
        float y = 0f,
        bool flag = false,
        EditorToolMode mode = EditorToolMode.Room,
        EditorPropertyValue propertyValue = default)
    {
        Kind = kind;
        Index = index;
        SecondaryIndex = secondaryIndex;
        Text = text;
        X = x;
        Y = y;
        Flag = flag;
        Mode = mode;
        PropertyValue = propertyValue;
    }

    public EditorUiCommandKind Kind { get; }
    public int Index { get; }
    public int SecondaryIndex { get; }
    public string Text { get; }
    public float X { get; }
    public float Y { get; }
    public bool Flag { get; }
    public EditorToolMode Mode { get; }
    public EditorPropertyValue PropertyValue { get; }
}

public static class EditorUiCommandQueue
{
    private static readonly ConcurrentQueue<EditorUiCommand> queue = new();

    public static void Enqueue(EditorUiCommand command) => queue.Enqueue(command);

    internal static void Process(EditorSession session)
    {
        if (session == null) return;

        long historyBeforeBatch = session.History.Revision;
        bool nonHistoryObjectDirty = false;

        while (queue.TryDequeue(out EditorUiCommand command))
        {
            try
            {
                long historyBeforeCommand = session.History.Revision;
                long selectionBefore = session.Selection.Revision;
                int objectCountBefore = session.RoomSettings?.placedObjects?.Count ?? 0;
                PlacedObject commandTarget = ResolveObject(session, command.Index);
                int selectionCountBefore = session.Selection.Count;
                PlacedObject primaryBefore = session.Selection.PrimaryPlacedObject;

                bool objectCommandSucceeded = Execute(session, command);

                long historyAfterCommand = session.History.Revision;
                long selectionAfter = session.Selection.Revision;
                int objectCountAfter = session.RoomSettings?.placedObjects?.Count ?? 0;
                bool historyChanged = historyAfterCommand != historyBeforeCommand;
                bool membershipChanged = objectCountBefore != objectCountAfter;
                bool selectionChanged = selectionBefore != selectionAfter;

                bool objectModelCommand = IsObjectModelCommand(command.Kind);
                bool observableModelChange = objectModelCommand &&
                    (historyChanged || membershipChanged || objectCommandSucceeded);

                if (observableModelChange)
                {
                    MarkObjectPresentationChange(
                        session,
                        command,
                        commandTarget,
                        membershipChanged,
                        selectionCountBefore,
                        primaryBefore);

                    if (!historyChanged)
                        nonHistoryObjectDirty = true;
                }

                // Selection has its own revision and never needs to bump Objects model revision.
                _ = selectionChanged;
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool UI command failed: " + error.Message);
            }
        }

        // History mutations are converted into Shell + active-workspace invalidation by the core
        // presentation hub. Direct compatibility writes without history retain one explicit Objects
        // revision for the whole queue batch.
        if (nonHistoryObjectDirty && session.History.Revision == historyBeforeBatch)
            EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private static void MarkObjectPresentationChange(
        EditorSession session,
        EditorUiCommand command,
        PlacedObject commandTarget,
        bool membershipChanged,
        int selectionCountBefore,
        PlacedObject primaryBefore)
    {
        if (membershipChanged)
        {
            ObjectPresentationChangeHintHub.MarkCollection(session);
            return;
        }

        switch (command.Kind)
        {
            case EditorUiCommandKind.SetObjectPosition:
            case EditorUiCommandKind.SetObjectProperty:
                ObjectPresentationChangeHintHub.MarkMember(session, commandTarget);
                break;

            case EditorUiCommandKind.SetSelectionPosition:
            case EditorUiCommandKind.SnapSelectionToGrid:
            case EditorUiCommandKind.AlignSelection:
            case EditorUiCommandKind.DistributeSelection:
            case EditorUiCommandKind.SetSelectionProperty:
                if (selectionCountBefore == 1)
                    ObjectPresentationChangeHintHub.MarkMember(session, primaryBefore);
                else
                    ObjectPresentationChangeHintHub.MarkAllMembers(session);
                break;

            case EditorUiCommandKind.DeleteObject:
            case EditorUiCommandKind.DeleteSelection:
            case EditorUiCommandKind.DuplicateSelection:
            case EditorUiCommandKind.CreateObject:
            case EditorUiCommandKind.PlaceObjectAtCursor:
                ObjectPresentationChangeHintHub.MarkCollection(session);
                break;

            // A third-party legacy control can legally mutate more than the object represented by
            // the clicked panel. Keep those bridge actions conservative unless a dedicated adapter
            // provides a narrower contract in the future.
            case EditorUiCommandKind.InvokeLegacyButton:
            case EditorUiCommandKind.SetLegacySlider:
            case EditorUiCommandKind.ResetLegacySlider:
            case EditorUiCommandKind.SetLegacyText:
            case EditorUiCommandKind.SetLegacyDirection:
            case EditorUiCommandKind.SetLegacyColor:
                ObjectPresentationChangeHintHub.MarkFull(session);
                break;

            default:
                ObjectPresentationChangeHintHub.MarkFull(session);
                break;
        }
    }

    private static bool Execute(EditorSession session, EditorUiCommand command)
    {
        switch (command.Kind)
        {
            case EditorUiCommandKind.Save:
                EditorActions.Save(session);
                return false;
            case EditorUiCommandKind.Undo:
                EditorActions.Undo(session);
                return false;
            case EditorUiCommandKind.Redo:
                EditorActions.Redo(session);
                return false;
            case EditorUiCommandKind.ToggleFocus:
                session.ToggleFocusMode();
                return false;
            case EditorUiCommandKind.ToggleBrowser:
                session.ToggleBrowser();
                return false;
            case EditorUiCommandKind.ToggleInspector:
                session.ToggleInspector();
                return false;
            case EditorUiCommandKind.ToggleLegacyUi:
                session.ToggleLegacyUi();
                return false;
            case EditorUiCommandKind.SetToolMode:
                session.SetToolMode(command.Mode);
                return false;
            case EditorUiCommandKind.SelectObject:
                session.Selection.SelectOnly(ResolveObject(session, command.Index));
                return false;
            case EditorUiCommandKind.ToggleObjectSelection:
                session.Selection.Toggle(ResolveObject(session, command.Index));
                return false;
            case EditorUiCommandKind.SelectObjectRange:
                session.Selection.SelectRange(session.RoomSettings?.placedObjects, command.SecondaryIndex, command.Index, command.Flag);
                return false;
            case EditorUiCommandKind.DeleteObject:
                return EditorActions.DeleteObject(session, ResolveObject(session, command.Index));
            case EditorUiCommandKind.DeleteSelection:
                return EditorActions.DeleteSelection(session);
            case EditorUiCommandKind.DuplicateSelection:
                return EditorActions.DuplicateSelection(session);
            case EditorUiCommandKind.CreateObject:
                return !string.IsNullOrEmpty(command.Text) &&
                       EditorActions.CreateObject(
                           session,
                           new PlacedObject.Type(command.Text, false),
                           new Vector2(command.X, command.Y)) != null;
            case EditorUiCommandKind.BeginPlacement:
                session.BeginPlacement(command.Text);
                return false;
            case EditorUiCommandKind.PlaceObjectAtCursor:
                return EditorActions.PlaceObjectAtCursor(session, command.Flag);
            case EditorUiCommandKind.CancelPlacement:
                session.CancelPlacement();
                return false;
            case EditorUiCommandKind.SetObjectPosition:
                return EditorActions.SetObjectPosition(session, ResolveObject(session, command.Index), new Vector2(command.X, command.Y));
            case EditorUiCommandKind.SetSelectionPosition:
                return EditorActions.SetSelectionPrimaryPosition(session, new Vector2(command.X, command.Y));
            case EditorUiCommandKind.SnapSelectionToGrid:
                return EditorActions.SnapSelectionToGrid(session, command.X);
            case EditorUiCommandKind.AlignSelection:
                return Enum.IsDefined(typeof(ObjectSelectionAlignment), command.Index) &&
                       EditorActions.AlignSelection(session, (ObjectSelectionAlignment)command.Index);
            case EditorUiCommandKind.DistributeSelection:
                return Enum.IsDefined(typeof(ObjectSelectionDistribution), command.Index) &&
                       EditorActions.DistributeSelection(session, (ObjectSelectionDistribution)command.Index);
            case EditorUiCommandKind.SetObjectProperty:
                return EditorActions.SetObjectProperty(session, ResolveObject(session, command.Index), command.Text, command.PropertyValue);
            case EditorUiCommandKind.SetSelectionProperty:
                return EditorActions.SetSelectionProperty(session, command.Text, command.PropertyValue);
            case EditorUiCommandKind.InvokeLegacyButton:
                return EditorActions.InvokeLegacyButton(session, ResolveObject(session, command.Index), command.Text);
            case EditorUiCommandKind.SetLegacySlider:
                return EditorActions.SetLegacySlider(session, ResolveObject(session, command.Index), command.Text, command.X);
            case EditorUiCommandKind.ResetLegacySlider:
                return EditorActions.ResetLegacySlider(session, ResolveObject(session, command.Index), command.Text);
            case EditorUiCommandKind.SetLegacyText:
                return EditorActions.SetLegacyText(session, ResolveObject(session, command.Index), command.Text, command.PropertyValue.Text);
            case EditorUiCommandKind.SetLegacyDirection:
                return EditorActions.SetLegacyDirection(session, ResolveObject(session, command.Index), command.Text, command.X, command.Y);
            case EditorUiCommandKind.SetLegacyColor:
                return EditorActions.SetLegacyColor(
                    session,
                    ResolveObject(session, command.Index),
                    command.Text,
                    command.PropertyValue.X,
                    command.PropertyValue.Y,
                    command.PropertyValue.Z,
                    command.PropertyValue.W);
            default:
                return false;
        }
    }

    private static bool IsObjectModelCommand(EditorUiCommandKind kind) => kind is
        EditorUiCommandKind.DeleteObject or
        EditorUiCommandKind.DeleteSelection or
        EditorUiCommandKind.DuplicateSelection or
        EditorUiCommandKind.CreateObject or
        EditorUiCommandKind.PlaceObjectAtCursor or
        EditorUiCommandKind.SetObjectPosition or
        EditorUiCommandKind.SetSelectionPosition or
        EditorUiCommandKind.SnapSelectionToGrid or
        EditorUiCommandKind.AlignSelection or
        EditorUiCommandKind.DistributeSelection or
        EditorUiCommandKind.SetObjectProperty or
        EditorUiCommandKind.SetSelectionProperty or
        EditorUiCommandKind.InvokeLegacyButton or
        EditorUiCommandKind.SetLegacySlider or
        EditorUiCommandKind.ResetLegacySlider or
        EditorUiCommandKind.SetLegacyText or
        EditorUiCommandKind.SetLegacyDirection or
        EditorUiCommandKind.SetLegacyColor;

    private static PlacedObject ResolveObject(EditorSession session, int index)
    {
        List<PlacedObject> objects = session?.RoomSettings?.placedObjects;
        return objects != null && index >= 0 && index < objects.Count ? objects[index] : null;
    }
}
