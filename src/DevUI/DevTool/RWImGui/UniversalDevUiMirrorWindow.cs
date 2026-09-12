using System;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Movable frontend surface for the page-agnostic semantic mirror and its generic audit status.
/// It is intentionally separate from page-specific inspectors so the same compatibility path can
/// be exercised against every active vanilla/RK/DryCycle/third-party DevInterface page.
/// </summary>
internal static class UniversalDevUiMirrorWindow
{
    internal static void Draw(Num.Vector2 display)
    {
        // Map owns the same generic mirror inside its Inspector. A second floating copy only covers
        // the graph and creates duplicate controls, so the standalone audit surface stays hidden
        // while the Map workspace is active.
        if (EditorPresentationHub.Current.ToolMode == EditorToolMode.Map)
            return;

        DevUiGenericProtocolBootstrap.Ensure();
        UniversalDevUiPresentationSnapshot snapshot = UniversalDevUiPresentationHub.Current;
        if (!snapshot.Available) return;

        float width = Math.Min(520f, Math.Max(360f, display.X * 0.34f));
        float height = Math.Min(680f, Math.Max(320f, display.Y * 0.62f));
        float x = Math.Max(8f, display.X - width - 16f);
        float y = Math.Max(96f, Math.Min(display.Y - height - 8f, 180f));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(320f, 220f),
            new Num.Vector2(Math.Max(320f, display.X - 16f), Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("通用 DevUI###UniversalDevUiMirrorWindow", "Universal DevUI###UniversalDevUiMirrorWindow"),
                ImGuiWindowFlags.None))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("UniversalDevUiMirror");
        DevUiCompatibilityGateView.Draw(snapshot);
        DevUiPageCoverageView.Draw();
        DevUiSemanticConformanceView.Draw(snapshot);
        UniversalDevUiMirrorView.Draw(snapshot);
        ImGui.End();
    }
}
