namespace DryCycle.DevUI.DevTool.Core;

/// <summary>
/// Common lifecycle contract for one DevTool page.
///
/// The lifecycle contract is deliberately independent from rendering. A frontend may compose this
/// contract with its own view interface, but page activation, deactivation and retained-state reset
/// remain page responsibilities rather than drawing responsibilities.
/// </summary>
internal interface IDevToolPage
{
    /// <summary>
    /// Stable page identifier used by registration and state ownership.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Called once when the page becomes the active DevTool page.
    /// </summary>
    void Activate();

    /// <summary>
    /// Called once when the page leaves the active DevTool page.
    /// </summary>
    void Deactivate();

    /// <summary>
    /// Clears retained state owned by this page without changing editor document data.
    /// </summary>
    void Reset();
}
