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
    private static bool projectionValid;
    private static bool projectedChinese;
    private static float projectedScale;
    private static Num.Vector2 projectedDisplay;
    private static float projectedFontScale;
    private static Num.Vector2 projectedPosition;
    private static Num.Vector2 projectedSize;
    private static Num.Vector2 projectedMinSize;
    private static Num.Vector2 projectedMaxSize;
    private static string projectedTitle = string.Empty;
    private static string projectedCaption = string.Empty;
    private static string projectedLeft = string.Empty;
    private static string projectedCenter = string.Empty;

    internal static bool Supports(EditorToolMode mode) =>
        DevToolPageViewRegistry.SupportsSceneSurface(mode);

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available || snapshot.FocusMode || !Supports(snapshot.ToolMode))
            return;

        EnsureProjection(display);

        ImGui.SetNextWindowPos(projectedPosition, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(projectedSize, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(projectedMinSize, projectedMaxSize);
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(projectedTitle, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("ScenePlacement");
        ImGui.SetWindowFontScale(projectedFontScale);

        DevToolWidgets.MutedText(projectedCaption);
        bool left = DevToolUiSettings.ScenePlacement == DevToolScenePlacement.Left;
        if (DevToolWidgets.ActionButton(
                projectedLeft,
                "ScenePlacementLeft",
                left ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle))
        {
            DevToolUiSettings.ScenePlacement = DevToolScenePlacement.Left;
        }

        ImGui.SameLine();
        if (DevToolWidgets.ActionButton(
                projectedCenter,
                "ScenePlacementCenter",
                left ? DevToolButtonTone.Subtle : DevToolButtonTone.Primary))
        {
            DevToolUiSettings.ScenePlacement = DevToolScenePlacement.Center;
        }

        ImGui.End();
    }

    private static void EnsureProjection(Num.Vector2 display)
    {
        float scale = Math.Max(0.80f, Math.Min(2.2f, DevToolUiSettings.UiScale));
        bool chinese = DevToolUiSettings.IsChinese;
        if (projectionValid && projectedChinese == chinese &&
            Math.Abs(projectedScale - scale) < 0.0001f && projectedDisplay == display)
            return;

        projectionValid = true;
        projectedChinese = chinese;
        projectedScale = scale;
        projectedDisplay = display;

        float width = Math.Min(Math.Max(250f, 250f * Math.Min(1.35f, scale)), Math.Max(220f, display.X - 16f));
        float height = Math.Min(106f * Math.Min(1.20f, scale), Math.Max(86f, display.Y - 16f));
        float x = Math.Max(8f, (display.X - width) * 0.5f);
        float y = Math.Max(8f, display.Y - height - 8f);

        projectedFontScale = chinese ? 1.12f : 1.08f;
        projectedPosition = new Num.Vector2(x, y);
        projectedSize = new Num.Vector2(width, height);
        projectedMinSize = new Num.Vector2(220f, 82f);
        projectedMaxSize = new Num.Vector2(Math.Max(220f, display.X - 16f), Math.Max(82f, display.Y - 16f));
        projectedTitle = DevToolUiSettings.T("场景布局###DevToolScenePlacement", "Scene Layout###DevToolScenePlacement");
        projectedCaption = DevToolUiSettings.T("场景列表位置", "Scene list position");
        projectedLeft = DevToolUiSettings.T("左侧", "Left");
        projectedCenter = DevToolUiSettings.T("中间", "Center");
    }
}
