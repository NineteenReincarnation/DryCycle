using System;
using DryCycle.DevUI.DevTool.Compatibility;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Movable frontend surface for the page-agnostic semantic mirror. It is intentionally separate
/// from page-specific inspectors so the same generic renderer can be exercised against every
/// active vanilla/RK/DryCycle/third-party DevInterface page during migration testing.
/// </summary>
internal static class UniversalDevUiMirrorWindow
{
    private static bool visible = true;

    internal static void Draw(Num.Vector2 display)
    {
        UniversalDevUiPresentationSnapshot snapshot = UniversalDevUiPresentationHub.Current;
        if (!snapshot.Available) return;

        LegacyControlSnapshot[] controls = snapshot.Controls ?? Array.Empty<LegacyControlSnapshot>();
        if (controls.Length == 0 && snapshot.UnmappedProtocolCount == 0) return;

        float width = Math.Min(520f, Math.Max(360f, display.X * 0.34f));
        float height = Math.Min(620f, Math.Max(300f, display.Y * 0.58f));
        float x = Math.Max(8f, display.X - width - 16f);
        float y = Math.Max(96f, Math.Min(display.Y - height - 8f, 180f));

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(320f, 220f),
            new Num.Vector2(Math.Max(320f, display.X - 16f), Math.Max(220f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!visible) return;
        bool open = visible;
        if (!ImGui.Begin(
                DevToolUiSettings.T("通用 DevUI###UniversalDevUiMirrorWindow", "Universal DevUI###UniversalDevUiMirrorWindow"),
                ref open,
                ImGuiWindowFlags.NoCollapse))
        {
            visible = open;
            ImGui.End();
            return;
        }

        visible = open;
        FloatingWindowSnap.TrackCurrentWindow("UniversalDevUiMirror");
        UniversalDevUiMirrorView.Draw(snapshot);
        ImGui.End();
    }
}
