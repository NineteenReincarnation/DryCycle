using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Shared center Scene window chrome.
///
/// Pages opt in through IDevToolPageView.SupportsSceneSurface and render their own Scene content
/// through DrawSceneWorkspace. This class intentionally owns no page-specific retained state.
/// </summary>
internal static class SceneWorkspaceWindow
{
    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available || snapshot.FocusMode ||
            DevToolOverlay.SuppressesSharedPageSurfaces ||
            !DevToolPageViewRegistry.TryGet(snapshot.ToolMode, out IDevToolPageView page) ||
            !page.SupportsSceneSurface ||
            (!DevToolUiSettings.SceneInCenter && !page.AlwaysShowSceneSurface))
            return;

        float scale = Math.Max(0.78f, Math.Min(2.2f, DevToolUiSettings.UiScale));
        bool primaryWorkspace = page.AlwaysShowSceneSurface;
        float width = primaryWorkspace
            ? Math.Min(
                Math.Max(640f, display.X * 0.56f),
                Math.Max(560f, Math.Min(1040f * Math.Min(1.10f, scale), display.X - 32f)))
            : Math.Min(
                Math.Max(500f, display.X * 0.34f),
                Math.Max(420f, Math.Min(760f * Math.Min(1.20f, scale), display.X - 32f)));
        float height = primaryWorkspace
            ? Math.Min(
                Math.Max(460f, display.Y * 0.64f),
                Math.Max(360f, Math.Min(740f * Math.Min(1.08f, scale), display.Y - 80f)))
            : Math.Min(
                Math.Max(420f, display.Y * 0.56f),
                Math.Max(320f, Math.Min(680f * Math.Min(1.12f, scale), display.Y - 80f)));
        Num.Vector2 pos = new(
            Math.Max(8f, (display.X - width) * 0.5f),
            Math.Max(62f, (display.Y - height) * 0.52f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            primaryWorkspace ? new Num.Vector2(560f, 360f) : new Num.Vector2(420f, 300f),
            primaryWorkspace
                ? new Num.Vector2(Math.Max(560f, display.X - 16f), Math.Max(360f, display.Y - 16f))
                : new Num.Vector2(Math.Max(420f, display.X - 16f), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        string title = DevToolUiSettings.T("场景###DevToolSceneWorkspace", "Scene###DevToolSceneWorkspace");
        if (!ImGui.Begin(title, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("SceneWorkspace");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.12f);
        page.DrawSceneWorkspace(snapshot);
        ImGui.End();
    }
}
