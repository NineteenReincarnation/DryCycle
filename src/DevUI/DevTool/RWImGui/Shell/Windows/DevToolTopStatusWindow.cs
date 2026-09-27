using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared top status surface for every rebuilt DevTool page.
///
/// The first row replaces Rain World's yellow "Dev tools active" label while New UI owns the
/// editor. The optional second row is page-owned and hosts only controls that are genuinely
/// specific to the active view.
/// </summary>
internal static class DevToolTopStatusWindow
{
    internal static void Draw(
        EditorPresentationSnapshot snapshot,
        IDevToolPageView page,
        Num.Vector2 display)
    {
        if (snapshot?.Available != true ||
            display.X <= 1f ||
            display.Y <= 1f)
            return;

        // Do not give this auto-sized shared chrome an estimated X before Begin. Its exact window
        // width is known only after ImGui has resolved the current contents; we center that real
        // window below against the full display width.
        ImGui.SetNextWindowBgAlpha(
            Math.Min(
                0.96f,
                DevToolUiSettings.WindowAlpha + 0.08f));

        ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse;

        if (!ImGui.Begin(
                "##DevToolTopStatus",
                flags))
        {
            ImGui.End();
            return;
        }

        // Center the already auto-sized window using its actual width every frame. This status
        // surface is shared chrome and intentionally not user-positionable.
        Num.Vector2 windowSize =
            ImGui.GetWindowSize();
        ImGui.SetWindowPos(
            new Num.Vector2(
                Math.Max(
                    8f,
                    (display.X - windowSize.X) * 0.5f),
                6f));

        string room =
            string.IsNullOrWhiteSpace(snapshot.RoomName)
                ? "-"
                : snapshot.RoomName;

        DevToolWidgets.CenteredPrimaryTitle(
            room +
            " : NewDevtool Active",
            1.68f);

        if (page?.HasTopControls == true)
        {
            ImGui.Separator();
            page.DrawTopControls(snapshot);
        }

        ImGui.End();
    }
}
