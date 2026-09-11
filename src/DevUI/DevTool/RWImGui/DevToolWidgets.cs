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
    private static readonly Num.Vector4 Accent = new(0.30f, 0.58f, 0.92f, 1f);
    private static readonly Num.Vector4 AccentSoft = new(0.19f, 0.38f, 0.62f, 0.78f);
    private static readonly Num.Vector4 AccentHover = new(0.27f, 0.50f, 0.80f, 0.92f);
    private static readonly Num.Vector4 AccentActive = new(0.34f, 0.63f, 0.98f, 1f);
    private static readonly Num.Vector4 Neutral = new(0.17f, 0.19f, 0.23f, 0.82f);
    private static readonly Num.Vector4 NeutralHover = new(0.24f, 0.27f, 0.32f, 0.92f);
    private static readonly Num.Vector4 Danger = new(0.57f, 0.20f, 0.22f, 0.88f);
    private static readonly Num.Vector4 DangerHover = new(0.74f, 0.27f, 0.29f, 0.96f);
    private static readonly Num.Vector4 Outline = new(0f, 0f, 0f, 1f);
    private static readonly Num.Vector4 Muted = new(0.68f, 0.72f, 0.78f, 1f);

    internal static void PaneTitle(string text)
    {
        ImGui.Spacing();
        DrawOutlinedText(text, Accent, 1.16f, 1.5f);
        ImGui.Separator();
        ImGui.Spacing();
    }

    internal static void SectionHeader(string text)
    {
        ImGui.Spacing();
        DrawOutlinedText(text, new Num.Vector4(0.78f, 0.86f, 1f, 1f), 1.10f, 1.25f);
        ImGui.Separator();
        ImGui.Spacing();
    }

    internal static void SourceHeader(string text, Num.Vector4 color, float fontScale = 1.38f)
    {
        ImGui.Spacing();
        DrawOutlinedText(text, color, fontScale, 2f);
        ImGui.Separator();
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
        return pressed;
    }

    internal static void MutedText(string text, bool wrapped = false)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        if (wrapped) ImGui.TextWrapped(text);
        else ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static void DrawOutlinedText(string text, Num.Vector4 color, float fontScale, float stroke)
    {
        ImGui.SetWindowFontScale(fontScale);
        Num.Vector2 pos = ImGui.GetCursorScreenPos();
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint outline = ImGui.GetColorU32(Outline);

        draw.AddText(pos + new Num.Vector2(-stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, 0f), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(0f, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, -stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(-stroke, stroke), outline, text);
        draw.AddText(pos + new Num.Vector2(stroke, stroke), outline, text);

        ImGui.TextColored(color, text);
        ImGui.SetWindowFontScale(1f);
    }
}
