using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class DevToolResponsiveText
{
    internal static string Ellipsize(string text, float maxWidth, out bool clipped)
    {
        text ??= string.Empty;
        clipped = false;
        if (text.Length == 0) return string.Empty;
        if (maxWidth <= 0f)
        {
            clipped = true;
            return string.Empty;
        }
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        const string ellipsis = "...";
        float ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;
        clipped = true;
        if (ellipsisWidth > maxWidth) return string.Empty;

        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text.Substring(0, mid) + ellipsis;
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }

        return low <= 0 ? ellipsis : text.Substring(0, low) + ellipsis;
    }
}

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
        return Draw(item, layerText, statusColor, selected, out _, height);
    }

    internal static bool Draw(
        string id,
        string roomName,
        string layerText,
        string statusText,
        string detailText,
        uint statusColor,
        bool selected,
        out bool doubleClicked,
        string tooltip = null,
        float height = DefaultHeight)
    {
        DevToolExplorerListItem item = new(
            id,
            roomName,
            detailText,
            statusText,
            tooltip);
        return Draw(item, layerText, statusColor, selected, out doubleClicked, height);
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
        where TItem : IDevToolExplorerListItem =>
        Draw(item, trailingText, statusColor, selected, out _, height);

    internal static bool Draw<TItem>(
        TItem item,
        string trailingText,
        uint statusColor,
        bool selected,
        out bool doubleClicked,
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
        const float rightPadding = 8f;
        const float textGap = 8f;

        float trailingWidth = 0f;
        if (!string.IsNullOrEmpty(trailingText))
        {
            Num.Vector2 trailingSize = ImGui.CalcTextSize(trailingText);
            trailingWidth = trailingSize.X;
            draw.AddText(
                new Num.Vector2(max.X - trailingSize.X - rightPadding, min.Y + 5f),
                ImGui.GetColorU32(ImGuiCol.TextDisabled),
                trailingText);
        }

        float primaryRight = max.X - rightPadding -
                             (trailingWidth > 0f ? trailingWidth + textGap : 0f);
        string renderedPrimary = DevToolResponsiveText.Ellipsize(
            primaryText,
            Math.Max(0f, primaryRight - namePos.X),
            out bool primaryClipped);
        if (!string.IsNullOrEmpty(renderedPrimary))
            draw.AddText(namePos, ImGui.GetColorU32(ImGuiCol.Text), renderedPrimary);

        string statusText = item.StatusText ?? string.Empty;
        string secondaryText = item.SecondaryText ?? string.Empty;
        Num.Vector2 metaPos = min + new Num.Vector2(11f, 25f);
        float metaWidth = Math.Max(0f, max.X - rightPadding - metaPos.X);
        bool metaClipped = false;

        string renderedStatus = DevToolResponsiveText.Ellipsize(
            statusText,
            metaWidth,
            out bool statusClipped);
        metaClipped |= statusClipped;
        float statusWidth = string.IsNullOrEmpty(renderedStatus) ? 0f : ImGui.CalcTextSize(renderedStatus).X;
        if (!string.IsNullOrEmpty(renderedStatus))
            draw.AddText(metaPos, statusColor, renderedStatus);

        if (!statusClipped && !string.IsNullOrEmpty(secondaryText))
        {
            string prefix = string.IsNullOrEmpty(renderedStatus) ? string.Empty : " | ";
            float prefixWidth = string.IsNullOrEmpty(prefix) ? 0f : ImGui.CalcTextSize(prefix).X;
            float secondaryWidth = Math.Max(0f, metaWidth - statusWidth - prefixWidth);
            string renderedSecondary = DevToolResponsiveText.Ellipsize(
                secondaryText,
                secondaryWidth,
                out bool secondaryClipped);
            metaClipped |= secondaryClipped;
            if (!string.IsNullOrEmpty(renderedSecondary))
            {
                draw.AddText(
                    metaPos + new Num.Vector2(statusWidth, 0f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    prefix + renderedSecondary);
            }
        }
        else if (statusClipped && !string.IsNullOrEmpty(secondaryText))
        {
            metaClipped = true;
        }

        bool hovered = ImGui.IsItemHovered();
        doubleClicked = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        ImGui.PopID();

        string tooltip = item.Tooltip;
        if (hovered && string.IsNullOrWhiteSpace(tooltip) && (primaryClipped || metaClipped))
        {
            tooltip = primaryText;
            if (!string.IsNullOrEmpty(statusText) || !string.IsNullOrEmpty(secondaryText))
            {
                string meta = statusText;
                if (!string.IsNullOrEmpty(statusText) && !string.IsNullOrEmpty(secondaryText))
                    meta += " | ";
                meta += secondaryText;
                if (!string.IsNullOrEmpty(meta))
                    tooltip += "\n" + meta;
            }
        }
        if (hovered && !string.IsNullOrWhiteSpace(tooltip))
            DevToolTooltip.Show(tooltip);

        return clicked;
    }
}
