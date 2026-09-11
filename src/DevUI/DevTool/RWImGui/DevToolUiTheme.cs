using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared visual language for the rebuilt DevTool. This mutates the active ImGui style only;
/// it never enters editor data, RoomSettings or history.
/// </summary>
internal static class DevToolUiTheme
{
    private static readonly Num.Vector4 Accent = new(0.18f, 0.42f, 0.70f, 0.92f);
    private static readonly Num.Vector4 AccentHovered = new(0.24f, 0.53f, 0.86f, 0.96f);
    private static readonly Num.Vector4 AccentActive = new(0.30f, 0.62f, 0.98f, 1f);
    private static readonly Num.Vector4 Frame = new(0.08f, 0.11f, 0.15f, 0.76f);
    private static readonly Num.Vector4 FrameHovered = new(0.13f, 0.20f, 0.29f, 0.88f);
    private static readonly Num.Vector4 FrameActive = new(0.16f, 0.27f, 0.40f, 0.94f);
    private static readonly Num.Vector4 Child = new(0.025f, 0.035f, 0.050f, 0.36f);
    private static readonly Num.Vector4 Border = new(0.05f, 0.06f, 0.08f, 0.96f);
    private static readonly Num.Vector4 Separator = new(0.34f, 0.43f, 0.55f, 0.56f);

    /// <summary>
    /// Apply the standard style to the current RWImGui context. Safe to call every frame.
    /// </summary>
    internal static void Apply()
    {
        ImGuiStylePtr style = ImGui.GetStyle();

        style.WindowRounding = 2f;
        style.ChildRounding = 2f;
        style.FrameRounding = 2f;
        style.PopupRounding = 2f;
        style.ScrollbarRounding = 2f;
        style.GrabRounding = 2f;
        style.TabRounding = 2f;
        style.FrameBorderSize = 1f;
        style.ChildBorderSize = 1f;

        style.Colors[(int)ImGuiCol.ChildBg] = Child;
        style.Colors[(int)ImGuiCol.Border] = Border;
        style.Colors[(int)ImGuiCol.Separator] = Separator;
        style.Colors[(int)ImGuiCol.SeparatorHovered] = AccentHovered;
        style.Colors[(int)ImGuiCol.SeparatorActive] = AccentActive;

        style.Colors[(int)ImGuiCol.FrameBg] = Frame;
        style.Colors[(int)ImGuiCol.FrameBgHovered] = FrameHovered;
        style.Colors[(int)ImGuiCol.FrameBgActive] = FrameActive;

        style.Colors[(int)ImGuiCol.Button] = new Num.Vector4(0.11f, 0.24f, 0.39f, 0.88f);
        style.Colors[(int)ImGuiCol.ButtonHovered] = AccentHovered;
        style.Colors[(int)ImGuiCol.ButtonActive] = AccentActive;

        style.Colors[(int)ImGuiCol.Header] = Accent;
        style.Colors[(int)ImGuiCol.HeaderHovered] = AccentHovered;
        style.Colors[(int)ImGuiCol.HeaderActive] = AccentActive;

        style.Colors[(int)ImGuiCol.CheckMark] = new Num.Vector4(0.45f, 0.75f, 1f, 1f);
        style.Colors[(int)ImGuiCol.SliderGrab] = new Num.Vector4(0.34f, 0.62f, 0.90f, 0.95f);
        style.Colors[(int)ImGuiCol.SliderGrabActive] = new Num.Vector4(0.48f, 0.78f, 1f, 1f);
        style.Colors[(int)ImGuiCol.TextSelectedBg] = new Num.Vector4(0.18f, 0.48f, 0.82f, 0.60f);
    }
}
