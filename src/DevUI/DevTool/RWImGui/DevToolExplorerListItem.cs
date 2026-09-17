namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Data contract for reusable DevTool explorer entries.
///
/// The contract contains only shared identity and display metadata. Individual pages keep their
/// own rendering style, selection semantics and actions.
/// </summary>
internal interface IDevToolExplorerListItem
{
    string StableId { get; }
    string PrimaryText { get; }
    string SecondaryText { get; }
    string StatusText { get; }
    string Tooltip { get; }
}

/// <summary>
/// Allocation-free default value object for explorer rows that do not need a page-specific model.
/// Page-owned row structs/classes may implement IDevToolExplorerListItem directly instead.
/// </summary>
internal readonly struct DevToolExplorerListItem : IDevToolExplorerListItem
{
    internal DevToolExplorerListItem(
        string stableId,
        string primaryText,
        string secondaryText = null,
        string statusText = null,
        string tooltip = null)
    {
        StableId = stableId ?? string.Empty;
        PrimaryText = primaryText ?? string.Empty;
        SecondaryText = secondaryText ?? string.Empty;
        StatusText = statusText ?? string.Empty;
        Tooltip = tooltip ?? string.Empty;
    }

    public string StableId { get; }
    public string PrimaryText { get; }
    public string SecondaryText { get; }
    public string StatusText { get; }
    public string Tooltip { get; }
}
