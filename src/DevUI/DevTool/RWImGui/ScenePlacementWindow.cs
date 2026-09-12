using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// One global presentation switch for every editor that owns a Scene list.
/// The choice is intentionally frontend-only: it changes where the same scene data is shown,
/// never the room data itself.
/// </summary>
internal static class ScenePlacementWindow
{
    internal static bool Supports(EditorToolMode mode) =>
        mode == EditorToolMode.Objects ||
        mode == EditorToolMode.Sound ||
        mode == EditorToolMode.Triggers;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available || snapshot.FocusMode || !Supports(snapshot.ToolMode))
            return;

        float scale = Math.Max(0.80f, Math.Min(2.2f, DevToolUiSettings.UiScale));
        float width = Math.Min(Math.Max(250f, 250f * Math.Min(1.35f, scale)), Math.Max(220f, display.X - 16f));
        float height = Math.Min(106f * Math.Min(1.20f, scale), Math.Max(86f, display.Y - 16f));
        float x = Math.Max(8f, (display.X - width) * 0.5f);
        float y = Math.Max(8f, display.Y - height - 8f);

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(220f, 82f),
            new Num.Vector2(Math.Max(220f, display.X - 16f), Math.Max(82f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("场景布局###DevToolScenePlacement", "Scene Layout###DevToolScenePlacement"),
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("ScenePlacement");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.12f : 1.08f);

        DevToolWidgets.MutedText(DevToolUiSettings.T("场景列表位置", "Scene list position"));
        bool left = DevToolUiSettings.ScenePlacement == DevToolScenePlacement.Left;
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("左侧", "Left"),
                "ScenePlacementLeft",
                left ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            DevToolUiSettings.ScenePlacement = DevToolScenePlacement.Left;
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                DevToolUiSettings.T("中间", "Center"),
                "ScenePlacementCenter",
                left ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
        {
            DevToolUiSettings.ScenePlacement = DevToolScenePlacement.Center;
        }

        ImGui.End();
    }
}
