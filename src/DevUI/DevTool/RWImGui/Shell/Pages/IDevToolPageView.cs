using System;
using DryCycle.DevUI.DevTool.Core;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Small semantic key used by a page to describe its Control Center session status.
/// The shared base class caches the formatted text against this key plus language so stable frames
/// do not rebuild status strings just because the Control Center is visible.
/// </summary>
internal readonly struct DevToolPageStatusState : IEquatable<DevToolPageStatusState>
{
    internal DevToolPageStatusState(
        int countA = 0,
        int countB = 0,
        bool flag = false,
        string textA = null,
        string textB = null)
    {
        CountA = countA;
        CountB = countB;
        Flag = flag;
        TextA = textA ?? string.Empty;
        TextB = textB ?? string.Empty;
    }

    internal int CountA { get; }
    internal int CountB { get; }
    internal bool Flag { get; }
    internal string TextA { get; }
    internal string TextB { get; }

    public bool Equals(DevToolPageStatusState other) =>
        CountA == other.CountA &&
        CountB == other.CountB &&
        Flag == other.Flag &&
        string.Equals(TextA, other.TextA, StringComparison.Ordinal) &&
        string.Equals(TextB, other.TextB, StringComparison.Ordinal);

    public override bool Equals(object obj) =>
        obj is DevToolPageStatusState other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = CountA;
            hash = hash * 397 ^ CountB;
            hash = hash * 397 ^ (Flag ? 1 : 0);
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(TextA);
            hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(TextB);
            return hash;
        }
    }
}

/// <summary>
/// Rendering contract for one DevTool page.
///
/// This contract owns presentation only. Identity, lifecycle and retained-state ownership live on
/// IDevToolPage, keeping frontend drawing concerns separate from editor-page identity.
/// Navigation metadata, shared-surface capabilities and session status live here so shared chrome
/// does not know concrete page implementations or read page-specific PresentationHub objects.
/// </summary>
internal interface IDevToolPageView
{
    int NavigationOrder { get; }
    string NavigationLabel { get; }
    string NavigationTooltip { get; }
    bool UsesDedicatedWorkspace { get; }
    bool SupportsSceneSurface { get; }
    bool AlwaysShowSceneSurface { get; }
    bool SupportsScenePlacement { get; }
    bool SupportsPlacementInput { get; }
    string LegacyFallbackTooltip { get; }

    string GetSessionStatus(EditorPresentationSnapshot snapshot);
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
    private bool statusProjectionValid;
    private bool statusProjectionChinese;
    private DevToolPageStatusState projectedStatus;
    private string projectedStatusText = string.Empty;

    public abstract string Id { get; }
    public abstract EditorToolMode Mode { get; }
    public virtual int NavigationOrder => int.MaxValue;
    public virtual string NavigationLabel => Id;
    public virtual string NavigationTooltip => NavigationLabel;
    public virtual bool UsesDedicatedWorkspace => false;
    public virtual bool SupportsSceneSurface => false;
    public virtual bool AlwaysShowSceneSurface => false;
    public virtual bool SupportsScenePlacement => SupportsSceneSurface;
    public virtual bool SupportsPlacementInput => false;
    public virtual string LegacyFallbackTooltip => string.Empty;

    public string GetSessionStatus(EditorPresentationSnapshot snapshot)
    {
        DevToolPageStatusState state = BuildSessionStatusState(snapshot);
        bool chinese = DevToolUiSettings.IsChinese;
        if (statusProjectionValid && statusProjectionChinese == chinese && projectedStatus.Equals(state))
            return projectedStatusText;

        statusProjectionValid = true;
        statusProjectionChinese = chinese;
        projectedStatus = state;
        projectedStatusText = FormatSessionStatus(state) ?? string.Empty;
        return projectedStatusText;
    }

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

    public void Reset()
    {
        statusProjectionValid = false;
        projectedStatusText = string.Empty;
        OnReset();
    }

    protected virtual DevToolPageStatusState BuildSessionStatusState(EditorPresentationSnapshot snapshot) =>
        new(textA: snapshot?.Document ?? string.Empty);

    protected virtual string FormatSessionStatus(DevToolPageStatusState state) => state.TextA;
    protected virtual void OnActivate() { }
    protected virtual void OnDeactivate() { }
    protected virtual void OnReset() { }
}
