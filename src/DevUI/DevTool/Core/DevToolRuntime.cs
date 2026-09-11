using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DevInterface;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.History;
using DryCycle.DevUI.DevTool.Input;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Objects;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Owns the lifetime of the new editor model. Vanilla DevInterface remains alive as a
/// compatibility backend; presentation is supplied by the optional RWImGui frontend.
/// DevTool does not discover, reference or call third-party mod APIs.
/// </summary>
internal static class DevToolRuntime
{
    private static bool enabled;

    internal static void Enable()
    {
        if (enabled) return;
        BuiltinInspectorAdapters.Enable();
        EditorInputRouter.Enable();
        On.DevInterface.DevUI.Update += DevUI_Update;
        enabled = true;
    }

    internal static void Disable()
    {
        if (!enabled) return;
        On.DevInterface.DevUI.Update -= DevUI_Update;
        LegacyUiPresentationController.Reset();
        EditorInputRouter.Disable();
        EditorUiCommandQueue.Clear();
        RoomEditorCommandQueue.Clear();
        SoundEditorCommandQueue.Clear();
        TriggerEditorCommandQueue.Clear();
        MapEditorCommandQueue.Clear();
        DialogEditorCommandQueue.Clear();
        RelationshipEditorCommandQueue.Clear();
        EditorPresentationHub.Clear();
        RoomEditorPresentationHub.Clear();
        SoundEditorPresentationHub.Clear();
        TriggerEditorPresentationHub.Clear();
        MapEditorPresentationHub.Clear();
        DialogEditorPresentationHub.Clear();
        RelationshipEditorPresentationHub.Clear();
        SoundEditorStateHub.Reset();
        TriggerEditorStateHub.Reset();
        MapEditorStateHub.Reset();
        DialogEditorStateHub.Reset();
        RelationshipEditorStateHub.Reset();
        DevToolSessionHub.Reset();
        enabled = false;
    }

    private static void DevUI_Update(On.DevInterface.DevUI.orig_Update orig, global::DevInterface.DevUI self)
    {
        if (self == null)
        {
            orig(self);
            return;
        }

        DevToolSessionHub.Synchronize(self);
        EditorSession session = DevToolSessionHub.Current;
        session?.LegacyTransactions.BeforeLegacyUpdate(session);

        EditorInputRouter.UpdateShortcuts(session);
        orig(self);

        session?.SynchronizeSelectionFromLegacyNode(self.draggedNode);
        session?.Synchronize(self);
        session?.LegacyTransactions.AfterLegacyUpdate(session);

        EditorUiCommandQueue.Process(session);
        RoomEditorCommandQueue.Process(session);
        SoundEditorCommandQueue.Process(session);
        TriggerEditorCommandQueue.Process(session);
        MapEditorCommandQueue.Process(session);
        DialogEditorCommandQueue.Process(session);
        RelationshipEditorCommandQueue.Process(session);
        session?.Synchronize(self);

        // New UI hides only the already-migrated screen controls. Vanilla mode restores the
        // complete original page while keeping the tiny frontend mode switch available.
        bool suppressMigratedLegacyUi =
            !EditorUiModeState.UseVanilla &&
            EditorInputRouter.FrontendAttached &&
            session != null &&
            session.LegacyUiVisible == false &&
            ((session.ToolMode == EditorToolMode.Objects && self.activePage is ObjectsPage) ||
             (session.ToolMode == EditorToolMode.Room && self.activePage is RoomSettingsPage) ||
             (session.ToolMode == EditorToolMode.Sound && self.activePage is SoundPage) ||
             (session.ToolMode == EditorToolMode.Triggers && self.activePage is TriggersPage) ||
             (session.ToolMode == EditorToolMode.Map && self.activePage is MapPage) ||
             (session.ToolMode == EditorToolMode.Dialog && self.activePage is DialogPage) ||
             (session.ToolMode == EditorToolMode.Relationships && self.activePage is RelationshipPage));
        LegacyUiPresentationController.Apply(self.activePage, suppressMigratedLegacyUi);

        EditorPresentationHub.Publish(session);
        RoomEditorPresentationHub.Publish(session);
        SoundEditorPresentationHub.Publish(session);
        TriggerEditorPresentationHub.Publish(session);
        MapEditorPresentationHub.Publish(session);
        DialogEditorPresentationHub.Publish(session);
        RelationshipEditorPresentationHub.Publish(session);
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
    private Page observedLegacyPage;

    internal EditorSession(global::DevInterface.DevUI owner)
    {
        Owner = owner;
        Selection = new EditorSelection();
        History = new EditorHistoryService(64);
        LegacyTransactions = new LegacyTransactionRecorder();
        observedLegacyPage = owner?.activePage;
        ToolMode = ResolveToolMode(observedLegacyPage);
        Synchronize(owner);
    }

    public global::DevInterface.DevUI Owner { get; private set; }
    public EditorDocumentKey DocumentKey => documentKey;
    public EditorToolMode ToolMode { get; private set; }
    public EditorSelection Selection { get; }
    public EditorHistoryService History { get; }
    public LegacyTransactionRecorder LegacyTransactions { get; }
    public bool FocusMode { get; private set; }
    public bool BrowserOpen { get; private set; } = true;
    public bool InspectorOpen { get; private set; } = true;
    public bool LegacyUiVisible { get; private set; }
    public bool PlacementActive => !string.IsNullOrEmpty(PlacementType);
    public string PlacementType { get; private set; } = string.Empty;
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
            LegacyTransactions.Reset();
            LegacyUiVisible = false;
            CancelPlacement();
        }

        if (!ReferenceEquals(observedLegacyPage, owner?.activePage))
        {
            observedLegacyPage = owner?.activePage;
            ToolMode = ResolveToolMode(observedLegacyPage);
            LegacyTransactions.Reset();
            LegacyUiVisible = false;
            if (ToolMode != EditorToolMode.Objects) CancelPlacement();
        }

        Selection.RemoveMissing(RoomSettings?.placedObjects);
    }

    public void SetToolMode(EditorToolMode mode)
    {
        if (mode != EditorToolMode.Objects) CancelPlacement();

        if (Owner == null)
        {
            ToolMode = mode;
            return;
        }

        int pageIndex = PageIndex(mode);
        if (pageIndex < 0)
        {
            ToolMode = mode;
            return;
        }

        if (ResolveToolMode(Owner.activePage) == mode)
        {
            ToolMode = mode;
            return;
        }

        LegacyUiPresentationController.Restore(Owner.activePage);
        LegacyTransactions.Reset();
        LegacyUiVisible = false;
        Owner.SwitchPage(pageIndex);
        observedLegacyPage = Owner.activePage;
        ToolMode = ResolveToolMode(observedLegacyPage);
    }

    public void BeginPlacement(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        if (ToolMode != EditorToolMode.Objects) SetToolMode(EditorToolMode.Objects);
        if (Owner?.draggedNode is Handle dragged) dragged.dragged = false;
        Owner.draggedNode = null;
        PlacementType = type;
    }

    public void CancelPlacement() => PlacementType = string.Empty;

    internal void SynchronizeSelectionFromLegacyNode(DevUINode node)
    {
        DevUINode current = node;
        while (current != null)
        {
            if (current is PlacedObjectRepresentation representation && representation.pObj != null)
            {
                Selection.SelectOnly(representation.pObj);
                return;
            }
            current = current.parentNode;
        }
    }

    public void ToggleFocusMode() => FocusMode = !FocusMode;
    public void SetFocusMode(bool value) => FocusMode = value;
    public void ToggleBrowser() => BrowserOpen = !BrowserOpen;
    public void ToggleInspector() => InspectorOpen = !InspectorOpen;

    public void ToggleLegacyUi()
    {
        if (ToolMode != EditorToolMode.Objects && ToolMode != EditorToolMode.Room &&
            ToolMode != EditorToolMode.Sound && ToolMode != EditorToolMode.Triggers &&
            ToolMode != EditorToolMode.Map && ToolMode != EditorToolMode.Relationships) return;
        LegacyUiVisible = !LegacyUiVisible;
        if (LegacyUiVisible)
            LegacyUiPresentationController.Restore(Owner?.activePage);
    }

    private static int PageIndex(EditorToolMode mode)
    {
        return mode switch
        {
            EditorToolMode.Room => 0,
            EditorToolMode.Objects => 1,
            EditorToolMode.Sound => 2,
            EditorToolMode.Map => 3,
            EditorToolMode.Triggers => 4,
            EditorToolMode.Dialog => 5,
            EditorToolMode.Relationships => 6,
            _ => -1
        };
    }

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

    public void SelectRange(IList<PlacedObject> live, int anchor, int target, bool additive)
    {
        if (live == null || live.Count == 0) return;
        anchor = Math.Max(0, Math.Min(anchor, live.Count - 1));
        target = Math.Max(0, Math.Min(target, live.Count - 1));
        if (!additive) placedObjects.Clear();

        int min = Math.Min(anchor, target);
        int max = Math.Max(anchor, target);
        for (int i = min; i <= max; i++)
        {
            PlacedObject item = live[i];
            if (item != null && !placedObjects.Contains(item)) placedObjects.Add(item);
        }
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
