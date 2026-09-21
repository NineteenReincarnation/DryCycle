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

using ImGuiNET;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared one-line explorer primitive for Browser lists.
///
/// The renderer owns only stable ImGui identity, selection focus and tooltip behavior. Page code
/// still owns filtering, ordering and the command triggered by a click. Generic dispatch keeps
/// struct-backed rows allocation-free while allowing richer page-specific row models.
/// </summary>
internal static class DevToolExplorerRowRenderer
{
    internal static bool DrawSelectable<TItem>(
        TItem item,
        bool selected,
        bool defaultFocus = false)
        where TItem : IDevToolExplorerListItem
    {
        ImGui.PushID(item.StableId ?? string.Empty);
        bool clicked = ImGui.Selectable(item.PrimaryText ?? string.Empty, selected);
        bool hovered = ImGui.IsItemHovered();
        if (selected && defaultFocus)
            ImGui.SetItemDefaultFocus();
        ImGui.PopID();

        string tooltip = item.Tooltip;
        if (hovered && !string.IsNullOrWhiteSpace(tooltip))
            DevToolTooltip.Show(tooltip);

        return clicked;
    }

    internal static void DrawReadOnly<TItem>(TItem item)
        where TItem : IDevToolExplorerListItem
    {
        ImGui.PushID(item.StableId ?? string.Empty);
        ImGui.TextUnformatted(item.PrimaryText ?? string.Empty);
        bool hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        string tooltip = item.Tooltip;
        if (hovered && !string.IsNullOrWhiteSpace(tooltip))
            DevToolTooltip.Show(tooltip);
    }
}
