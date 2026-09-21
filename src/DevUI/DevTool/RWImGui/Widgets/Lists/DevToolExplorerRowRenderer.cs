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
