using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal enum DevToolButtonTone
{
    Normal,
    Primary,
    Danger,
    Subtle
}

/// <summary>
/// Shared visual language for the RWImGui editor.
/// Keep these widgets deliberately simple so they remain compatible with the
/// ImGui.NET version shipped by RWImGui.
/// </summary>
internal static class DevToolWidgets
{
    private enum FlowTitleLevel
    {
        Primary,
        Secondary,
        Tertiary
    }

    private static readonly Num.Vector4 Accent = new(0.30f, 0.58f, 0.92f, 1f);
    private static readonly Num.Vector4 AccentSoft = new(0.19f, 0.38f, 0.62f, 0.78f);
    private static readonly Num.Vector4 AccentHover = new(0.27f, 0.50f, 0.80f, 0.92f);
    private static readonly Num.Vector4 AccentActive = new(0.34f, 0.63f, 0.98f, 1f);
    private static readonly Num.Vector4 Neutral = new(0.17f, 0.19f, 0.23f, 0.82f);
    private static readonly Num.Vector4 NeutralHover = new(0.24f, 0.27f, 0.32f, 0.92f);
    private static readonly Num.Vector4 Danger = new(0.57f, 0.20f, 0.22f, 0.88f);
    private static readonly Num.Vector4 DangerHover = new(0.74f, 0.27f, 0.29f, 0.96f);
    private static readonly Num.Vector4 Muted = new(0.68f, 0.72f, 0.78f, 1f);

    // Static gold title hierarchy. The primary title is intentionally the brightest element in
    // the local information hierarchy; secondary/tertiary headings remain clearly readable over
    // Rain World's dark and mid-tone rooms instead of fading into the scene behind the editor.
    // The flowing title shader remains available elsewhere but is deliberately not applied here.
    private static readonly Num.Vector4 PrimaryGold = new(1.00f, 0.88f, 0.56f, 1f);
    private static readonly Num.Vector4 PrimaryGoldHighlight = new(1.00f, 0.97f, 0.82f, 0.96f);
    private static readonly Num.Vector4 PrimaryGoldRelief = new(0.31f, 0.17f, 0.045f, 0.96f);
    private static readonly Num.Vector4 SecondaryGold = new(0.96f, 0.79f, 0.45f, 1f);
    private static readonly Num.Vector4 SecondaryGoldRelief = new(0.32f, 0.18f, 0.055f, 0.82f);
    private static readonly Num.Vector4 TertiaryGold = new(0.90f, 0.72f, 0.40f, 1f);
    private static readonly Num.Vector4 TertiaryGoldRelief = new(0.28f, 0.16f, 0.05f, 0.74f);

    private const float PrimaryPaneTitleScale = 1.82f;
    // Inspector content used to fall back to 1.15, which made controls inside framed sections
    // visibly smaller than the Browser and the surrounding editor chrome. Keep both language
    // modes readable; Chinese gets the slightly larger body scale it needs for dense CJK glyphs.
    private const float InspectorPaneBodyScaleEnglish = 1.22f;
    private const float InspectorPaneBodyScaleChinese = 1.28f;
    private static float paneBodyScale = 1f;

    // RoomSettingsView historically exposed a button literally named "Switch" for the Effect
    // headers. Preserve its existing toggle command contract, but present the action users actually
    // get: collapse all on the first press, expand all on the next press.
    private static bool roomEffectsExpanded = true;

    internal static void PaneTitle(string text, float restoreScale = 1f)
    {
        bool primary = IsPrimaryPaneTitle(text);
        ImGui.Spacing();

        if (primary)
        {
            paneBodyScale = IsInspectorPaneTitle(text) ? InspectorPaneBodyScale() : restoreScale;
            DrawFlowingTitle(text, FlowTitleLevel.Primary, PrimaryPaneTitleScale * restoreScale, 2.0f, paneBodyScale);
            ImGui.Spacing();
            return;
        }

        float bodyScale = ResolvePaneBodyScale(restoreScale);
        DrawFlowingTitle(text, FlowTitleLevel.Secondary, 1.28f * bodyScale, 1.6f, bodyScale);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws the normal secondary pane title with one compact action pinned to the right edge.
    /// This is used for commands such as Collapse All so the action does not consume another
    /// vertical row in the inspector or obscure more of the room than necessary.
    /// </summary>
    internal static bool PaneTitleWithAction(
        string text,
        string actionLabel,
        string actionId,
        float restoreScale = 1f,
        DevToolButtonTone tone = DevToolButtonTone.Subtle)
    {
        bool roomEffectToggle = string.Equals(actionId, "RoomEffectsSwitchAll", StringComparison.Ordinal);
        if (roomEffectToggle)
        {
            actionLabel = roomEffectsExpanded
                ? DevToolUiSettings.T("折叠所有", "Collapse All")
                : DevToolUiSettings.T("展开所有", "Expand All");
        }

        bool primary = IsPrimaryPaneTitle(text);
        ImGui.Spacing();

        float titleRestoreScale;
        FlowTitleLevel level;
        float fontScale;
        float stroke;

        if (primary)
        {
            paneBodyScale = IsInspectorPaneTitle(text) ? InspectorPaneBodyScale() : restoreScale;
            titleRestoreScale = paneBodyScale;
            level = FlowTitleLevel.Primary;
            fontScale = PrimaryPaneTitleScale * restoreScale;
            stroke = 2.0f;
        }
        else
        {
            float bodyScale = ResolvePaneBodyScale(restoreScale);
            titleRestoreScale = bodyScale;
            level = FlowTitleLevel.Secondary;
            fontScale = 1.28f * bodyScale;
            stroke = 1.6f;
        }

        float rowStartX = ImGui.GetCursorPosX();
        float rowRightX = rowStartX + ImGui.GetContentRegionAvail().X;
        float buttonWidth = ButtonWidth(actionLabel);

        DrawFlowingTitle(text, level, fontScale, stroke, titleRestoreScale);

        ImGui.SameLine();
        float actionX = rowRightX - buttonWidth;
        if (actionX > rowStartX)
            ImGui.SetCursorPosX(actionX);
        bool pressed = ActionButton(actionLabel, actionId, tone);
        if (pressed && roomEffectToggle)
            roomEffectsExpanded = !roomEffectsExpanded;

        if (primary)
        {
            ImGui.Spacing();
        }
        else
        {
            ImGui.Separator();
            ImGui.Spacing();
        }

        return pressed;
    }

    internal static void SectionHeader(string text, float restoreScale = 1f)
    {
        float bodyScale = ResolvePaneBodyScale(restoreScale);
        ImGui.Spacing();
        DrawFlowingTitle(text, FlowTitleLevel.Tertiary, 1.18f * bodyScale, 1.4f, bodyScale);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Compatibility overload for existing browsers. Source colour is now resolved centrally from
    /// the source label so individual views can no longer drift into their own colour conventions.
    /// </summary>
    internal static void SourceHeader(string text, Num.Vector4 color, float fontScale = 1.38f, float restoreScale = 1f)
    {
        DevToolSourceMark source = DevToolSourcePresentation.FromLabel(text);
        SourceHeader(source, fontScale, restoreScale);
    }

    /// <summary>
    /// Preferred source-header API when a catalog has a stable source id/kind.
    /// </summary>
    internal static void SourceHeader(in DevToolSourceMark source, float fontScale = 1.38f, float restoreScale = 1f)
    {
        float bodyScale = ResolvePaneBodyScale(restoreScale);
        DevToolSourcePresentation.DrawHeader(source, fontScale, bodyScale);
    }

    internal static bool NavItem(string label, string id, bool active)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, active ? 1.5f : 0.5f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(10f, 6f));
        ImGui.PushStyleColor(ImGuiCol.Button, active ? AccentSoft : Neutral);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, active ? AccentHover : NeutralHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentActive);
        ImGui.PushStyleColor(ImGuiCol.Border, active ? Accent : new Num.Vector4(0.35f, 0.38f, 0.44f, 0.7f));

        bool pressed = ImGui.Button(label + "##" + id, new Num.Vector2(-1f, 0f));

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(3);
        return pressed;
    }

    internal static bool ActionButton(string label, string id, DevToolButtonTone tone = DevToolButtonTone.Normal, bool fullWidth = false)
    {
        // Objects used to own its Scene list inside the Browser. Sound and Triggers can suppress
        // their tab directly because their views are separate classes; Objects lives in the large
        // legacy overlay. Keep the global Center placement truly exclusive here: hide the duplicate
        // Scene button and force the Browser back to Library in the same frame that the placement
        // switch changes. The zero-size dummy preserves ImGui's SameLine flow expected by the old
        // caller without leaving a visible placeholder.
        bool objectSceneInCenter = DevToolUiSettings.SceneInCenter;
        if (objectSceneInCenter && string.Equals(id, "ObjectsSceneTab", StringComparison.Ordinal))
        {
            ImGui.Dummy(Num.Vector2.Zero);
            return false;
        }
        bool forceObjectsLibrary = objectSceneInCenter &&
                                   string.Equals(id, "ObjectsLibraryTab", StringComparison.Ordinal);

        label = StripInlineShortcutHint(label);

        Num.Vector4 normal;
        Num.Vector4 hovered;
        Num.Vector4 active;
        Num.Vector4 border;

        switch (tone)
        {
            case DevToolButtonTone.Primary:
                normal = AccentSoft;
                hovered = AccentHover;
                active = AccentActive;
                border = Accent;
                break;
            case DevToolButtonTone.Danger:
                normal = Danger;
                hovered = DangerHover;
                active = DangerHover;
                border = new Num.Vector4(0.88f, 0.34f, 0.36f, 1f);
                break;
            case DevToolButtonTone.Subtle:
                normal = new Num.Vector4(0.12f, 0.14f, 0.17f, 0.68f);
                hovered = NeutralHover;
                active = NeutralHover;
                border = new Num.Vector4(0.34f, 0.38f, 0.44f, 0.72f);
                break;
            default:
                normal = Neutral;
                hovered = NeutralHover;
                active = AccentSoft;
                border = new Num.Vector4(0.40f, 0.44f, 0.50f, 0.78f);
                break;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Num.Vector2(9f, 5f));
        ImGui.PushStyleColor(ImGuiCol.Button, normal);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, active);
        ImGui.PushStyleColor(ImGuiCol.Border, border);

        bool pressed = ImGui.Button(label + "##" + id, fullWidth ? new Num.Vector2(-1f, 0f) : new Num.Vector2(0f, 0f));

        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(3);
        return pressed || forceObjectsLibrary;
    }

    internal static void MutedText(string text, bool wrapped = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        if (wrapped) ImGui.TextWrapped(text);
        else ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    internal static bool FullWidthInputText(string label, string id, ref string value, uint maxLength)
    {
        MutedText(label);
        ImGui.SetNextItemWidth(-1f);
        return ImGui.InputText("##" + id, ref value, maxLength);
    }

    internal static float ButtonWidth(string label)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        return ImGui.CalcTextSize(StripInlineShortcutHint(label)).X + style.FramePadding.X * 2f;
    }

    internal static float RadioWidth(string label)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        return ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;
    }

    internal static bool SameLineIfFits(float nextItemWidth, float extraReserve = 0f)
    {
        ImGuiStylePtr style = ImGui.GetStyle();
        float nextX = ImGui.GetItemRectMax().X + style.ItemSpacing.X;
        float right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - style.WindowPadding.X - extraReserve;
        if (nextX + nextItemWidth > right) return false;
        ImGui.SameLine();
        return true;
    }

    private static string StripInlineShortcutHint(string label)
    {
        if (string.IsNullOrEmpty(label)) return label ?? string.Empty;

        int idStart = label.IndexOf("##", StringComparison.Ordinal);
        string visible = idStart >= 0 ? label.Substring(0, idStart) : label;
        string idSuffix = idStart >= 0 ? label.Substring(idStart) : string.Empty;

        string[] markers =
        {
            "  Ctrl+",
            "  Cmd+",
            "  Shift+",
            "  Alt+",
            "  Tab",
            "  Esc",
            "  Delete"
        };

        int cut = -1;
        for (int i = 0; i < markers.Length; i++)
        {
            int index = visible.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (cut < 0 || index < cut)) cut = index;
        }

        if (cut < 0) return label;
        return visible.Substring(0, cut).TrimEnd() + idSuffix;
    }

    private static float ResolvePaneBodyScale(float requestedRestoreScale)
    {
        return requestedRestoreScale == 1f ? paneBodyScale : requestedRestoreScale;
    }

    private static float InspectorPaneBodyScale() =>
        DevToolUiSettings.IsChinese ? InspectorPaneBodyScaleChinese : InspectorPaneBodyScaleEnglish;

    private static bool IsPrimaryPaneTitle(string text)
    {
        return text == "BROWSER" ||
               text == "INSPECTOR" ||
               text == "浏览器" ||
               text == "检查器";
    }

    private static bool IsInspectorPaneTitle(string text)
    {
        return text == "INSPECTOR" || text == "检查器";
    }

    private static void DrawFlowingTitle(
        string text,
        FlowTitleLevel level,
        float fontScale,
        float stroke,
        float restoreScale)
    {
        Num.Vector4 body;
        Num.Vector4 relief;

        switch (level)
        {
            case FlowTitleLevel.Primary:
                body = PrimaryGold;
                relief = PrimaryGoldRelief;
                break;
            case FlowTitleLevel.Secondary:
                body = SecondaryGold;
                relief = SecondaryGoldRelief;
                break;
            default:
                body = TertiaryGold;
                relief = TertiaryGoldRelief;
                break;
        }

        ImGui.SetWindowFontScale(fontScale);
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        const uint outline = 0xFF000000u;

        draw.AddText(pos + new Num.Vector2(-stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, stroke), outline, text);

        // Primary titles get a real two-sided relief: a pale upper-left ridge plus a warm dark
        // lower-right extrusion. Both are offset underneath the face so the result reads as raised
        // metal rather than glow, and remains legible on both bright and dark Rain World rooms.
        if (level == FlowTitleLevel.Primary)
            draw.AddText(pos + new Num.Vector2(-0.9f, -0.8f), ImGui.GetColorU32(PrimaryGoldHighlight), text);

        draw.AddText(
            pos + (level == FlowTitleLevel.Primary
                ? new Num.Vector2(1.15f, 1.35f)
                : new Num.Vector2(0.8f, 1.0f)),
            ImGui.GetColorU32(relief),
            text);

        ImGui.TextColored(body, text);
        ImGui.SetWindowFontScale(restoreScale);
    }

    private static Num.Vector4 BrightenSourceColor(Num.Vector4 color)
    {
        // Kept for binary/source compatibility with older local experiments; the active source
        // colour policy now lives in DevToolSourcePresentation.
        const float floor = 0.58f;
        const float lift = 0.20f;
        return new Num.Vector4(
            System.Math.Min(1f, System.Math.Max(floor, color.X + lift)),
            System.Math.Min(1f, System.Math.Max(floor, color.Y + lift)),
            System.Math.Min(1f, System.Math.Max(floor, color.Z + lift)),
            System.Math.Max(0.92f, color.W));
    }

    private static void DrawOutlinedText(string text, Num.Vector4 color, float fontScale, float stroke, float restoreScale)
    {
        ImGui.SetWindowFontScale(fontScale);
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        const uint outline = 0xFF000000u;

        draw.AddText(pos + new Num.Vector2(-stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, stroke), outline, text);

        ImGui.TextColored(color, text);
        ImGui.SetWindowFontScale(restoreScale);
    }
}
