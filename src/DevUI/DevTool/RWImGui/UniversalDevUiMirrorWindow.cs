using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Standalone movable diagnostics surface for the page-agnostic semantic mirror and migration audits.
/// It deliberately stays outside page-specific inspectors and normal editor workflows.
/// </summary>
internal static class UniversalDevUiMirrorWindow
{
    internal static void Draw(Num.Vector2 display)
    {
        // This window used to be pumped from ActionToastOverlay on every frontend frame. Its body
        // performs reflection-backed mirror capture, semantic validation, page coverage traversal
        // and a loaded-type inventory, which made the first O/H frame compete with vanilla DevUI
        // construction and RWImGui context activation. Keep the diagnostic implementation intact,
        // but make it explicitly opt-in so production editor interaction never pays for auditing.
        if (!DevUiDiagnosticsPolicy.Enabled)
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
                DevToolUiSettings.T("DevUI 兼容性诊断###UniversalDevUiMirrorWindow", "DevUI Compatibility Diagnostics###UniversalDevUiMirrorWindow"),
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
