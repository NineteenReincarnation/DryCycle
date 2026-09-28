using System;
using DryCycle.DevUI.DevTool.Compatibility;
using DryCycle.DevUI.DevTool.Core;
using ImGuiNET;
using Num = System.Numerics;

namespace DryCycle.DevUI.DevTool.RWImGui;

/// <summary>
/// Single home for developer-only diagnostics. Normal editing surfaces must not expose cache,
/// migration, revision, timing or backend implementation details; those belong here.
/// </summary>
internal static class DevToolDebugWorkspaceView
{
    private enum DebugSection
    {
        Overview,
        Performance,
        WorldMap,
        Compatibility,
        Frontend,
        LanceScavenger
    }

    private static DebugSection section = DebugSection.Overview;

    internal static void Draw(EditorPresentationSnapshot snapshot, Num.Vector2 display)
    {
        bool compatibilityActive = section == DebugSection.Compatibility;
        DevUiDiagnosticsPolicy.Enabled = compatibilityActive;

        // Expensive diagnostics are scoped to the category that exposes them. Leaving a category
        // immediately returns its instrumentation to the normal zero/low-overhead path.
        if (section != DebugSection.Performance && DevToolPerformanceMonitor.Enabled)
        {
            DevToolPerformanceMonitor.SetEnabled(false);
            DevToolFrontendPerformanceMonitor.SetEnabled(false);
        }

        if (section != DebugSection.LanceScavenger)
            LanceScavengerDebugView.StopCapture();

        float x = Math.Min(188f, Math.Max(8f, display.X * 0.12f));
        float y = 92f;
        float width = Math.Max(620f, display.X - x - 12f);
        float height = Math.Max(420f, display.Y - y - 12f);

        ImGui.SetNextWindowPos(new Num.Vector2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Num.Vector2(width, height), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(
            new Num.Vector2(Math.Min(620f, Math.Max(420f, display.X - 24f)), 360f),
            new Num.Vector2(Math.Max(620f, display.X - 16f), Math.Max(360f, display.Y - 16f)));
        ImGui.SetNextWindowBgAlpha(DevToolUiSettings.WindowAlpha);

        if (!ImGui.Begin(
                DevToolUiSettings.T("调试中心###DevToolDebugWorkspace", "Debug Center###DevToolDebugWorkspace"),
                ImGuiWindowFlags.NoCollapse |
                ImGuiWindowFlags.NoBringToFrontOnFocus))
        {
            ImGui.End();
            return;
        }

        FloatingWindowSnap.TrackCurrentWindow("DebugWorkspace");

        Num.Vector2 available = ImGui.GetContentRegionAvail();
        float navWidth = Math.Min(220f, Math.Max(170f, available.X * 0.18f));

        if (ImGui.BeginChild(
                "##DevToolDebugNavigation",
                new Num.Vector2(navWidth, available.Y),
                ImGuiChildFlags.Borders))
        {
            DevToolWidgets.PaneTitle(DevToolUiSettings.T("分类", "CATEGORIES"));
            DrawSectionButton(DebugSection.Overview, DevToolUiSettings.T("总览", "Overview"));
            DrawSectionButton(DebugSection.Performance, DevToolUiSettings.T("性能", "Performance"));
            DrawSectionButton(DebugSection.WorldMap, DevToolUiSettings.T("世界地图", "World Map"));
            DrawSectionButton(DebugSection.Compatibility, DevToolUiSettings.T("兼容 / 迁移", "Compatibility"));
            DrawSectionButton(DebugSection.Frontend, DevToolUiSettings.T("前端 / 字体", "Frontend / Font"));
            DrawSectionButton(DebugSection.LanceScavenger, DevToolUiSettings.T("长枪拾荒者", "Lance Scavenger"));

            ImGui.Spacing();
            ImGui.Separator();
            DevToolWidgets.MutedText(
                DevToolUiSettings.T(
                    "这里只有诊断与实现状态；正常编辑页面只保留可操作内容和必要错误。",
                    "Implementation diagnostics live here; normal editor pages keep only actionable controls and necessary errors."),
                true);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild(
                "##DevToolDebugContents",
                new Num.Vector2(0f, available.Y),
                ImGuiChildFlags.Borders))
        {
            DrawCurrentSection(snapshot);
            ScopedScrollChrome.Draw("DevToolDebugContents", pruneAfter: true);
        }
        ImGui.EndChild();

        ImGui.End();
    }

    internal static void Deactivate()
    {
        DevUiDiagnosticsPolicy.Enabled = false;
        if (DevToolPerformanceMonitor.Enabled)
        {
            DevToolPerformanceMonitor.SetEnabled(false);
            DevToolFrontendPerformanceMonitor.SetEnabled(false);
        }
        LanceScavengerDebugView.StopCapture();
    }

    internal static void ResetRetainedState()
    {
        section = DebugSection.Overview;
        Deactivate();
    }

    private static void DrawSectionButton(DebugSection target, string label)
    {
        if (DevToolWidgets.ActionButton(
                label,
                "DebugSection:" + target,
                section == target ? DevToolButtonTone.Primary : DevToolButtonTone.Subtle,
                fullWidth: true))
        {
            section = target;
        }
    }

    private static void DrawCurrentSection(EditorPresentationSnapshot snapshot)
    {
        switch (section)
        {
            case DebugSection.Performance:
                DrawHeading(DevToolUiSettings.T("性能诊断", "PERFORMANCE"));
                DevToolPerformanceWindow.DrawEmbedded();
                break;

            case DebugSection.WorldMap:
                DrawHeading(DevToolUiSettings.T("世界地图诊断", "WORLD MAP DIAGNOSTICS"));
                WorldMapRetainedV2Runtime.DrawDebugPanel();
                break;

            case DebugSection.Compatibility:
                DrawHeading(DevToolUiSettings.T("兼容与迁移诊断", "COMPATIBILITY / MIGRATION"));
                DevToolWidgets.MutedText(
                    DevToolUiSettings.T(
                        "该分类可见时才启用昂贵的 DevUI 运行时审计；离开后立即关闭。",
                        "Expensive DevUI runtime audits are enabled only while this category is visible."),
                    true);
                ImGui.Spacing();
                DevUiCompatibilityGateView.Draw();
                DevUiPageCoverageView.Draw();
                DevUiSemanticConformanceView.Draw();

                ImGui.Spacing();
                if (ImGui.CollapsingHeader(
                        DevToolUiSettings.T(
                            "Dialog 迁移边界",
                            "DIALOG MIGRATION BOUNDARY") +
                        "##DialogMigrationBoundary"))
                {
                    DevToolWidgets.MutedText(
                        DevToolUiSettings.T(
                            "原版 DialogPage 本质上是预览工具，而不是文本文件编辑器。新版工作区保留这个边界，不会从 DevTool 直接写入对话资源。",
                            "Vanilla DialogPage is a preview tool, not a text-file editor. The rebuilt workspace intentionally preserves that boundary rather than writing conversation resources from DevTool."),
                        true);
                }
                break;

            case DebugSection.Frontend:
                DrawFrontendDiagnostics();
                break;

            case DebugSection.LanceScavenger:
                DrawHeading(DevToolUiSettings.T("长枪拾荒者诊断", "LANCE SCAVENGER"));
                LanceScavengerDebugView.DrawEmbedded(snapshot);
                break;

            default:
                DrawOverview(snapshot);
                break;
        }
    }

    private static void DrawOverview(EditorPresentationSnapshot snapshot)
    {
        DrawHeading(DevToolUiSettings.T("调试总览", "DEBUG OVERVIEW"));

        DrawValue(DevToolUiSettings.T("文档", "Document"), snapshot?.Document);
        DrawValue(DevToolUiSettings.T("房间", "Room"), snapshot?.RoomName);
        DrawValue(
            DevToolUiSettings.T("工具模式", "Tool mode"),
            snapshot == null ? "-" : snapshot.ToolMode.ToString());
        DrawValue(
            DevToolUiSettings.T("DevTool 会话", "DevTool session"),
            DevToolSessionHub.IsCurrentSessionLive
                ? DevToolUiSettings.T("运行中", "Live")
                : DevToolUiSettings.T("未运行", "Inactive"));
        DrawValue(
            DevToolUiSettings.T("RWImGUI 后端", "RWImGUI backend"),
            DevToolFrontend.NativeBackendReady
                ? DevToolUiSettings.T("就绪", "Ready")
                : DevToolUiSettings.T("等待", "Waiting"));
        DrawValue(
            DevToolUiSettings.T("性能采样", "Performance sampling"),
            DevToolPerformanceMonitor.Enabled
                ? DevToolUiSettings.T("开启", "On")
                : DevToolUiSettings.T("关闭", "Off"));
        DrawValue(
            DevToolUiSettings.T("兼容审计", "Compatibility audit"),
            DevUiDiagnosticsPolicy.Enabled
                ? DevToolUiSettings.T("开启", "On")
                : DevToolUiSettings.T("关闭", "Off"));

        ImGui.Spacing();
        DevToolWidgets.MutedText(
            DevToolUiSettings.T(
                "需要看具体实现状态时，从左侧分类进入。普通房间、地图、声音、物件等页面不再显示这些内部统计。",
                "Choose a category for implementation details. Room, Map, Sound, Objects and other normal pages no longer expose these internal statistics."),
            true);
    }

    private static void DrawFrontendDiagnostics()
    {
        DrawHeading(DevToolUiSettings.T("前端 / 字体诊断", "FRONTEND / FONT"));

        DrawValue(
            DevToolUiSettings.T("后端", "Backend"),
            DevToolFrontend.NativeBackendReady
                ? DevToolUiSettings.T("就绪", "Ready")
                : DevToolUiSettings.T("等待 Present", "Waiting for Present"));
        DrawValue(
            DevToolUiSettings.T("语言", "Language"),
            DevToolUiSettings.Language.ToString());
        DrawValue(
            DevToolUiSettings.T("UI 缩放", "UI scale"),
            DevToolUiSettings.UiScale.ToString("0.00"));
        DrawValue(
            DevToolUiSettings.T("窗口透明度", "Window alpha"),
            DevToolUiSettings.WindowAlpha.ToString("0.00"));
        DrawValue(
            DevToolUiSettings.T("解析字体", "Resolved font"),
            string.IsNullOrEmpty(DevToolFrontend.ResolvedFontName)
                ? "-"
                : DevToolFrontend.ResolvedFontName);
        DrawValue(
            DevToolUiSettings.T("解析字重", "Resolved weight"),
            DevToolFrontend.ResolvedFontWeight.ToString());
        DrawValue(
            DevToolUiSettings.T("可用字重版本", "Weight variants"),
            DevToolFrontend.ResolvedFontWeightVariantCount.ToString());
    }

    private static void DrawHeading(string text)
    {
        ImGui.TextUnformatted(text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static void DrawValue(string label, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(Math.Max(170f, ImGui.GetWindowWidth() * 0.32f));
        ImGui.TextUnformatted(string.IsNullOrEmpty(value) ? "-" : value);
    }
}
