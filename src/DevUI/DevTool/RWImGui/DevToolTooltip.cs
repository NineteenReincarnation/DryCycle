using System;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Draws rebuilt-editor help text away from Rain World's vanilla developer cursor.
/// The cursor itself is never hidden, replaced or repositioned.
/// </summary>
internal static class DevToolTooltip
{
    private const float OffsetX = 34f;
    private const float OffsetY = 26f;
    private const float PreferredWidth = 320f;

    internal static void Show(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        ImGuiIOPtr io = ImGui.GetIO();
        Num.Vector2 display = io.DisplaySize;
        Num.Vector2 mouse = io.MousePos;

        float x = mouse.X + OffsetX;
        float y = mouse.Y + OffsetY;

        // Flip the tooltip to the opposite side when the pointer is near a display edge.
        // This keeps both the Rain World cursor and the hovered world/object name unobscured.
        if (mouse.X > display.X * 0.68f)
            x = mouse.X - PreferredWidth - OffsetX;
        if (mouse.Y > display.Y * 0.72f)
            y = mouse.Y - 96f;

        x = Math.Max(8f, Math.Min(x, Math.Max(8f, display.X - PreferredWidth - 8f)));
        y = Math.Max(8f, Math.Min(y, Math.Max(8f, display.Y - 72f)));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.PopupAlpha);
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + PreferredWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
