using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared visual primitive for room browsers.
///
/// The component owns only the stable two-line room-entry layout. Page-specific code remains
/// responsible for deciding what status/detail text means, so World Map and Player Map can share
/// the same readable interaction without sharing backend semantics.
/// </summary>
internal static class DevToolRoomExplorerEntry
{
    internal const float DefaultHeight = 46f;

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
        ImGui.PushID(id ?? string.Empty);
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

        Num.Vector2 namePos = min + new Num.Vector2(11f, 5f);
        draw.AddText(namePos, ImGui.GetColorU32(ImGuiCol.Text), roomName ?? string.Empty);

        if (!string.IsNullOrEmpty(layerText))
        {
            Num.Vector2 layerSize = ImGui.CalcTextSize(layerText);
            draw.AddText(
                new Num.Vector2(max.X - layerSize.X - 8f, min.Y + 5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                layerText);
        }

        Num.Vector2 metaPos = min + new Num.Vector2(11f, 25f);
        if (!string.IsNullOrEmpty(statusText))
            draw.AddText(metaPos, statusColor, statusText);

        if (!string.IsNullOrEmpty(detailText))
        {
            float statusWidth = string.IsNullOrEmpty(statusText) ? 0f : ImGui.CalcTextSize(statusText).X;
            string prefix = string.IsNullOrEmpty(statusText) ? string.Empty : " · ";
            draw.AddText(
                metaPos + new Num.Vector2(statusWidth, 0f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                prefix + detailText);
        }

        bool hovered = ImGui.IsItemHovered();
        ImGui.PopID();

        if (hovered && !string.IsNullOrWhiteSpace(tooltip))
            DevToolTooltip.Show(tooltip);

        return clicked;
    }
}
