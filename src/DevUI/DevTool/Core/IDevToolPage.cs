using System;

namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Common lifecycle contract for DevTool pages.
/// This interface intentionally does not define visual layout.
/// Existing pages keep their own ImGui rendering and styling.
/// Map pages may use this contract for the new workspace architecture.
/// </summary>
internal interface IDevToolPage
{
    /// <summary>
    /// Stable page identifier used by registration and state ownership.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Called when the page becomes active.
    /// </summary>
    void Activate();

    /// <summary>
    /// Called when the page leaves active state.
    /// </summary>
    void Deactivate();

    /// <summary>
    /// Clears retained state owned by this page.
    /// </summary>
    void Reset();
}
