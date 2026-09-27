using System;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

internal static class UiModeSwitch
{
    private static bool groupStatusFaulted;

    internal static void Draw()
    {
        // Apply one shared visual language before any rebuilt editor window is drawn this frame.
        DevToolUiTheme.Apply();

        Num.Vector2 display = ImGui.GetIO().DisplaySize;
        EditorPresentationSnapshot snapshot = EditorPresentationHub.Current;

        // Mode and language are global controls owned by the shared top bar. Keep that same bar
        // visible while Vanilla DevUI is primary so switching back never requires a separate panel.
        if (EditorUiModeState.UseVanilla)
        {
            DevToolTopStatusWindow.DrawGlobalOnly(
                snapshot,
                display);
            return;
        }

        // The renderer/context can be healthy before the core DevTool backend publishes its first
        // immutable snapshot. Keep the same global top bar visible during that short startup gap.
        if (!snapshot.Available)
        {
            DevToolTopStatusWindow.DrawGlobalOnly(
                snapshot,
                display);
            return;
        }

        // Group status is auxiliary UI. It must never stand between a healthy ImGui context
        // and the main editor overlay.
        if (!groupStatusFaulted &&
            (FloatingWindowSnap.SelectedWindowCount > 0 ||
             FloatingWindowSnap.GetGroupSnapshots().Length > 0))
        {
            try
            {
                GroupStatusWindow.Draw(display);
            }
            catch (Exception error)
            {
                groupStatusFaulted = true;
                global::DryCycle.Plugin.Logger?.LogError(
                    "DevTool group-status diagnostics failed and were isolated from the core UI shell. " +
                    error);
            }
        }

        // ActionToastOverlay is drawn once, after the main editor windows, by BridgePlugin.
        // Do not draw it here as well; duplicate pumping also duplicated the universal mirror.
    }
}
