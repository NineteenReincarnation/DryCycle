using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Owns the lifetime of the new editor model. The legacy DevInterface remains alive as a
/// compatibility backend; presentation is supplied by the optional RWImGui frontend.
/// </summary>
internal static class DevToolRuntime
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        On.DevInterface.DevUI.Update += DevUI_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.DevUI.Update -= DevUI_Update;
        DevToolSessionHub.Reset();
        enabled = false;
    }

    private static void DevUI_Update(On.DevInterface.DevUI.orig_Update orig, global::DevInterface.DevUI self)
    {
        orig(self);
        if (self != null)
            DevToolSessionHub.Synchronize(self);
    }
}

public enum EditorToolMode
{
    Room,
    Objects,
    Sound,
    Triggers,
    Map,
    Dialog,
    Relationships
}

public enum EditorDocumentKind
{
    Room,
    RegionMap,
    Relationships
}

/// <summary>
/// Stable history boundary. Tool-mode changes do not create a new document.
/// </summary>
public readonly struct EditorDocumentKey : IEquatable<EditorDocumentKey>
{
    public EditorDocumentKey(EditorDocumentKind kind, string identity)
    {
        Kind = kind;
        Identity = identity ?? string.Empty;
    }

    public EditorDocumentKind Kind { get; }
    public string Identity { get; }

    public bool Equals(EditorDocumentKey other) =>
        Kind == other.Kind && string.Equals(Identity, other.Identity, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is EditorDocumentKey other && Equals(other);
    public override int GetHashCode() => ((int)Kind * 397) ^ StringComparer.Ordinal.GetHashCode(Identity);
    public override string ToString() => Kind + ":" + Identity;
}

public sealed class EditorSession
{
    private EditorDocumentKey documentKey;

    internal EditorSession(global::DevInterface.DevUI owner)
    {
        Owner = owner;
        Selection = new EditorSelection();
        History = new History.EditorHistoryService(64);
        ToolMode = ResolveToolMode(owner?.activePage);
        Synchronize(owner);
    }

    public global::DevInterface.DevUI Owner { get; private set; }
    public EditorDocumentKey DocumentKey => documentKey;
    public EditorToolMode ToolMode { get; private set; }
    public EditorSelection Selection { get; }
    public History.EditorHistoryService History { get; }
    public bool FocusMode { get; private set; }
    public bool BrowserOpen { get; private set; } = true;
    public bool InspectorOpen { get; private set; } = true;
    public string ObjectSearch { get; set; } = string.Empty;

    public Room Room => Owner?.room;
    public RoomSettings RoomSettings => Room?.roomSettings;
    public World World => Owner?.game?.world;

    internal void Synchronize(global::DevInterface.DevUI owner)
    {
        Owner = owner;
        EditorDocumentKey next = ResolveDocument(owner);
        if (!next.Equals(documentKey))
        {
            documentKey = next;
            Selection.Clear();
            History.ActivateDocument(next);
        }

        // Legacy page changes are mirrored as a tool choice but do not reset room history.
        ToolMode = ResolveToolMode(owner?.activePage);
        Selection.RemoveMissing(RoomSettings?.placedObjects);
    }

    public void SetToolMode(EditorToolMode mode) => ToolMode = mode;
    public void ToggleFocusMode() => FocusMode = !FocusMode;
    public void SetFocusMode(bool value) => FocusMode = value;
    public void ToggleBrowser() => BrowserOpen = !BrowserOpen;
    public void ToggleInspector() => InspectorOpen = !InspectorOpen;

    private static EditorDocumentKey ResolveDocument(global::DevInterface.DevUI ui)
    {
        if (ui?.activePage is MapPage)
            return new EditorDocumentKey(EditorDocumentKind.RegionMap, ui.game?.world?.name ?? "<world>");
        if (ui?.activePage is RelationshipPage)
            return new EditorDocumentKey(EditorDocumentKind.Relationships, "global");

        Room room = ui?.room;
        string roomName = room?.abstractRoom?.name ?? room?.roomSettings?.name ?? "<room>";
        return new EditorDocumentKey(EditorDocumentKind.Room, roomName);
    }

    private static EditorToolMode ResolveToolMode(Page page)
    {
        if (page is ObjectsPage) return EditorToolMode.Objects;
        if (page is SoundPage) return EditorToolMode.Sound;
        if (page is TriggersPage) return EditorToolMode.Triggers;
        if (page is MapPage) return EditorToolMode.Map;
        if (page is DialogPage) return EditorToolMode.Dialog;
        if (page is RelationshipPage) return EditorToolMode.Relationships;
        return EditorToolMode.Room;
    }
}

public sealed class EditorSelection
{
    private readonly List<PlacedObject> placedObjects = new();

    public IReadOnlyList<PlacedObject> PlacedObjects => placedObjects;
    public PlacedObject PrimaryPlacedObject => placedObjects.Count == 0 ? null : placedObjects[placedObjects.Count - 1];
    public int Count => placedObjects.Count;

    public bool Contains(PlacedObject value) => value != null && placedObjects.Contains(value);

    public void SelectOnly(PlacedObject value)
    {
        placedObjects.Clear();
        if (value != null) placedObjects.Add(value);
    }

    public void Toggle(PlacedObject value)
    {
        if (value == null) return;
        if (!placedObjects.Remove(value)) placedObjects.Add(value);
    }

    public void Clear() => placedObjects.Clear();

    internal void RemoveMissing(List<PlacedObject> live)
    {
        if (live == null)
        {
            Clear();
            return;
        }
        placedObjects.RemoveAll(item => item == null || !live.Contains(item));
    }
}

public static class DevToolSessionHub
{
    private static ConditionalWeakTable<global::DevInterface.DevUI, EditorSession> sessions = new();
    private static WeakReference<EditorSession> current = new(null);

    public static EditorSession Current
    {
        get
        {
            current.TryGetTarget(out EditorSession session);
            return session;
        }
    }

    internal static void Synchronize(global::DevInterface.DevUI ui)
    {
        EditorSession session = sessions.GetValue(ui, key => new EditorSession(key));
        session.Synchronize(ui);
        current.SetTarget(session);
    }

    internal static void Reset()
    {
        sessions = new ConditionalWeakTable<global::DevInterface.DevUI, EditorSession>();
        current = new WeakReference<EditorSession>(null);
    }
}
