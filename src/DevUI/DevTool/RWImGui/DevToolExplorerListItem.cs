namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Data contract for reusable DevTool explorer entries.
///
/// The contract contains only shared identity and display metadata. Individual pages keep their
/// own rendering style and interaction rules.
/// </summary>
internal interface IDevToolExplorerListItem
{
    string StableId { get; }
    string PrimaryText { get; }
    string SecondaryText { get; }
    string StatusText { get; }
    string Tooltip { get; }
}
