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

public static class EditorPresentationHub
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

    internal static void Publish(EditorSession session, bool shellOnly = false)
    {
        if (session?.Owner == null)
        {
            Clear();
            return;
        }

        // History can change through keyboard shortcuts and legacy transaction completion without
        // passing through EditorUiCommandQueue. Detect that authoritative revision before reading
        // presentation revisions. The active workspace is invalidated as Undo/Redo can restore data
        // in any editor model, while Shell owns the undo/redo labels and availability flags.
        long historyRevision = session.History.Revision;
        if (ReferenceEquals(observedSession, session) &&
            observedHistoryRevision != 0L &&
            observedHistoryRevision != historyRevision)
        {
            EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
            EditorRevisionHub.MarkWorkspace(session);
        }

        bool objectWorkspace = !shellOnly && session.ToolMode == EditorToolMode.Objects;
        if (objectWorkspace &&
            (EditorRevisionHub.RequiresLiveWorkspaceRefresh(session) || session.Owner.draggedNode != null))
            EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);

        long shellRevision = EditorRevisionHub.Get(session, EditorRevisionKind.Shell);
        long objectRevision = objectWorkspace
            ? EditorRevisionHub.Get(session, EditorRevisionKind.Objects)
            : 0L;

        List<PlacedObject> live = objectWorkspace ? session.RoomSettings?.placedObjects : null;
        int objectCount = live?.Count ?? 0;
        int selectionCount = objectWorkspace ? session.Selection.Count : 0;
        PlacedObject primarySelection = objectWorkspace ? session.Selection.PrimaryPlacedObject : null;
        int typeCount = objectWorkspace ? ExtEnum<PlacedObject.Type>.values.Count : libraryTypeCount;
        bool libraryStale = objectWorkspace && (libraryTypeCount != typeCount || libraryCache.Length == 0);
        string placementType = session.PlacementType ?? string.Empty;

        // Session/page identity protects reopen and workspace transitions. Revisions cover model
        // writes; direct shell-state comparisons cover keyboard paths such as Ctrl+B, Ctrl+I, Tab
        // and Escape without requiring every caller to remember an invalidation API.
        if (!libraryStale &&
            ReferenceEquals(observedSession, session) &&
            ReferenceEquals(observedRoom, session.Room) &&
            ReferenceEquals(observedPage, session.Owner.activePage) &&
            observedShellOnly == shellOnly &&
            observedShellRevision == shellRevision &&
            observedObjectRevision == objectRevision &&
            observedHistoryRevision == historyRevision &&
            observedToolMode == session.ToolMode &&
            observedFocusMode == session.FocusMode &&
            observedBrowserOpen == session.BrowserOpen &&
            observedInspectorOpen == session.InspectorOpen &&
            observedLegacyUiVisible == session.LegacyUiVisible &&
            observedPlacementActive == session.PlacementActive &&
            string.Equals(observedPlacementType, placementType, StringComparison.Ordinal) &&
            observedObjectCount == objectCount &&
            observedSelectionCount == selectionCount &&
            ReferenceEquals(observedPrimarySelection, primarySelection) &&
            current.Available)
            return;

        EditorObjectSnapshot[] scene = Array.Empty<EditorObjectSnapshot>();
        EditorObjectTypeSnapshot[] objectLibrary = Array.Empty<EditorObjectTypeSnapshot>();
        EditorInspectorSnapshot inspector = new();

        if (objectWorkspace)
        {
            scene = live == null ? Array.Empty<EditorObjectSnapshot>() : new EditorObjectSnapshot[live.Count];
            if (live != null)
            {
                for (int i = 0; i < live.Count; i++)
                {
                    PlacedObject item = live[i];
                    scene[i] = new EditorObjectSnapshot
                    {
                        Index = i,
                        Type = item?.type?.value ?? "Unknown",
                        X = item?.pos.x ?? 0f,
                        Y = item?.pos.y ?? 0f,
                        Selected = session.Selection.Contains(item)
                    };
                }
            }

            PlacedObject selected = primarySelection;
            int selectedIndex = selected != null && live != null ? live.IndexOf(selected) : -1;

            EditorPropertySnapshot[] properties;
            string[] mixedPropertyKeys;
            if (selectionCount > 1)
                properties = MultiSelectionInspector.Capture(session.Selection.PlacedObjects, out mixedPropertyKeys);
            else
            {
                properties = ObjectInspectorRegistry.Capture(selected);
                mixedPropertyKeys = Array.Empty<string>();
            }

            inspector = new EditorInspectorSnapshot
            {
                HasSelection = selected != null && selectedIndex >= 0,
                ObjectIndex = selectedIndex,
                SelectionCount = selectionCount,
                Type = selectionCount > 1 ? selectionCount + " Objects" : selected?.type?.value ?? string.Empty,
                X = selected?.pos.x ?? 0f,
                Y = selected?.pos.y ?? 0f,
                DataType = selectionCount > 1 ? "Shared properties" : selected?.data?.GetType().FullName ?? string.Empty,
                LegacyUiAvailable = selectionCount == 1,
                LegacyUiVisible = session.LegacyUiVisible,
                Properties = properties,
                MixedPropertyKeys = mixedPropertyKeys,
                LegacyControls = selectionCount == 1
                    ? LegacyDevInterfaceBridge.Capture(session.Owner, selected)
                    : Array.Empty<LegacyControlSnapshot>()
            };

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
    }

    internal static void Clear()
    {
        current = EditorPresentationSnapshot.Empty;
        observedSession = null;
        observedRoom = null;
        observedPage = null;
        observedShellRevision = 0L;
        observedObjectRevision = 0L;
        observedHistoryRevision = 0L;
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
        while (queue.TryDequeue(out EditorUiCommand command))
        {
            try
            {
                Execute(session, command);
                InvalidateAfterCommand(session, command.Kind);
            }
            catch (Exception error)
            {
                Plugin.Logger?.LogWarning("DevTool UI command failed: " + error.Message);
            }
        }
    }

    internal static void Clear()
    {
        while (queue.TryDequeue(out _)) { }
    }

    private static void InvalidateAfterCommand(EditorSession session, EditorUiCommandKind kind)
    {
        switch (kind)
        {
            case EditorUiCommandKind.Save:
                return;

            case EditorUiCommandKind.ToggleFocus:
            case EditorUiCommandKind.ToggleBrowser:
            case EditorUiCommandKind.ToggleInspector:
            case EditorUiCommandKind.BeginPlacement:
            case EditorUiCommandKind.CancelPlacement:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
                return;

            case EditorUiCommandKind.SelectObject:
            case EditorUiCommandKind.ToggleObjectSelection:
            case EditorUiCommandKind.SelectObjectRange:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
                return;

            case EditorUiCommandKind.SetToolMode:
            case EditorUiCommandKind.ToggleLegacyUi:
            case EditorUiCommandKind.Undo:
            case EditorUiCommandKind.Redo:
                EditorRevisionHub.MarkShellAndWorkspace(session);
                return;

            default:
                EditorRevisionHub.Mark(session, EditorRevisionKind.Shell);
                EditorRevisionHub.Mark(session, EditorRevisionKind.Objects);
                return;
        }
    }

    private static void Execute(EditorSession session, EditorUiCommand command)
    {
        switch (command.Kind)
        {
            case EditorUiCommandKind.Save:
                EditorActions.Save(session);
                break;
            case EditorUiCommandKind.Undo:
                EditorActions.Undo(session);
                break;
            case EditorUiCommandKind.Redo:
                EditorActions.Redo(session);
                break;
            case EditorUiCommandKind.ToggleFocus:
                session.ToggleFocusMode();
                break;
            case EditorUiCommandKind.ToggleBrowser:
                session.ToggleBrowser();
                break;
            case EditorUiCommandKind.ToggleInspector:
                session.ToggleInspector();
                break;
            case EditorUiCommandKind.ToggleLegacyUi:
                session.ToggleLegacyUi();
                break;
            case EditorUiCommandKind.SetToolMode:
                session.SetToolMode(command.Mode);
                break;
            case EditorUiCommandKind.SelectObject:
                session.Selection.SelectOnly(ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.ToggleObjectSelection:
                session.Selection.Toggle(ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.SelectObjectRange:
                session.Selection.SelectRange(session.RoomSettings?.placedObjects, command.SecondaryIndex, command.Index, command.Flag);
                break;
            case EditorUiCommandKind.DeleteObject:
                EditorActions.DeleteObject(session, ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.DeleteSelection:
                EditorActions.DeleteSelection(session);
                break;
            case EditorUiCommandKind.DuplicateSelection:
                EditorActions.DuplicateSelection(session);
                break;
            case EditorUiCommandKind.CreateObject:
                if (!string.IsNullOrEmpty(command.Text))
                    EditorActions.CreateObject(session, new PlacedObject.Type(command.Text, false), new Vector2(command.X, command.Y));
                break;
            case EditorUiCommandKind.BeginPlacement:
                session.BeginPlacement(command.Text);
                break;
            case EditorUiCommandKind.PlaceObjectAtCursor:
                EditorActions.PlaceObjectAtCursor(session, command.Flag);
                break;
            case EditorUiCommandKind.CancelPlacement:
                session.CancelPlacement();
                break;
            case EditorUiCommandKind.SetObjectPosition:
                EditorActions.SetObjectPosition(session, ResolveObject(session, command.Index), new Vector2(command.X, command.Y));
                break;
            case EditorUiCommandKind.SetSelectionPosition:
                EditorActions.SetSelectionPrimaryPosition(session, new Vector2(command.X, command.Y));
                break;
            case EditorUiCommandKind.SetObjectProperty:
                EditorActions.SetObjectProperty(session, ResolveObject(session, command.Index), command.Text, command.PropertyValue);
                break;
            case EditorUiCommandKind.SetSelectionProperty:
                EditorActions.SetSelectionProperty(session, command.Text, command.PropertyValue);
                break;
            case EditorUiCommandKind.InvokeLegacyButton:
                EditorActions.InvokeLegacyButton(session, ResolveObject(session, command.Index), command.Text);
                break;
            case EditorUiCommandKind.SetLegacySlider:
                EditorActions.SetLegacySlider(session, ResolveObject(session, command.Index), command.Text, command.X);
                break;
            case EditorUiCommandKind.ResetLegacySlider:
                EditorActions.ResetLegacySlider(session, ResolveObject(session, command.Index), command.Text);
                break;
            case EditorUiCommandKind.SetLegacyText:
                EditorActions.SetLegacyText(session, ResolveObject(session, command.Index), command.Text, command.PropertyValue.Text);
                break;
            case EditorUiCommandKind.SetLegacyDirection:
                EditorActions.SetLegacyDirection(session, ResolveObject(session, command.Index), command.Text, command.X, command.Y);
                break;
            case EditorUiCommandKind.SetLegacyColor:
                EditorActions.SetLegacyColor(
                    session,
                    ResolveObject(session, command.Index),
                    command.Text,
                    command.PropertyValue.X,
                    command.PropertyValue.Y,
                    command.PropertyValue.Z,
                    command.PropertyValue.W);
                break;
        }
    }

    private static PlacedObject ResolveObject(EditorSession session, int index)
    {
        List<PlacedObject> objects = session?.RoomSettings?.placedObjects;
        return objects != null && index >= 0 && index < objects.Count ? objects[index] : null;
    }
}
