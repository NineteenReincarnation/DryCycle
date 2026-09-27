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

        float width =
            Math.Min(
                760f,
                Math.Max(
                    260f,
                    display.X - 420f));
        float x =
            Math.Max(
                8f,
                (display.X - width) * 0.5f);

        ImGui.SetNextWindowPos(
            new Num.Vector2(x, 8f),
            ImGuiCond.Always);
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

        string room =
            string.IsNullOrWhiteSpace(snapshot.RoomName)
                ? "-"
                : snapshot.RoomName;

        ImGui.TextUnformatted(
            room +
            " : NewDevtool Active");

        if (page?.HasTopControls == true)
        {
            ImGui.Separator();
            page.DrawTopControls(snapshot);
        }

        ImGui.End();
    }
}
