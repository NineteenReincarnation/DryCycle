using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared visual primitive for room browsers.
///
/// The component owns only the stable two-line room-entry layout. Shared identity/text comes from
/// IDevToolExplorerListItem; page-specific code still owns the trailing layer token, semantic color,
/// selection state and click action.
/// </summary>
internal static class DevToolRoomExplorerEntry
{
    internal const float DefaultHeight = 46f;

    /// <summary>
    /// Compatibility overload for existing room pages. It immediately projects the primitive
    /// arguments into the shared explorer data contract, so pages can migrate independently without
    /// changing visuals or interaction behavior.
    /// </summary>
    internal static bool Draw(
        string id,
        string roomName,
        string layerText,
        string statusText,
        string detailText,
        uint statusColor,
        bool selected,
        string tooltip = null,
        float height = DefaultHeight)
    {
        DevToolExplorerListItem item = new(
            id,
            roomName,
            detailText,
            statusText,
            tooltip);
        return Draw(item, layerText, statusColor, selected, height);
    }

    /// <summary>
    /// Contract-driven renderer. Generic dispatch lets page-owned structs implement the explorer
    /// contract without boxing or allocating one interface object per visible row.
    /// </summary>
    internal static bool Draw<TItem>(
        TItem item,
        string trailingText,
        uint statusColor,
        bool selected,
        float height = DefaultHeight)
        where TItem : IDevToolExplorerListItem
    {
        ImGui.PushID(item.StableId ?? string.Empty);
        bool clicked = ImGui.Selectable(
            "##RoomExplorerEntry",
            selected,
            ImGuiSelectableFlags.None,
            new Num.Vector2(0f, height));

        Num.Vector2 min = ImGui.GetItemRectMin();
        Num.Vector2 max = ImGui.GetItemRectMax();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();

        // Thin semantic strip: color helps scanning, text still carries the actual meaning.
        draw.AddRectFilled(
            new Num.Vector2(min.X + 2f, min.Y + 6f),
            new Num.Vector2(min.X + 5f, max.Y - 6f),
            statusColor);

        string primaryText = item.PrimaryText ?? string.Empty;
        Num.Vector2 namePos = min + new Num.Vector2(11f, 5f);
        draw.AddText(namePos, ImGui.GetColorU32(ImGuiCol.Text), primaryText);

        if (!string.IsNullOrEmpty(trailingText))
        {
            Num.Vector2 trailingSize = ImGui.CalcTextSize(trailingText);
            draw.AddText(
                new Num.Vector2(max.X - trailingSize.X - 8f, min.Y + 5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                trailingText);
        }

        string statusText = item.StatusText ?? string.Empty;
        string secondaryText = item.SecondaryText ?? string.Empty;
        Num.Vector2 metaPos = min + new Num.Vector2(11f, 25f);
        if (!string.IsNullOrEmpty(statusText))
            draw.AddText(metaPos, statusColor, statusText);

        if (!string.IsNullOrEmpty(secondaryText))
        {
            float statusWidth = string.IsNullOrEmpty(statusText) ? 0f : ImGui.CalcTextSize(statusText).X;
            string prefix = string.IsNullOrEmpty(statusText) ? string.Empty : " · ";
            draw.AddText(
                metaPos + new Num.Vector2(statusWidth, 0f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                prefix + secondaryText);
        }

        bool hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        string tooltip = item.Tooltip;
        if (hovered && !string.IsNullOrWhiteSpace(tooltip))
            DevToolTooltip.Show(tooltip);

        return clicked;
    }
}
