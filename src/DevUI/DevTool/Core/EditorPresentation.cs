using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Commands;
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
    public string Type { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public string DataType { get; init; } = string.Empty;
    public string SerializedData { get; init; } = string.Empty;
}

/// <summary>
/// Immutable-enough detached presentation model created on Unity's main thread. The
/// RWImGui Present callback consumes only these scalar/string/array values and never
/// mutates Rain World state directly.
/// </summary>
public sealed class EditorPresentationSnapshot
{
    public static readonly EditorPresentationSnapshot Empty = new();

    public bool Available { get; init; }
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
    public EditorObjectSnapshot[] SceneObjects { get; init; } = Array.Empty<EditorObjectSnapshot>();
    public EditorObjectTypeSnapshot[] ObjectLibrary { get; init; } = Array.Empty<EditorObjectTypeSnapshot>();
    public EditorInspectorSnapshot Inspector { get; init; } = new();
}

public static class EditorPresentationHub
{
    private static volatile EditorPresentationSnapshot current = EditorPresentationSnapshot.Empty;
    private static EditorObjectTypeSnapshot[] libraryCache = Array.Empty<EditorObjectTypeSnapshot>();
    private static int libraryTypeCount = -1;

    public static EditorPresentationSnapshot Current => current;

    internal static void Publish(EditorSession session)
    {
        if (session?.Owner == null)
        {
            current = EditorPresentationSnapshot.Empty;
            return;
        }

        List<PlacedObject> live = session.RoomSettings?.placedObjects;
        EditorObjectSnapshot[] scene = live == null ? Array.Empty<EditorObjectSnapshot>() : new EditorObjectSnapshot[live.Count];
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

        PlacedObject selected = session.Selection.PrimaryPlacedObject;
        int selectedIndex = selected != null && live != null ? live.IndexOf(selected) : -1;
        EditorInspectorSnapshot inspector = new()
        {
            HasSelection = selected != null && selectedIndex >= 0,
            ObjectIndex = selectedIndex,
            Type = selected?.type?.value ?? string.Empty,
            X = selected?.pos.x ?? 0f,
            Y = selected?.pos.y ?? 0f,
            DataType = selected?.data?.GetType().FullName ?? string.Empty,
            SerializedData = SafeDataString(selected)
        };

        int typeCount = ExtEnum<PlacedObject.Type>.values.Count;
        if (libraryTypeCount != typeCount || libraryCache.Length == 0)
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

        current = new EditorPresentationSnapshot
        {
            Available = true,
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
            SceneObjects = scene,
            ObjectLibrary = libraryCache,
            Inspector = inspector
        };
    }

    internal static void Clear() => current = EditorPresentationSnapshot.Empty;

    private static string SafeDataString(PlacedObject item)
    {
        try { return item?.data?.ToString() ?? string.Empty; }
        catch { return "<serialization failed>"; }
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
    SetToolMode,
    SelectObject,
    ToggleObjectSelection,
    DeleteObject,
    CreateObject,
    SetObjectPosition
}

public readonly struct EditorUiCommand
{
    public EditorUiCommand(EditorUiCommandKind kind, int index = -1, string text = null, float x = 0f, float y = 0f, EditorToolMode mode = EditorToolMode.Room)
    {
        Kind = kind;
        Index = index;
        Text = text;
        X = x;
        Y = y;
        Mode = mode;
    }

    public EditorUiCommandKind Kind { get; }
    public int Index { get; }
    public string Text { get; }
    public float X { get; }
    public float Y { get; }
    public EditorToolMode Mode { get; }
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
            try { Execute(session, command); }
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
            case EditorUiCommandKind.SetToolMode:
                session.SetToolMode(command.Mode);
                break;
            case EditorUiCommandKind.SelectObject:
                session.Selection.SelectOnly(ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.ToggleObjectSelection:
                session.Selection.Toggle(ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.DeleteObject:
                EditorActions.DeleteObject(session, ResolveObject(session, command.Index));
                break;
            case EditorUiCommandKind.CreateObject:
                if (!string.IsNullOrEmpty(command.Text))
                    EditorActions.CreateObject(session, new PlacedObject.Type(command.Text, false), new Vector2(command.X, command.Y));
                break;
            case EditorUiCommandKind.SetObjectPosition:
                EditorActions.SetObjectPosition(session, ResolveObject(session, command.Index), new Vector2(command.X, command.Y));
                break;
        }
    }

    private static PlacedObject ResolveObject(EditorSession session, int index)
    {
        List<PlacedObject> objects = session?.RoomSettings?.placedObjects;
        return objects != null && index >= 0 && index < objects.Count ? objects[index] : null;
    }
}
