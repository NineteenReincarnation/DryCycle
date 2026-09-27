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
        // Match the normal editor windows instead of forcing the top status surface more opaque.
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        // Shared chrome is intentionally a little larger than normal editor panels. It needs to be
        // readable against a busy room background without turning into another large workspace.
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowPadding,
            new Num.Vector2(13f, 9f));
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing,
            new Num.Vector2(9f, 7f));

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
            ImGui.PopStyleVar(2);
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

            // Make page-specific top actions easier to read/hit without changing their normal
            // appearance elsewhere in the editor.
            ImGui.SetWindowFontScale(1.14f);
            page.DrawTopControls(snapshot);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }
}
