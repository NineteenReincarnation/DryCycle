using System;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Sound;
using DryCycle.DevUI.DevTool.Triggers;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Dedicated Scene surface shared by Objects, Sound and Triggers. The underlying editor state and
/// command queues remain unchanged; this class only moves the scene list out of the Browser when the
/// global Scene placement preference is Center.
/// </summary>
internal static class SceneWorkspaceWindow
{
    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        if (snapshot == null || !snapshot.Available || snapshot.FocusMode ||
            !DevToolUiSettings.SceneInCenter || !ScenePlacementWindow.Supports(snapshot.ToolMode))
            return;

        float scale = Math.Max(0.78f, Math.Min(2.2f, DevToolUiSettings.UiScale));
        float width = Math.Min(
            Math.Max(500f, display.X * 0.34f),
            Math.Max(420f, Math.Min(760f * Math.Min(1.20f, scale), display.X - 32f)));
        float height = Math.Min(
            Math.Max(420f, display.Y * 0.56f),
            Math.Max(320f, Math.Min(680f * Math.Min(1.12f, scale), display.Y - 80f)));
        Num.Vector2 pos = new(
            Math.Max(8f, (display.X - width) * 0.5f),
            Math.Max(62f, (display.Y - height) * 0.52f));

        ImGui.SetNextWindowPos(pos, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(420f, 300f),
            new Num.Vector2(Math.Max(420f, display.X - 16f), Math.Max(300f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        string title = DevToolUiSettings.T("场景###DevToolSceneWorkspace", "Scene###DevToolSceneWorkspace");
        if (!ImGui.Begin(title, ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("SceneWorkspace");
        ImGui.SetWindowFontScale(DevToolUiSettings.IsChinese ? 1.18f : 1.12f);

        switch (snapshot.ToolMode)
        {
            case EditorToolMode.Objects:
                DevToolOverlay.DrawSceneObjectWorkspace(snapshot);
                break;
            case EditorToolMode.Sound:
                SoundEditorView.DrawSceneWorkspace(SoundEditorPresentationHub.Current);
                break;
            case EditorToolMode.Triggers:
                TriggerEditorView.DrawSceneWorkspace(TriggerEditorPresentationHub.Current);
                break;
        }

        ImGui.End();
    }
}
