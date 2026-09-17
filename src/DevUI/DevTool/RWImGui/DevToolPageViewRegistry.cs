using System;
using System.Collections.Generic;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Dialog;
using DryCycle.DevUI.DevTool.Map;
using DryCycle.DevUI.DevTool.Relationships;
using DryCycle.DevUI.DevTool.Room;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Rendering contract for one DevTool page.
///
/// This contract deliberately owns presentation only. Identity, lifecycle and retained-state ownership
/// live on IDevToolPage; the frontend registration object composes both contracts without forcing a
/// shared visual style on Browser or Inspector contents.
/// </summary>
internal interface IDevToolPageView
{
    bool UsesDedicatedWorkspace { get; }
    string LegacyFallbackTooltip { get; }

    bool SuppressInspector(EditorPresentationSnapshot snapshot);
    void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display);
    void DrawBrowser(EditorPresentationSnapshot snapshot);
    void DrawInspector(EditorPresentationSnapshot snapshot);
    void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display);
}

/// <summary>
/// One registered frontend page owns both its lifecycle and its rendering contract. Keeping the
/// composition here prevents Page and PageView from becoming two independently managed registries.
/// </summary>
internal interface IDevToolFrontendPage : IDevToolPage, IDevToolPageView
{
}

internal sealed class DelegateDevToolPage : IDevToolFrontendPage
{
    private readonly Func<EditorPresentationSnapshot, bool> suppressInspector;
    private readonly Action<EditorPresentationSnapshot, Num.Vector2> drawBackground;
    private readonly Action<EditorPresentationSnapshot> drawBrowser;
    private readonly Action<EditorPresentationSnapshot> drawInspector;
    private readonly Action<EditorPresentationSnapshot, Num.Vector2> drawWorkspace;
    private readonly Action activate;
    private readonly Action deactivate;
    private readonly Action reset;
    private readonly Func<string> legacyFallbackTooltip;
    private bool active;

    internal DelegateDevToolPage(
        string id,
        EditorToolMode mode,
        Action<EditorPresentationSnapshot> drawBrowser,
        Action<EditorPresentationSnapshot> drawInspector,
        bool usesDedicatedWorkspace = false,
        Action<EditorPresentationSnapshot, Num.Vector2> drawBackground = null,
        Action<EditorPresentationSnapshot, Num.Vector2> drawWorkspace = null,
        Func<EditorPresentationSnapshot, bool> suppressInspector = null,
        Func<string> legacyFallbackTooltip = null,
        Action activate = null,
        Action deactivate = null,
        Action reset = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Page id must not be empty.", nameof(id));

        Id = id.Trim();
        Mode = mode;
        UsesDedicatedWorkspace = usesDedicatedWorkspace;
        this.drawBrowser = drawBrowser;
        this.drawInspector = drawInspector;
        this.drawBackground = drawBackground;
        this.drawWorkspace = drawWorkspace;
        this.suppressInspector = suppressInspector;
        this.legacyFallbackTooltip = legacyFallbackTooltip;
        this.activate = activate;
        this.deactivate = deactivate;
        this.reset = reset;
    }

    public string Id { get; }
    public EditorToolMode Mode { get; }
    public bool UsesDedicatedWorkspace { get; }
    public string LegacyFallbackTooltip => legacyFallbackTooltip?.Invoke() ?? string.Empty;

    public bool SuppressInspector(EditorPresentationSnapshot snapshot) =>
        suppressInspector?.Invoke(snapshot) == true;

    public void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display) =>
        drawBackground?.Invoke(snapshot, display);

    public void DrawBrowser(EditorPresentationSnapshot snapshot) =>
        drawBrowser?.Invoke(snapshot);

    public void DrawInspector(EditorPresentationSnapshot snapshot) =>
        drawInspector?.Invoke(snapshot);

    public void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display) =>
        drawWorkspace?.Invoke(snapshot, display);

    public void Activate()
    {
        if (active) return;
        active = true;
        try
        {
            activate?.Invoke();
        }
        catch
        {
            active = false;
            throw;
        }
    }

    public void Deactivate()
    {
        if (!active) return;
        active = false;
        deactivate?.Invoke();
    }

    public void Reset() => reset?.Invoke();
}

/// <summary>
/// Single registration point for all rebuilt DevTool pages.
///
/// The registry owns one composite page object per ToolMode and one stable ID per page. Get() also
/// synchronizes lifecycle so existing render callers automatically participate in Activate/Deactivate
/// without another mode dispatch layer. The retained-view lifetime plugin mirrors ToolMode changes
/// while the frontend is not drawing, keeping lifecycle state correct in Vanilla/New-UI transitions.
/// </summary>
internal static class DevToolPageViewRegistry
{
    private static readonly Dictionary<EditorToolMode, IDevToolFrontendPage> Pages = new();
    private static readonly Dictionary<string, IDevToolFrontendPage> PagesById =
        new(StringComparer.OrdinalIgnoreCase);
    private static IDevToolFrontendPage activePage;

    static DevToolPageViewRegistry()
    {
        Register(new DelegateDevToolPage(
            "room",
            EditorToolMode.Room,
            _ => RoomSettingsView.DrawBrowser(RoomEditorPresentationHub.Current),
            _ => RoomSettingsView.DrawInspector(RoomEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于尚未迁移的模板、地形或自定义房间设置控件。",
                "Fallback for template, terrain or custom RoomSettings controls not migrated yet."),
            reset: RoomSettingsView.ResetRetainedState));

        Register(new DelegateDevToolPage(
            "objects",
            EditorToolMode.Objects,
            DevToolOverlay.DrawObjectsBrowser,
            snapshot => ObjectInspectorView.Draw(snapshot.Inspector),
            suppressInspector: snapshot => snapshot.Inspector?.HasSelection != true,
            reset: ObjectInspectorView.ResetRetainedState));

        Register(new DelegateDevToolPage(
            "sound",
            EditorToolMode.Sound,
            _ => SoundEditorView.DrawBrowser(SoundEditorPresentationHub.Current),
            _ => SoundEditorView.DrawInspector(SoundEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于未迁移的自定义声音页面控件。",
                "Fallback for custom SoundPage controls or mod-added sound tooling not migrated yet."),
            reset: () =>
            {
                SoundEditorView.ResetRetainedState();
                SoundLibraryGroupsView.ResetRetainedState();
            }));

        Register(new DelegateDevToolPage(
            "triggers",
            EditorToolMode.Triggers,
            _ => TriggerEditorView.DrawBrowser(TriggerEditorPresentationHub.Current),
            _ => TriggerEditorView.DrawInspector(TriggerEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于新检查器无法表达的自定义触发器/事件控件。",
                "Fallback for custom Trigger/TriggeredEvent controls not represented by the native inspector."),
            reset: TriggerEditorView.ResetRetainedState));

        Register(new DelegateDevToolPage(
            "map",
            EditorToolMode.Map,
            _ => MapEditorView.DrawBrowser(MapEditorPresentationHub.Current),
            _ => MapEditorView.DrawInspector(MapEditorPresentationHub.Current),
            usesDedicatedWorkspace: true,
            drawBackground: (snapshot, display) =>
            {
                if (snapshot.FocusMode)
                    DevToolOverlay.DrawMapCanvas(snapshot, display);
            },
            drawWorkspace: (snapshot, display) => WorldWorkspaceView.Draw(snapshot, display),
            reset: () =>
            {
                MapEditorView.ResetRetainedState();
                WorldWorkspaceView.ResetRetainedState();
            }));

        Register(new DelegateDevToolPage(
            "dialog",
            EditorToolMode.Dialog,
            _ => DialogEditorView.DrawBrowser(DialogEditorPresentationHub.Current),
            _ => DialogEditorView.DrawInspector(DialogEditorPresentationHub.Current),
            drawBackground: DevToolOverlay.DrawDialogPreview,
            reset: DialogEditorView.ResetRetainedState));

        Register(new DelegateDevToolPage(
            "relationships",
            EditorToolMode.Relationships,
            _ => RelationshipEditorView.DrawBrowser(RelationshipEditorPresentationHub.Current),
            _ => RelationshipEditorView.DrawInspector(RelationshipEditorPresentationHub.Current),
            drawBackground: DevToolOverlay.DrawRelationshipMatrix,
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于矩阵编辑器尚未表达的关系页面扩展。",
                "Fallback for custom RelationshipPage extensions not represented by the matrix editor."),
            reset: RelationshipEditorView.ResetRetainedState));
    }

    internal static IDevToolPageView Get(EditorToolMode mode)
    {
        SynchronizeActive(mode);
        Pages.TryGetValue(mode, out IDevToolFrontendPage page);
        return page;
    }

    internal static bool TryGet(string id, out IDevToolPageView view)
    {
        view = null;
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!PagesById.TryGetValue(id.Trim(), out IDevToolFrontendPage page)) return false;
        view = page;
        return true;
    }

    internal static string ActivePageId => activePage?.Id ?? string.Empty;

    internal static void SynchronizeActive(EditorToolMode mode)
    {
        Pages.TryGetValue(mode, out IDevToolFrontendPage next);
        if (ReferenceEquals(activePage, next)) return;

        IDevToolFrontendPage previous = activePage;
        activePage = null;
        previous?.Deactivate();

        if (next == null) return;
        next.Activate();
        activePage = next;
    }

    internal static void DeactivateActive()
    {
        IDevToolFrontendPage previous = activePage;
        activePage = null;
        previous?.Deactivate();
    }

    internal static void Register(IDevToolFrontendPage page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        if (string.IsNullOrWhiteSpace(page.Id)) throw new ArgumentException("Page id must not be empty.", nameof(page));
        if (Pages.ContainsKey(page.Mode))
            throw new InvalidOperationException("A DevTool page is already registered for mode " + page.Mode + ".");
        if (PagesById.ContainsKey(page.Id))
            throw new InvalidOperationException("A DevTool page is already registered with id '" + page.Id + "'.");

        Pages.Add(page.Mode, page);
        PagesById.Add(page.Id, page);
    }

    internal static void ResetAll()
    {
        DeactivateActive();
        foreach (IDevToolFrontendPage page in Pages.Values)
            page.Reset();
    }
}
