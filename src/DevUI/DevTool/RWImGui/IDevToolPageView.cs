using System;
using DryCycle.DevUI.DevTool.Core;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Rendering contract for one DevTool page.
///
/// This contract owns presentation only. Identity, lifecycle and retained-state ownership live on
/// IDevToolPage, keeping frontend drawing concerns separate from editor-page identity.
/// Navigation metadata also lives here because labels/tooltips/order are frontend presentation,
/// while the stable page ID and ToolMode remain lifecycle identity.
/// </summary>
internal interface IDevToolPageView
{
    int NavigationOrder { get; }
    string NavigationLabel { get; }
    string NavigationTooltip { get; }
    bool UsesDedicatedWorkspace { get; }
    bool SupportsSceneSurface { get; }
    string LegacyFallbackTooltip { get; }

    bool SuppressInspector(EditorPresentationSnapshot snapshot);
    void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display);
    void DrawBrowser(EditorPresentationSnapshot snapshot);
    void DrawInspector(EditorPresentationSnapshot snapshot);
    void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display);
    void DrawSceneWorkspace(EditorPresentationSnapshot snapshot);
}

/// <summary>
/// Composite contract used by the RWImGui registry. One object is the authoritative owner for both
/// page lifecycle and page rendering; the registry never has to keep parallel Page/PageView maps.
/// </summary>
internal interface IDevToolFrontendPage : IDevToolPage, IDevToolPageView
{
}

/// <summary>
/// Lifecycle-safe base class for built-in and future frontend pages.
///
/// Activate/Deactivate are idempotent. Activation rolls back its active flag when page-specific
/// startup fails, while deactivation marks the page inactive before invoking cleanup so a throwing
/// cleanup cannot leave the registry believing the old page is still active.
/// </summary>
internal abstract class DevToolFrontendPageBase : IDevToolFrontendPage
{
    private bool active;

    public abstract string Id { get; }
    public abstract EditorToolMode Mode { get; }
    public virtual int NavigationOrder => int.MaxValue;
    public virtual string NavigationLabel => Id;
    public virtual string NavigationTooltip => NavigationLabel;
    public virtual bool UsesDedicatedWorkspace => false;
    public virtual bool SupportsSceneSurface => false;
    public virtual string LegacyFallbackTooltip => string.Empty;

    public virtual bool SuppressInspector(EditorPresentationSnapshot snapshot) => false;
    public virtual void DrawBackground(EditorPresentationSnapshot snapshot, Num.Vector2 display) { }
    public abstract void DrawBrowser(EditorPresentationSnapshot snapshot);
    public abstract void DrawInspector(EditorPresentationSnapshot snapshot);
    public virtual void DrawWorkspace(EditorPresentationSnapshot snapshot, Num.Vector2 display) { }
    public virtual void DrawSceneWorkspace(EditorPresentationSnapshot snapshot) { }

    public void Activate()
    {
        if (active) return;
        active = true;
        try
        {
            OnActivate();
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
        OnDeactivate();
    }

    public void Reset() => OnReset();

    protected virtual void OnActivate() { }
    protected virtual void OnDeactivate() { }
    protected virtual void OnReset() { }
}
