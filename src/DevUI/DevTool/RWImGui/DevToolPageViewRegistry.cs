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
/// Defines the frontend contract for one DevTool page.
///
/// This contract deliberately owns dispatch only. It does not impose a common visual style on
/// Browser or Inspector contents: each page keeps its existing view implementation and styling.
/// Map is the exception by design: it owns a dedicated workspace through the same contract.
/// </summary>
internal interface IDevToolPageView
{
    EditorToolMode Mode { get; }
    bool UsesDedicatedWorkspace { get; }
    string LegacyFallbackTooltip { get; }

    bool SuppressInspector(EditorPresentationSnapshot snapshot);
    void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display);
    void DrawBrowser(EditorPresentationSnapshot snapshot);
    void DrawInspector(EditorPresentationSnapshot snapshot);
    void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display);
    void ResetRetainedState();
}

internal sealed class DelegateDevToolPageView : IDevToolPageView
{
    private readonly Func<EditorPresentationSnapshot, bool> suppressInspector;
    private readonly Action<EditorPresentationSnapshot, Num.Vector2> drawBackground;
    private readonly Action<EditorPresentationSnapshot> drawBrowser;
    private readonly Action<EditorPresentationSnapshot> drawInspector;
    private readonly Action<EditorPresentationSnapshot, Num.Vector2> drawWorkspace;
    private readonly Action resetRetainedState;
    private readonly Func<string> legacyFallbackTooltip;

    internal DelegateDevToolPageView(
        EditorToolMode mode,
        Action<EditorPresentationSnapshot> drawBrowser,
        Action<EditorPresentationSnapshot> drawInspector,
        bool usesDedicatedWorkspace = false,
        Action<EditorPresentationSnapshot, Num.Vector2> drawBackground = null,
        Action<EditorPresentationSnapshot, Num.Vector2> drawWorkspace = null,
        Func<EditorPresentationSnapshot, bool> suppressInspector = null,
        Func<string> legacyFallbackTooltip = null,
        Action resetRetainedState = null)
    {
        Mode = mode;
        UsesDedicatedWorkspace = usesDedicatedWorkspace;
        this.drawBrowser = drawBrowser;
        this.drawInspector = drawInspector;
        this.drawBackground = drawBackground;
        this.drawWorkspace = drawWorkspace;
        this.suppressInspector = suppressInspector;
        this.legacyFallbackTooltip = legacyFallbackTooltip;
        this.resetRetainedState = resetRetainedState;
    }

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

    public void ResetRetainedState() => resetRetainedState?.Invoke();
}

/// <summary>
/// Single registration point for all rebuilt DevTool page views.
/// Adding a page or changing its presentation policy should happen here instead of adding another
/// ToolMode branch to DevToolOverlay.
/// </summary>
internal static class DevToolPageViewRegistry
{
    private static readonly Dictionary<EditorToolMode, IDevToolPageView> Pages = new();

    static DevToolPageViewRegistry()
    {
        Register(new DelegateDevToolPageView(
            EditorToolMode.Room,
            _ => RoomSettingsView.DrawBrowser(RoomEditorPresentationHub.Current),
            _ => RoomSettingsView.DrawInspector(RoomEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于尚未迁移的模板、地形或自定义房间设置控件。",
                "Fallback for template, terrain or custom RoomSettings controls not migrated yet."),
            resetRetainedState: RoomSettingsView.ResetRetainedState));

        Register(new DelegateDevToolPageView(
            EditorToolMode.Objects,
            DevToolOverlay.DrawObjectsBrowser,
            snapshot => ObjectInspectorView.Draw(snapshot.Inspector),
            suppressInspector: snapshot => snapshot.Inspector?.HasSelection != true,
            resetRetainedState: ObjectInspectorView.ResetRetainedState));

        Register(new DelegateDevToolPageView(
            EditorToolMode.Sound,
            _ => SoundEditorView.DrawBrowser(SoundEditorPresentationHub.Current),
            _ => SoundEditorView.DrawInspector(SoundEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于未迁移的自定义声音页面控件。",
                "Fallback for custom SoundPage controls or mod-added sound tooling not migrated yet."),
            resetRetainedState: () =>
            {
                SoundEditorView.ResetRetainedState();
                SoundLibraryGroupsView.ResetRetainedState();
            }));

        Register(new DelegateDevToolPageView(
            EditorToolMode.Triggers,
            _ => TriggerEditorView.DrawBrowser(TriggerEditorPresentationHub.Current),
            _ => TriggerEditorView.DrawInspector(TriggerEditorPresentationHub.Current),
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于新检查器无法表达的自定义触发器/事件控件。",
                "Fallback for custom Trigger/TriggeredEvent controls not represented by the native inspector."),
            resetRetainedState: TriggerEditorView.ResetRetainedState));

        Register(new DelegateDevToolPageView(
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
            resetRetainedState: () =>
            {
                MapEditorView.ResetRetainedState();
                WorldWorkspaceView.ResetRetainedState();
            }));

        Register(new DelegateDevToolPageView(
            EditorToolMode.Dialog,
            _ => DialogEditorView.DrawBrowser(DialogEditorPresentationHub.Current),
            _ => DialogEditorView.DrawInspector(DialogEditorPresentationHub.Current),
            drawBackground: DevToolOverlay.DrawDialogPreview,
            resetRetainedState: DialogEditorView.ResetRetainedState));

        Register(new DelegateDevToolPageView(
            EditorToolMode.Relationships,
            _ => RelationshipEditorView.DrawBrowser(RelationshipEditorPresentationHub.Current),
            _ => RelationshipEditorView.DrawInspector(RelationshipEditorPresentationHub.Current),
            drawBackground: DevToolOverlay.DrawRelationshipMatrix,
            legacyFallbackTooltip: () => DevToolUiSettings.T(
                "用于矩阵编辑器尚未表达的关系页面扩展。",
                "Fallback for custom RelationshipPage extensions not represented by the matrix editor."),
            resetRetainedState: RelationshipEditorView.ResetRetainedState));
    }

    internal static IDevToolPageView Get(EditorToolMode mode)
    {
        Pages.TryGetValue(mode, out IDevToolPageView page);
        return page;
    }

    internal static void Register(IDevToolPageView page)
    {
        if (page == null) throw new ArgumentNullException(nameof(page));
        Pages[page.Mode] = page;
    }

    internal static void ResetAll()
    {
        foreach (IDevToolPageView page in Pages.Values)
            page.ResetRetainedState();
    }
}
